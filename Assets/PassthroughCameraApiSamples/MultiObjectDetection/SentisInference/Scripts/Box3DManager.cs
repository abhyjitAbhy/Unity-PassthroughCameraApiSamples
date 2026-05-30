// Copyright (c) Meta Platforms, Inc. and affiliates.

using System.Buffers;
using System.Collections;
using System.Collections.Generic;
using Meta.XR;
using Meta.XR.BuildingBlocks.AIBlocks;
using Unity.Collections;
using UnityEngine;

namespace PassthroughCameraSamples.MultiObjectDetection
{
    /// <summary>
    /// Box3DManager.cs
    ///
    /// Changes in this version (Gaps 3, 4, 7 + PlaceBox3D integration):
    ///
    ///   Gap 3 — FrameSnapshot struct: VP matrix + depth buffer captured per depth
    ///     frame via OnDepthFrame callback, using ArrayPool to avoid heap pressure.
    ///     PlaceBox3D takes a struct copy at entry so it is safe across yield points.
    ///
    ///   Gap 4 — Stereo eye index: computed once in Awake() from
    ///     m_cameraAccess.CameraPosition; passed through FrameSnapshot.EyeIdx into
    ///     every depth buffer lookup. Wrong-eye parallax at 50 cm ~ IPD (≈64 mm).
    ///
    ///   Gap 6 — MeasurePhysicalSize hybrid: after FitPCA6DoF succeeds,
    ///     DepthSampler.MeasurePhysicalSize overrides rawSize.x and rawSize.y
    ///     with geometrically grounded edge-projected width/height. rawSize.z
    ///     (depth extent) is kept from PCA. EMA smoothing applied to hybrid size.
    ///
    ///   Gap 7 — Zero-allocation hot path: m_hitBuffer and m_inlierBuffer are
    ///     pre-allocated fields passed as reuse/inlierReuse to eliminate all
    ///     per-frame List<Vector3> allocations.
    ///
    ///   Sampling path decision: when DepthTextureAccess is present and
    ///     initialised, SampleGridDepthTexture is used (primary, zero-raycast).
    ///     Falls back to SampleGrid (raycast) when depth texture unavailable.
    ///     Optional segmentation mask routes through SampleGridMaskGated.
    ///
    ///   Previous changes (1-5) unchanged:
    ///   1. One coroutine per class  2. Pose EMA smoothing
    ///   3. PCA 6-DoF pose           4. Global freeze / lock API
    ///   5. Dimension readout on lock
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

        // ── Gap 3: FrameSnapshot ─────────────────────────────────────────────

        /// <summary>
        /// Snapshot of a single depth frame's data, captured on the depth callback
        /// thread and consumed safely inside coroutines via struct copy.
        /// All arrays are rented from ArrayPool and returned in OnDepthFrame/OnDestroy.
        /// </summary>
        private struct FrameSnapshot
        {
            public Pose CameraPose;
            public float[] DepthBuffer;   // rented from ArrayPool<float>.Shared
            public Matrix4x4[] VpMatrix;      // rented from ArrayPool<Matrix4x4>.Shared
            public int EyeIdx;
            public int TexSize;
            public bool IsValid;
        }

        private FrameSnapshot _latestFrame;
        private DepthTextureAccess _depthAccess;
        private int _eyeIdx;

        // ── Pool ─────────────────────────────────────────────────────────────

        private readonly List<Box3DInstance> m_activeBoxes = new();
        private readonly List<Box3DVisualizer> m_pool = new();

        // Gap 7: pre-allocated reuse buffers — eliminates per-frame heap alloc
        private readonly List<Vector3> m_hitBuffer = new List<Vector3>(64);
        private readonly List<Vector3> m_inlierBuffer = new List<Vector3>(64);

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

        private void Awake()
        {
            // Gap 3 & 4: subscribe to depth frames; compute stereo eye index once
            _depthAccess = GetComponent<DepthTextureAccess>(); // null is valid — raycast fallback used
            if (_depthAccess != null)
                _depthAccess.OnDepthTextureUpdateCPU += OnDepthFrame;

            // Gap 4: wrong eye at 50cm = ~IPD parallax error in measurements
            _eyeIdx = (m_cameraAccess != null &&
                       m_cameraAccess.CameraPosition == PassthroughCameraAccess.CameraPositionType.Left)
                      ? 0 : 1;
        }

        private void OnDestroy()
        {
            if (_depthAccess != null)
                _depthAccess.OnDepthTextureUpdateCPU -= OnDepthFrame;

            // Gap 3: return rented buffers to pool
            if (_latestFrame.DepthBuffer != null)
            {
                ArrayPool<float>.Shared.Return(_latestFrame.DepthBuffer, clearArray: false);
                _latestFrame.DepthBuffer = null;
            }
            if (_latestFrame.VpMatrix != null)
            {
                ArrayPool<Matrix4x4>.Shared.Return(_latestFrame.VpMatrix, clearArray: false);
                _latestFrame.VpMatrix = null;
            }
        }

        // ── Gap 3: Depth frame callback ──────────────────────────────────────

        /// <summary>
        /// Called by DepthTextureAccess on each new depth frame (AsyncGPUReadback callback).
        /// Copies the NativeArray into a rented buffer — NativeArray is owned by the
        /// building block and must not be referenced after this callback returns.
        /// </summary>
        private void OnDepthFrame(DepthTextureAccess.DepthFrameData d)
        {
            // Return previous rented buffers before renting new ones
            if (_latestFrame.DepthBuffer != null)
                ArrayPool<float>.Shared.Return(_latestFrame.DepthBuffer, clearArray: false);
            if (_latestFrame.VpMatrix != null)
                ArrayPool<Matrix4x4>.Shared.Return(_latestFrame.VpMatrix, clearArray: false);

            int len = d.DepthTexturePixels.Length;
            _latestFrame.DepthBuffer = ArrayPool<float>.Shared.Rent(len);
            _latestFrame.VpMatrix = ArrayPool<Matrix4x4>.Shared.Rent(d.ViewProjectionMatrix.Length);

            // NativeArray<float>.Copy is the safe API — never store the NativeArray ref
            NativeArray<float>.Copy(d.DepthTexturePixels, _latestFrame.DepthBuffer, len);
            System.Array.Copy(d.ViewProjectionMatrix, _latestFrame.VpMatrix, d.ViewProjectionMatrix.Length);

            _latestFrame.CameraPose = d.CameraPose;
            _latestFrame.TexSize = _depthAccess.TextureSize;
            _latestFrame.EyeIdx = _eyeIdx;   // Gap 4: correct stereo eye
            _latestFrame.IsValid = true;
        }

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

        /// <summary>
        /// Entry point from the YOLO pipeline. Optionally accepts a segmentation mask
        /// to gate depth sampling to confirmed object pixels.
        /// </summary>
        /// <param name="detections">YOLO detections this frame.</param>
        /// <param name="inputSize">YOLO input resolution in pixels.</param>
        /// <param name="cameraPose">Camera pose at inference time (used as fallback when no depth snapshot).</param>
        /// <param name="mask">Optional segmentation mask (float array, may be null).</param>
        /// <param name="maskWidth">Pixel width of mask (ignored when mask is null).</param>
        /// <param name="maskHeight">Pixel height of mask (ignored when mask is null).</param>
        /// <param name="maskAreLogits">True = threshold mask at 0; false = at 0.5.</param>
        public void Draw3DBoxes(
            List<(int classId, Vector4 boundingBox)> detections,
            Vector2Int inputSize,
            Pose cameraPose,
            float[] mask = null,
            int maskWidth = 0,
            int maskHeight = 0,
            bool maskAreLogits = true)
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

                StartCoroutine(PlaceBox3D(detection, inputSize, cameraPose, mask, maskWidth, maskHeight, maskAreLogits));
            }
        }

        // ── Core 3D placement ────────────────────────────────────────────────

        private IEnumerator PlaceBox3D(
            (int classId, Vector4 boundingBox) detection,
            Vector2Int inputSize,
            Pose fallbackCameraPose,
            float[] mask,
            int maskWidth,
            int maskHeight,
            bool maskAreLogits)
        {
            // Gap 3: struct copy at coroutine entry — safe across yield points,
            // even if OnDepthFrame fires and mutates _latestFrame mid-coroutine.
            var snapshot = _latestFrame;

            // Use snapshot pose when available; fall back to YOLO-time pose otherwise.
            Pose activePose = snapshot.IsValid ? snapshot.CameraPose : fallbackCameraPose;

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

            // ── Sampling path decision ────────────────────────────────────────
            // Primary: hardware depth texture (zero-raycast, most accurate).
            // With mask: mask-gated raycast path (mask confirms object pixels).
            // Fallback: plain raycast (when DepthTextureAccess absent or uninitialised).

            bool useDepthTexture = snapshot.IsValid
                && snapshot.DepthBuffer != null
                && _depthAccess != null
                && _depthAccess.IsInitialized;

            List<Vector3> hits;

            if (mask != null && maskWidth > 0 && maskHeight > 0)
            {
                // Mask-gated path: raycast only confirmed object pixels
                hits = DepthSampler.SampleGridMaskGated(
                    normRect,
                    mask, maskWidth, maskHeight, maskAreLogits,
                    m_cameraAccess, m_raycastManager,
                    m_gridSize, activePose,
                    m_hitBuffer, jitter: true, edgeExclusionFraction: 0.08f);
            }
            else if (useDepthTexture)
            {
                // Primary: hardware depth texture — accurate, no raycast cost
                hits = DepthSampler.SampleGridDepthTexture(
                    normRect,
                    snapshot.DepthBuffer, snapshot.VpMatrix,
                    snapshot.EyeIdx, snapshot.TexSize,
                    m_cameraAccess, snapshot.CameraPose,
                    m_gridSize, m_hitBuffer, jitter: true, edgeExclusionFraction: 0.08f);
            }
            else
            {
                // Fallback: raycast path
                hits = DepthSampler.SampleGrid(
                    normRect, m_cameraAccess, m_raycastManager,
                    m_gridSize, activePose, m_hitBuffer, jitter: true);
            }

            // Gap 7: pass m_inlierBuffer to avoid List<Vector3> allocation
            List<Vector3> inliers = DepthSampler.ClusterFilter(
                hits, activePose.position, m_clusterTolerance, m_minHits, m_inlierBuffer);

            if (inliers.Count < m_minHits)
                yield break;

            // PCA 6-DoF pose (rotation + rawSize.z)
            if (!DepthSampler.FitPCA6DoF(inliers, activePose,
                    out Vector3 rawCenter, out Vector3 rawSize, out Quaternion rawRotation))
                yield break;

            // ── Gap 6: MeasurePhysicalSize hybrid ────────────────────────────
            // Override W and H with geometrically projected values at median depth.
            // This is independent of point density and correct even with few inliers.
            // rawSize.z (depth extent from PCA) is preserved — PCA is better at depth
            // than projection because projection has no depth-axis baseline.
            DepthSampler.MeasurePhysicalSize(
                normRect, inliers, m_cameraAccess, activePose,
                out float physicalWidth, out float physicalHeight);
            rawSize.x = physicalWidth;
            rawSize.y = physicalHeight;

            // Get-or-create the visualizer slot
            Box3DVisualizer vis = GetOrReuseVisualizer(detection.classId, rawCenter);

            Box3DInstance inst = null;
            foreach (var b in m_activeBoxes)
                if (b.Visualizer == vis) { inst = b; break; }

            if (inst == null) yield break;

            inst.PendingCoroutine = true;

            // ── EMA pose smoothing ────────────────────────────────────────────
            // Applied to the hybrid rawSize (Gap 6 already overrode X/Y before here).
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