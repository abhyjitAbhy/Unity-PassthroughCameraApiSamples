// Copyright (c) Meta Platforms, Inc. and affiliates.

using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Meta.XR;
using Meta.XR.Samples;
using Unity.Collections;
using Unity.InferenceEngine;
using UnityEngine;

namespace PassthroughCameraSamples.MultiObjectDetection
{
    [MetaCodeSample("PassthroughCameraApiSamples-MultiObjectDetection")]
    public class SentisInferenceRunManager : MonoBehaviour
    {
        [SerializeField] private PassthroughCameraAccess m_cameraAccess;
        [SerializeField] private DetectionUiMenuManager m_uiMenuManager;
        [SerializeField] private DetectionManager m_detectionManager;

        [Header("Sentis Model config")]
        [SerializeField] private BackendType m_backend = BackendType.CPU;
        [SerializeField] private ModelAsset m_sentisModel;
        [SerializeField] private TextAsset m_labelsAsset;
        [SerializeField, Range(0, 1)] private float m_iouThreshold = 0.6f;
        [SerializeField, Range(0, 1)] private float m_scoreThreshold = 0.35f;

        [Header("Temporal Stabilisation")]
        [Tooltip("Frames a detection must appear consecutively before being shown. 3-4 recommended.")]
        [SerializeField, Range(1, 8)] private int m_confirmFrames = 3;

        [Tooltip("IoU overlap required between frames to count as the same detection.")]
        [SerializeField, Range(0.1f, 0.8f)] private float m_temporalIoU = 0.25f;

        [Tooltip("Frames a confirmed detection can go missing before it is dropped.")]
        [SerializeField, Range(1, 10)] private int m_maxMissedFrames = 4;

        [Header("UI display references")]
        [SerializeField] private SentisInferenceUiManager m_uiInference;
        [SerializeField] private Box3DManager m_box3DManager;

        [Header("[Editor Only] Convert to Sentis")]
        public ModelAsset OnnxModel;
        [Space(40)]

        // ── SmartMove household object filter ────────────────────────────────
        //
        // Only these class IDs are tracked or displayed. Everything else
        // (people, vehicles, animals, food, sports gear…) is dropped inside
        // NMS — zero cost downstream.
        //
        // IDs match the label file shipped with this Sentis YOLOv8 model:
        //
        //   ID  Label (as in .txt file)   Common name
        //   ──  ──────────────────────    ───────────
        //   56  chair                     Chair
        //   57  sofa                      Sofa / couch
        //   58  pottedplant               Potted plant
        //   59  bed                       Bed
        //   60  diningtable               Dining table
        //   61  toilet                    Toilet
        //   62  tvmonitor                 TV / monitor
        //   63  laptop                    Laptop
        //   68  microwave                 Microwave
        //   69  oven                      Oven
        //   70  toaster                   Toaster
        //   71  sink                      Sink
        //   72  refrigerator              Refrigerator
        //   73  book                      Book
        //   74  clock                     Clock
        //   75  vase                      Vase
        //   28  suitcase                  Suitcase / moving box
        //
        // To add an object: look up its index in the label .txt file
        // (0-based line number) and add it here. No retraining needed —
        // the model already detects all 80 COCO classes; this is just a filter.
        private static readonly HashSet<int> k_householdIds = new HashSet<int>
        {
            56,  // chair
            57,  // sofa
            58,  // pottedplant
            59,  // bed
            60,  // diningtable
            61,  // toilet
            62,  // tvmonitor
            63,  // laptop
            68,  // microwave
            69,  // oven
            70,  // toaster
            71,  // sink
            72,  // refrigerator
            73,  // book
            74,  // clock
            75,  // vase
            28,  // suitcase
        };

        private Worker m_engine;
        private Vector2Int m_inputSize;

        private readonly List<(int classId, Vector4 boundingBox)> m_rawDetections = new();

        // ── Temporal tracker ─────────────────────────────────────────────────
        private class TrackedDetection
        {
            public int ClassId;
            public Vector4 Box;         // EMA-smoothed bbox in model-input pixels
            public int HitCount;
            public int MissCount;
            public bool Confirmed;
        }

        private readonly List<TrackedDetection> m_tracked = new();
        private readonly List<(int classId, Vector4 boundingBox)> m_stableDetections = new();

        private void Awake()
        {
            var model = ModelLoader.Load(m_sentisModel);
            var inputShape = model.inputs[0].shape;
            m_inputSize = new Vector2Int(inputShape.Get(2), inputShape.Get(3));
            m_engine = new Worker(model, m_backend);
        }

        private IEnumerator Start()
        {
            m_uiInference.SetLabels(m_labelsAsset);
            m_box3DManager?.SetLabels(m_labelsAsset);

            // Tell Box3DManager to track only household objects so even if a
            // detection somehow slips through NMS it is still filtered at render.
            m_box3DManager?.SetFilter(k_householdIds);

            while (true)
            {
                while (m_uiMenuManager.IsPaused)
                    yield return null;

                yield return RunInference();
            }
        }

        private void OnDestroy()
        {
            m_engine.PeekOutput(0)?.CompleteAllPendingOperations();
            m_engine.PeekOutput(1)?.CompleteAllPendingOperations();
            m_engine.PeekOutput(2)?.CompleteAllPendingOperations();
            m_engine.Dispose();
        }

        internal static void PreloadModel(ModelAsset modelAsset)
        {
            var model = ModelLoader.Load(modelAsset);
            var inputShape = model.inputs[0].shape;

            using var worker = new Worker(model, BackendType.CPU);

            Texture tempTexture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            var textureTransform = new TextureTransform().SetDimensions(tempTexture.width, tempTexture.height, 3);
            using var input = new Tensor<float>(new TensorShape(1, 3, inputShape.Get(2), inputShape.Get(3)));
            TextureConverter.ToTensor(tempTexture, input, textureTransform);
            worker.Schedule(input);

            worker.PeekOutput(0).CompleteAllPendingOperations();
            worker.PeekOutput(1).CompleteAllPendingOperations();
            worker.PeekOutput(2).CompleteAllPendingOperations();
            Destroy(tempTexture);
        }

        private IEnumerator RunInference()
        {
            if (!m_cameraAccess.IsPlaying)
                yield break;

            [DllImport("OVRPlugin", CallingConvention = CallingConvention.Cdecl)]
            static extern OVRPlugin.Result ovrp_GetNodePoseStateAtTime(
                double time, OVRPlugin.Node nodeId, out OVRPlugin.PoseStatef nodePoseState);

            if (!ovrp_GetNodePoseStateAtTime(
                    OVRPlugin.GetTimeInSeconds(), OVRPlugin.Node.Head, out _).IsSuccess())
            {
                Debug.Log("ovrp_GetNodePoseStateAtTime failed, skipping.");
                yield break;
            }

            var cachedCameraPose = m_cameraAccess.GetCameraPose();
            Texture targetTexture = m_cameraAccess.GetTexture();

            var textureTransform = new TextureTransform().SetDimensions(targetTexture.width, targetTexture.height, 3);
            using var input = new Tensor<float>(new TensorShape(1, 3, m_inputSize.x, m_inputSize.y));
            TextureConverter.ToTensor(targetTexture, input, textureTransform);
            m_engine.Schedule(input);

            var boxesAwaiter = (m_engine.PeekOutput(0) as Tensor<float>).ReadbackAndCloneAsync().GetAwaiter();
            while (!boxesAwaiter.IsCompleted) yield return null;
            using var boxes = boxesAwaiter.GetResult();
            if (boxes.shape[0] == 0) { UpdateTemporalTracker(null); yield break; }

            var classIDsAwaiter = (m_engine.PeekOutput(1) as Tensor<int>).ReadbackAndCloneAsync().GetAwaiter();
            while (!classIDsAwaiter.IsCompleted) yield return null;
            using var classIDs = classIDsAwaiter.GetResult();
            if (classIDs.shape[0] == 0) { UpdateTemporalTracker(null); yield break; }

            var scoresAwaiter = (m_engine.PeekOutput(2) as Tensor<float>).ReadbackAndCloneAsync().GetAwaiter();
            while (!scoresAwaiter.IsCompleted) yield return null;
            using var scores = scoresAwaiter.GetResult();
            if (scores.shape[0] == 0) { UpdateTemporalTracker(null); yield break; }

            // NMS — household filter applied inside, irrelevant classes never enter the tracker
            NonMaxSuppression(m_rawDetections, boxes, classIDs, scores, m_iouThreshold, m_scoreThreshold);

            UpdateTemporalTracker(m_rawDetections);
            BuildStableList();

            if (!m_cameraAccess.IsPlaying ||
                m_detectionManager.m_spatialAnchor == null ||
                !m_detectionManager.m_spatialAnchor.IsTracked)
                yield break;

            m_uiInference.DrawUIBoxes(m_stableDetections, m_inputSize, cachedCameraPose);
            m_box3DManager?.Draw3DBoxes(m_stableDetections, m_inputSize, cachedCameraPose);
        }

        // ── Temporal Tracker ─────────────────────────────────────────────────

        private void UpdateTemporalTracker(List<(int classId, Vector4 boundingBox)> rawFrame)
        {
            bool[] matched = new bool[m_tracked.Count];

            if (rawFrame != null)
            {
                foreach (var det in rawFrame)
                {
                    int bestIdx = -1;
                    float bestIoU = m_temporalIoU;

                    for (int i = 0; i < m_tracked.Count; i++)
                    {
                        if (m_tracked[i].ClassId != det.classId) continue;
                        float iou = CalculateIoU(m_tracked[i].Box, det.boundingBox);
                        if (iou > bestIoU) { bestIoU = iou; bestIdx = i; }
                    }

                    if (bestIdx >= 0)
                    {
                        var t = m_tracked[bestIdx];
                        const float alpha = 0.4f;
                        t.Box = Vector4.Lerp(t.Box, det.boundingBox, alpha);
                        t.HitCount = Mathf.Min(t.HitCount + 1, m_confirmFrames + 2);
                        t.MissCount = 0;
                        if (t.HitCount >= m_confirmFrames) t.Confirmed = true;
                        matched[bestIdx] = true;
                    }
                    else
                    {
                        m_tracked.Add(new TrackedDetection
                        {
                            ClassId = det.classId,
                            Box = det.boundingBox,
                            HitCount = 1,
                            MissCount = 0,
                            Confirmed = m_confirmFrames <= 1
                        });
                        System.Array.Resize(ref matched, m_tracked.Count);
                        matched[m_tracked.Count - 1] = true;
                    }
                }
            }

            for (int i = m_tracked.Count - 1; i >= 0; i--)
            {
                if (!matched[i])
                {
                    m_tracked[i].MissCount++;
                    if (m_tracked[i].MissCount > m_maxMissedFrames)
                        m_tracked.RemoveAt(i);
                }
            }
        }

        private void BuildStableList()
        {
            m_stableDetections.Clear();
            foreach (var t in m_tracked)
                if (t.Confirmed)
                    m_stableDetections.Add((t.ClassId, t.Box));
        }

        // ── NMS with household class filter ──────────────────────────────────
        //
        // Detections whose classId is NOT in k_householdIds are skipped before
        // they are ever added to outDetections. This means the temporal tracker,
        // UI, and 3D box system never see people, vehicles, food, animals, etc.

        private static void NonMaxSuppression(
            List<(int classId, Vector4 boundingBox)> outDetections,
            Tensor<float> boxes,
            Tensor<int> classIDs,
            Tensor<float> scores,
            float iouThreshold,
            float scoreThreshold)
        {
            outDetections.Clear();

            var filteredIndices = new List<int>();
            NativeArray<float>.ReadOnly scoresArray = scores.AsReadOnlyNativeArray();

            for (int i = 0; i < scoresArray.Length; i++)
            {
                // Reject low-confidence detections AND non-household classes
                // in a single pass — no wasted work downstream.
                if (scoresArray[i] >= scoreThreshold && k_householdIds.Contains(classIDs[i]))
                    filteredIndices.Add(i);
            }

            if (filteredIndices.Count == 0) return;

            filteredIndices.Sort((a, b) => scoresArray[b].CompareTo(scoresArray[a]));

            bool[] suppressed = new bool[filteredIndices.Count];
            for (int i = 0; i < filteredIndices.Count; i++)
            {
                if (suppressed[i]) continue;
                int idx = filteredIndices[i];
                outDetections.Add((classIDs[idx], GetBox(idx)));

                for (int j = i + 1; j < filteredIndices.Count; j++)
                {
                    if (suppressed[j]) continue;
                    int jdx = filteredIndices[j];
                    if (CalculateIoU(GetBox(idx), GetBox(jdx)) > iouThreshold)
                        suppressed[j] = true;
                }
            }

            Vector4 GetBox(int i) => new(boxes[i, 0], boxes[i, 1], boxes[i, 2], boxes[i, 3]);
        }

        internal static float CalculateIoU(Vector4 boxA, Vector4 boxB)
        {
            float x1 = Mathf.Max(boxA.x, boxB.x);
            float y1 = Mathf.Max(boxA.y, boxB.y);
            float x2 = Mathf.Min(boxA.z, boxB.z);
            float y2 = Mathf.Min(boxA.w, boxB.w);

            float intersectionArea = Mathf.Max(0, x2 - x1) * Mathf.Max(0, y2 - y1);
            float unionArea = (boxA.z - boxA.x) * (boxA.w - boxA.y)
                            + (boxB.z - boxB.x) * (boxB.w - boxB.y)
                            - intersectionArea;

            return unionArea == 0 ? 0 : intersectionArea / unionArea;
        }
    }
}