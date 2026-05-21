// Copyright (c) Meta Platforms, Inc. and affiliates.

using System.Collections.Generic;
using Meta.XR;
using UnityEngine;

namespace PassthroughCameraSamples.MultiObjectDetection
{
    /// <summary>
    /// Box3DManager.cs  (GPU depth texture version)
    ///
    /// Replaces the raycast coroutine approach with EnvironmentDepthSampler:
    ///   - No coroutines, no per-frame raycasts
    ///   - Calls EnvironmentDepthSampler.RequestDepthForRect() which fires an
    ///     AsyncGPUReadback; the callback arrives 1-2 frames later on the main thread
    ///   - Front/back Z from percentile filtering → accurate box depth
    ///   - Camera-aligned AABB → correct width/height/depth regardless of view angle
    ///
    /// All lock/unlock/filter/pool logic unchanged from previous version.
    /// </summary>
    public class Box3DManager : MonoBehaviour
    {
        [Header("Scene References")]
        [SerializeField] private PassthroughCameraAccess m_cameraAccess;

        [Header("GPU Depth Sampler")]
        [Tooltip("Assign the EnvironmentDepthSampler component (same GameObject is fine).")]
        [SerializeField] private EnvironmentDepthSampler m_depthSampler;

        [Header("3D Box Prefab")]
        [SerializeField] private GameObject m_box3DPrefab;

        [Header("Box Persistence")]
        [Tooltip("Seconds a 3D box stays visible without a fresh detection.")]
        [Range(0.5f, 5f)]
        [SerializeField] private float m_boxPersistSeconds = 2f;

        [Header("Class Filter")]
        [Tooltip("YOLO class IDs to track. Leave EMPTY to track ALL classes.\n" +
                 "COCO IDs: 0=person  62=TV/monitor  63=laptop\n" +
                 "          64=mouse  66=keyboard    67=cell phone\n" +
                 "          72=fridge 73=book         76=AC/remote")]
        [SerializeField] private List<int> m_allowedClassIds = new();

        private string[] m_labels;

        // ── Pool ─────────────────────────────────────────────────────────────

        private readonly List<Box3DInstance> m_activeBoxes = new();
        private readonly List<Box3DVisualizer> m_pool = new();

        private class Box3DInstance
        {
            public int ClassId;
            public Box3DVisualizer Visualizer;
            public float LastUpdateTime;
            public bool Locked;
            /// <summary>True while an AsyncGPUReadback is in-flight for this class.</summary>
            public bool PendingDepth;
        }

        // ── Unity Lifecycle ──────────────────────────────────────────────────

        private void Update()
        {
            for (int i = m_activeBoxes.Count - 1; i >= 0; i--)
            {
                var inst = m_activeBoxes[i];
                if (!inst.Locked && Time.time - inst.LastUpdateTime > m_boxPersistSeconds)
                {
                    ReturnToPool(inst.Visualizer);
                    m_activeBoxes.RemoveAt(i);
                }
            }
        }

        // ── Public API ───────────────────────────────────────────────────────

        public void SetLabels(TextAsset labelsAsset)
        {
            if (labelsAsset != null)
                m_labels = labelsAsset.text.Split('\n');
        }

        public void SetFilter(IEnumerable<int> classIds)
        {
            m_allowedClassIds.Clear();
            if (classIds != null)
                m_allowedClassIds.AddRange(classIds);
        }

        /// <summary>
        /// Called from SentisInferenceRunManager every inference frame.
        /// Fires one async depth request per detection (non-blocking).
        /// </summary>
        public void Draw3DBoxes(
            List<(int classId, Vector4 boundingBox)> detections,
            Vector2Int inputSize,
            Pose cameraPose)
        {
            if (!m_cameraAccess.IsPlaying) return;
            if (m_depthSampler == null)
            {
                Debug.LogWarning("[Box3DManager] m_depthSampler is not assigned.");
                return;
            }

            foreach (var detection in detections)
            {
                if (m_allowedClassIds.Count > 0 && !m_allowedClassIds.Contains(detection.classId))
                    continue;

                if (IsLocked(detection.classId))
                    continue;

                // Skip if a depth request is already in-flight for this class
                // (prevents queuing multiple requests per class per second)
                if (IsPendingDepth(detection.classId))
                    continue;

                // ── Build normRect — identical to SentisInferenceUiManager ────
                float x1 = detection.boundingBox.x;
                float y1 = detection.boundingBox.y;
                float x2 = detection.boundingBox.z;
                float y2 = detection.boundingBox.w;
                float yMax = y2;

                var normRect = new Rect(
                    x1 / inputSize.x,
                    1f - yMax / inputSize.y,   // Y-flip to match UI manager
                    (x2 - x1) / inputSize.x,
                    (y2 - y1) / inputSize.y
                );

                // Mark as pending before the async call so we don't double-queue
                SetPendingDepth(detection.classId, true);

                // Capture loop variables for the closure
                int classId = detection.classId;
                var bbox = detection.boundingBox;
                Pose pose = cameraPose;
                Rect rect = normRect;
                Vector2Int iSize = inputSize;

                m_depthSampler.RequestDepthForRect(
                    rect,
                    pose,
                    iSize,
                    bbox,
                    volume =>
                    {
                        // This callback runs on the main thread (Unity AsyncGPUReadback guarantee)
                        SetPendingDepth(classId, false);

                        if (!volume.IsValid) return;

                        string label = GetLabel(classId);
                        Box3DVisualizer vis = GetOrReuseVisualizer(classId, volume.Center);
                        vis.SetBox(volume.Center, volume.Rotation, volume.Size, label);

                        foreach (var inst in m_activeBoxes)
                        {
                            if (inst.Visualizer == vis)
                            {
                                inst.LastUpdateTime = Time.time;
                                break;
                            }
                        }
                    });
            }
        }

        /// <summary>
        /// Freeze the 3D box nearest to worldPosition for the given classId.
        /// Called by DetectionManager when a spatial anchor is placed (Button A / pinch).
        /// </summary>
        public void LockBox(int classId, Vector3 worldPosition)
        {
            Box3DInstance best = null;
            float bestDist = float.MaxValue;

            foreach (var inst in m_activeBoxes)
            {
                if (inst.ClassId != classId) continue;
                float d = Vector3.Distance(inst.Visualizer.transform.position, worldPosition);
                if (d < bestDist) { bestDist = d; best = inst; }
            }

            if (best != null)
            {
                best.Locked = true;
                best.Visualizer.SetLocked(true);
            }
        }

        /// <summary>
        /// Resume live tracking on all boxes.
        /// Called by DetectionManager when markers are cleared (Button B / pinch).
        /// </summary>
        public void UnlockAll()
        {
            foreach (var inst in m_activeBoxes)
            {
                inst.Locked = false;
                inst.Visualizer.SetLocked(false);
            }
        }

        public void ClearAll()
        {
            foreach (var inst in m_activeBoxes)
                ReturnToPool(inst.Visualizer);
            m_activeBoxes.Clear();
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        private bool IsLocked(int classId)
        {
            foreach (var inst in m_activeBoxes)
                if (inst.ClassId == classId && inst.Locked) return true;
            return false;
        }

        private bool IsPendingDepth(int classId)
        {
            foreach (var inst in m_activeBoxes)
                if (inst.ClassId == classId && inst.PendingDepth) return true;
            return false;
        }

        private void SetPendingDepth(int classId, bool pending)
        {
            foreach (var inst in m_activeBoxes)
                if (inst.ClassId == classId) inst.PendingDepth = pending;
        }

        private string GetLabel(int classId)
        {
            if (m_labels != null && classId >= 0 && classId < m_labels.Length)
                return m_labels[classId].Replace(" ", "_");
            return classId.ToString();
        }

        private Box3DVisualizer GetOrReuseVisualizer(int classId, Vector3 worldCenter)
        {
            const float threshold = 0.5f;
            for (int i = 0; i < m_activeBoxes.Count; i++)
            {
                var inst = m_activeBoxes[i];
                if (inst.ClassId == classId &&
                    Vector3.Distance(inst.Visualizer.transform.position, worldCenter) < threshold)
                    return inst.Visualizer;
            }

            Box3DVisualizer vis;
            if (m_pool.Count > 0)
            {
                vis = m_pool[m_pool.Count - 1];
                m_pool.RemoveAt(m_pool.Count - 1);
                vis.gameObject.SetActive(true);
            }
            else
            {
                vis = Instantiate(m_box3DPrefab).GetComponent<Box3DVisualizer>();
            }

            m_activeBoxes.Add(new Box3DInstance
            {
                ClassId = classId,
                Visualizer = vis,
                LastUpdateTime = Time.time
            });

            return vis;
        }

        private void ReturnToPool(Box3DVisualizer vis)
        {
            vis.gameObject.SetActive(false);
            m_pool.Add(vis);
        }
    }
}