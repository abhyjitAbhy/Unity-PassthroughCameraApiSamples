// Copyright (c) Meta Platforms, Inc. and affiliates.

using System.Collections;
using System.Collections.Generic;
using Meta.XR;
using UnityEngine;

namespace PassthroughCameraSamples.MultiObjectDetection
{
    /// <summary>
    /// Box3DManager.cs
    ///
    /// Changes in this version:
    ///
    ///   5. DIMENSION READOUT ON LOCK  — LockBox() and LockAll() now pass the
    ///      EMA-smoothed SmoothedSize (metres) into SetLocked() so the visualizer
    ///      can display the real-world "W × H × D cm" on the frozen box.
    ///      No depth re-sampling is needed; the size that was already stabilised
    ///      by the EMA over the tracking period is used directly.
    ///
    ///   Previous stability changes (1-4) are unchanged:
    ///   1. One coroutine per class  2. Pose EMA smoothing
    ///   3. PCA 6-DoF pose           4. Global freeze / lock API
    /// </summary>
    public class Box3DManager : MonoBehaviour
    {
        [Header("Scene References")]
        [SerializeField] private PassthroughCameraAccess m_cameraAccess;
        [SerializeField] private EnvironmentRayCastSampleManager m_raycastManager;

        [Header("3D Box Prefab")]
        [Tooltip("Prefab that has a Box3DVisualizer component")]
        [SerializeField] private GameObject m_box3DPrefab;

        [Header("Depth Sampling")]
        [Tooltip("Grid dimension (gridSize x gridSize rays per box). 5 = 25 rays.")]
        [Range(3, 9)]
        [SerializeField] private int m_gridSize = 5;

        [Tooltip("Max deviation from median depth to count as same object surface (metres)")]
        [Range(0.05f, 0.4f)]
        [SerializeField] private float m_clusterTolerance = 0.12f;

        [Tooltip("Minimum inlier hits required to draw a box")]
        [Range(3, 15)]
        [SerializeField] private int m_minHits = 5;

        [Header("Box Persistence")]
        [Tooltip("How many seconds a 3D box stays visible without a fresh detection")]
        [Range(0.5f, 5f)]
        [SerializeField] private float m_boxPersistSeconds = 2f;

        [Header("Pose EMA Smoothing")]
        [Tooltip("How strongly each new depth measurement pulls the box. " +
                 "0.1 = very smooth/slow, 0.5 = snappy. Default 0.25.")]
        [Range(0.05f, 1.0f)]
        [SerializeField] private float m_poseAlpha = 0.25f;

        [Tooltip("Slerp weight for rotation EMA. Lower = slower rotation updates.")]
        [Range(0.05f, 1.0f)]
        [SerializeField] private float m_rotationAlpha = 0.2f;

        [Header("Class Filter")]
        [Tooltip("YOLO class IDs to track. Leave EMPTY to track ALL classes.")]
        [SerializeField] private List<int> m_allowedClassIds = new();

        private string[] m_labels;
        private bool m_globalFreeze;

        // ── Pool ─────────────────────────────────────────────────────────────

        private readonly List<Box3DInstance> m_activeBoxes = new();
        private readonly List<Box3DVisualizer> m_pool = new();
        private readonly List<Vector3> m_hitBuffer = new();

        private class Box3DInstance
        {
            public int ClassId;
            public Box3DVisualizer Visualizer;
            public float LastUpdateTime;
            public bool Locked;

            // Prevent multiple simultaneous PlaceBox3D coroutines for this slot
            public bool PendingCoroutine;

            // EMA-smoothed pose — updated in PlaceBox3D, read in SetBox call
            public Vector3 SmoothedCenter;
            public Vector3 SmoothedSize;
            public Quaternion SmoothedRotation;
            public bool PoseInitialised;
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

        public void ToggleGlobalFreeze()
        {
            m_globalFreeze = !m_globalFreeze;
            if (m_globalFreeze) LockAll(); else UnlockAll();
        }

        public void LockAll()
        {
            foreach (var inst in m_activeBoxes)
            {
                inst.Locked = true;
                // Pass the EMA-smoothed size so the visualizer can show dimensions
                inst.Visualizer.SetLocked(true, inst.SmoothedSize);
            }
        }

        /// <summary>
        /// Locks the 3D box for classId that is closest to worldPosition.
        /// The EMA-smoothed size at the moment of locking is frozen and displayed
        /// as a real-world dimension readout on the box label.
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
                // SmoothedSize is in metres — the visualizer formats it to cm
                best.Visualizer.SetLocked(true, best.SmoothedSize);
            }
        }

        public void UnlockAll()
        {
            m_globalFreeze = false;
            foreach (var inst in m_activeBoxes)
            {
                inst.Locked = false;
                inst.Visualizer.SetLocked(false);
            }
        }

        public void Draw3DBoxes(
            List<(int classId, Vector4 boundingBox)> detections,
            Vector2Int inputSize,
            Pose cameraPose)
        {
            if (!m_cameraAccess.IsPlaying) return;
            if (m_globalFreeze) return;

            foreach (var detection in detections)
            {
                if (m_allowedClassIds.Count > 0 && !m_allowedClassIds.Contains(detection.classId))
                    continue;

                if (IsLocked(detection.classId)) continue;

                // Gate: only one coroutine per active instance
                Box3DInstance existing = FindExistingInstance(detection.classId);
                if (existing != null && existing.PendingCoroutine) continue;

                StartCoroutine(PlaceBox3D(detection, inputSize, cameraPose));
            }
        }

        // ── Core 3D placement ────────────────────────────────────────────────

        private IEnumerator PlaceBox3D(
            (int classId, Vector4 boundingBox) detection,
            Vector2Int inputSize,
            Pose cameraPose)
        {
            // Build normRect
            float x1 = detection.boundingBox.x;
            float y1 = detection.boundingBox.y;
            float x2 = detection.boundingBox.z;
            float y2 = detection.boundingBox.w;
            float rW = x2 - x1;
            float rH = y2 - y1;

            var normRect = new Rect(
                x1 / inputSize.x,
                1f - (y1 + rH) / inputSize.y,
                rW / inputSize.x,
                rH / inputSize.y
            );

            // Depth sampling
            List<Vector3> hits = DepthSampler.SampleGrid(
                normRect, m_cameraAccess, m_raycastManager,
                m_gridSize, cameraPose, m_hitBuffer, jitter: true);

            List<Vector3> inliers = DepthSampler.ClusterFilter(
                hits, cameraPose.position, m_clusterTolerance, m_minHits);

            if (inliers.Count < m_minHits)
                yield break;

            // PCA 6-DoF pose
            if (!DepthSampler.FitPCA6DoF(inliers, cameraPose,
                    out Vector3 rawCenter, out Vector3 rawSize, out Quaternion rawRotation))
                yield break;

            // Get-or-create the visualizer slot
            Box3DVisualizer vis = GetOrReuseVisualizer(detection.classId, rawCenter);

            Box3DInstance inst = null;
            foreach (var b in m_activeBoxes)
                if (b.Visualizer == vis) { inst = b; break; }

            if (inst == null) yield break;

            inst.PendingCoroutine = true;

            // ── EMA pose smoothing ────────────────────────────────────────────
            if (!inst.PoseInitialised)
            {
                inst.SmoothedCenter = rawCenter;
                inst.SmoothedSize = rawSize;
                inst.SmoothedRotation = rawRotation;
                inst.PoseInitialised = true;
            }
            else
            {
                inst.SmoothedCenter = Vector3.Lerp(inst.SmoothedCenter, rawCenter, m_poseAlpha);
                inst.SmoothedSize = Vector3.Lerp(inst.SmoothedSize, rawSize, m_poseAlpha);
                inst.SmoothedRotation = Quaternion.Slerp(inst.SmoothedRotation, rawRotation, m_rotationAlpha);
            }

            string label = GetLabel(detection.classId);
            vis.SetBox(inst.SmoothedCenter, inst.SmoothedRotation, inst.SmoothedSize, label);

            inst.LastUpdateTime = Time.time;
            inst.PendingCoroutine = false;

            yield return null;
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        private bool IsLocked(int classId)
        {
            foreach (var inst in m_activeBoxes)
                if (inst.ClassId == classId && inst.Locked) return true;
            return false;
        }

        private Box3DInstance FindExistingInstance(int classId)
        {
            Box3DInstance best = null;
            float bestDist = float.MaxValue;
            foreach (var inst in m_activeBoxes)
            {
                if (inst.ClassId != classId) continue;
                float d = Vector3.Distance(inst.Visualizer.transform.position, Vector3.zero);
                if (d < bestDist) { bestDist = d; best = inst; }
            }
            return best;
        }

        private string GetLabel(int classId)
        {
            if (m_labels != null && classId >= 0 && classId < m_labels.Length)
                return m_labels[classId].Replace(" ", "_");
            return classId.ToString();
        }

        private Box3DVisualizer GetOrReuseVisualizer(int classId, Vector3 worldCenter)
        {
            const float reuseDistThreshold = 0.5f;
            foreach (var inst in m_activeBoxes)
            {
                if (inst.ClassId == classId &&
                    Vector3.Distance(inst.Visualizer.transform.position, worldCenter) < reuseDistThreshold)
                    return inst.Visualizer;
            }

            Box3DVisualizer vis;
            if (m_pool.Count > 0)
            {
                vis = m_pool[m_pool.Count - 1];
                m_pool.RemoveAt(m_pool.Count - 1);
                vis.gameObject.SetActive(true);
                vis.ResetForReuse();
            }
            else
            {
                vis = Instantiate(m_box3DPrefab).GetComponent<Box3DVisualizer>();
            }

            m_activeBoxes.Add(new Box3DInstance
            {
                ClassId = classId,
                Visualizer = vis,
                LastUpdateTime = Time.time,
                PoseInitialised = false,
                PendingCoroutine = false
            });

            return vis;
        }

        private void ReturnToPool(Box3DVisualizer vis)
        {
            foreach (var inst in m_activeBoxes)
            {
                if (inst.Visualizer == vis)
                {
                    inst.PoseInitialised = false;
                    inst.PendingCoroutine = false;
                    break;
                }
            }
            vis.ResetForReuse();
            vis.gameObject.SetActive(false);
            m_pool.Add(vis);
        }

        public void ClearAll()
        {
            foreach (var inst in m_activeBoxes)
                ReturnToPool(inst.Visualizer);
            m_activeBoxes.Clear();
            m_globalFreeze = false;
        }
    }
}