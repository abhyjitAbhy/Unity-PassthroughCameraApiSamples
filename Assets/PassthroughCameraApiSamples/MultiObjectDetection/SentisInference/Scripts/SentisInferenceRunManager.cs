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
        [SerializeField, Range(0, 1)] private float m_scoreThreshold = 0.23f;

        [Header("UI display references")]
        [SerializeField] private SentisInferenceUiManager m_uiInference;
        [SerializeField] private Box3DManager m_box3DManager;

        [Header("[Editor Only] Convert to Sentis")]
        public ModelAsset OnnxModel;
        [Space(40)]

        private Worker m_engine;
        private Vector2Int m_inputSize;
        private readonly List<(int classId, Vector4 boundingBox)> m_detections = new();

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

            // Give Box3DManager the same label array so it can show class names
            m_box3DManager?.SetLabels(m_labelsAsset);

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
            static extern OVRPlugin.Result ovrp_GetNodePoseStateAtTime(double time, OVRPlugin.Node nodeId, out OVRPlugin.PoseStatef nodePoseState);

            if (!ovrp_GetNodePoseStateAtTime(OVRPlugin.GetTimeInSeconds(), OVRPlugin.Node.Head, out _).IsSuccess())
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
            if (boxes.shape[0] == 0) yield break;

            var classIDsAwaiter = (m_engine.PeekOutput(1) as Tensor<int>).ReadbackAndCloneAsync().GetAwaiter();
            while (!classIDsAwaiter.IsCompleted) yield return null;
            using var classIDs = classIDsAwaiter.GetResult();
            if (classIDs.shape[0] == 0) { Debug.LogError("classIDs.shape[0] == 0"); yield break; }

            var scoresAwaiter = (m_engine.PeekOutput(2) as Tensor<float>).ReadbackAndCloneAsync().GetAwaiter();
            while (!scoresAwaiter.IsCompleted) yield return null;
            using var scores = scoresAwaiter.GetResult();
            if (scores.shape[0] == 0) { Debug.LogError("scores.shape[0] == 0"); yield break; }

            NonMaxSuppression(m_detections, boxes, classIDs, scores, m_iouThreshold, m_scoreThreshold);

            if (!m_cameraAccess.IsPlaying ||
                m_detectionManager.m_spatialAnchor == null ||
                !m_detectionManager.m_spatialAnchor.IsTracked)
                yield break;

            // Draw 2D bounding boxes (unchanged)
            m_uiInference.DrawUIBoxes(m_detections, m_inputSize, cachedCameraPose);

            // Draw accurate 3D wireframe cuboids
            m_box3DManager?.Draw3DBoxes(m_detections, m_inputSize, cachedCameraPose);
        }

        private static void NonMaxSuppression(
            List<(int classId, Vector4 boundingBox)> outDetections,
            Tensor<float> boxes,
            Tensor<int> classIDs,
            Tensor<float> scores,
            float iouThreshold,
            float scoreThreshold)
        {
            outDetections.Clear();

            List<int> filteredIndices = new();
            NativeArray<float>.ReadOnly scoresArray = scores.AsReadOnlyNativeArray();
            for (int i = 0; i < scoresArray.Length; i++)
                if (scoresArray[i] >= scoreThreshold)
                    filteredIndices.Add(i);

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