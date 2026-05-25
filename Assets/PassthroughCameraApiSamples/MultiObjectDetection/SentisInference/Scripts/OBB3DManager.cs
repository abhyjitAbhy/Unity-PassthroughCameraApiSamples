// OBB3DManager.cs — FIXED
//
// KEY CHANGES vs previous version:
//
//  1. REMOVED all internal Meta SDK calls that caused CS1061 / CS0122 errors:
//       ✗ m_envDepthManager.frameDescriptors
//       ✗ EnvironmentDepthUtils.CalculateDepthCameraMatrices()
//       ✗ m_envDepthManager.GetTrackingSpaceWorldToLocalMatrix()
//     These are declared 'internal' in the SDK and not accessible from user code.
//
//  2. REPLACED with public API only:
//       ✓ Unity.XR.Oculus.Utils.GetEnvironmentDepthFrameDesc(int eye)
//     Returns EnvironmentDepthFrameDesc with public fields:
//       fovLeftAngle, fovRightAngle, fovTopAngle, fovDownAngle  (radians, all positive)
//       nearZ, farZ
//       createPoseLocation (Vector3)  — depth sensor position in tracking space
//       createPoseRotation (Quaternion) — depth sensor orientation in tracking space
//     From these we build depthProj (off-centre perspective) and depthView manually.
//
//  3. BuildDepthCameraVPInv() now uses only public API.
//     The off-centre projection matrix mirrors what EnvironmentDepthUtils does
//     internally: it uses the four asymmetric FOV angles, not a single symmetric FOV.
//
//  4. BuildMaskFromScreenUV() is REPLACED by ComputeEyeMaskRect().
//     Instead of allocating a CPU Texture2D mask and uploading it to the GPU,
//     we now pass a float4 rect (minU, minV, maxU, maxV) in normalised eye-cam
//     screen UV directly to the compute shader. The shader reprojects each depth
//     texel into eye-cam space and tests against this rect — correct alignment,
//     zero CPU texture overhead.
//
//  5. BuildPointCloud() call updated to new PointCloudBuilder signature:
//       OLD: (depthRT, maskTex, depthVPInv, zBuf)
//       NEW: (depthRT, depthVPInv, eyeVP, eyeMaskRect, zBuf)

#define DEBUG_OBB3D

using System.Collections.Generic;
using Meta.XR.EnvironmentDepth;
using Unity.XR.Oculus;   // for Utils.GetEnvironmentDepthFrameDesc
using UnityEngine;

namespace PassthroughCameraSamples.MultiObjectDetection
{
    public class OBB3DManager : MonoBehaviour
    {
        [Header("--- Plug into existing sample components ---")]
        [SerializeField] private SentisInferenceUiManager m_uiInference;

        [Header("--- Depth sampling ---")]
        [SerializeField] private ComputeShader m_depthSamplerCS;
        [SerializeField] private int m_maxPointsPerObject = 4096;
        [SerializeField] private float m_maxDepthMeters = 4f;
        [SerializeField] private float m_voxelSize = 0.02f;
        [SerializeField] private int m_minPointsForOBB = 50;

        [Header("--- Prefabs ---")]
        [SerializeField] private GameObject m_wireframePrefab;
        [SerializeField] private GameObject m_labelPrefab;

        // EnvironmentDepthManager is still needed so the depth runtime is active,
        // but we no longer call any internal methods on it.
        [Header("--- Meta depth manager (keeps depth runtime alive) ---")]
        [SerializeField] private EnvironmentDepthManager m_envDepthManager;

        private DepthTextureProvider m_depthProvider;
        private PointCloudBuilder m_pointCloudBuilder;

        private class OBBSlot
        {
            public WireframeCuboidRenderer Cuboid;
            public DimensionLabelRenderer Label;
            public TemporalSmoother Smoother = new TemporalSmoother(5, 0.25f, 0.12f);
            public float LastSeen;
            public int ClassId = -1;
            public Vector3 LastCenter;
        }

        private readonly List<OBBSlot> m_slots = new();
        private const float SlotTimeout = 2.5f;
        private const float SlotMatchRadius = 0.4f;

        private bool m_loggedNullUI;
        private bool m_loggedNullDepthRT;
        private bool m_loggedNullPrefabs;
        private bool m_depthWasReady;
        private int m_lastDetectionCount = -1;
        private int m_totalSlotsCreated;

        void Awake()
        {
            Log($"Awake — uiInference={m_uiInference}, depthCS={m_depthSamplerCS}, " +
                $"envDepthManager={m_envDepthManager}, " +
                $"wireframePrefab={m_wireframePrefab}, labelPrefab={m_labelPrefab}");

            if (m_uiInference == null) LogError("m_uiInference is NULL.");
            if (m_depthSamplerCS == null) LogError("m_depthSamplerCS is NULL.");
            if (m_envDepthManager == null)
                LogError("m_envDepthManager is NULL — depth runtime won't start.");
            if (m_wireframePrefab == null || m_labelPrefab == null)
                LogError("wireframePrefab or labelPrefab is NULL.");

            m_depthProvider = gameObject.AddComponent<DepthTextureProvider>();
            m_pointCloudBuilder = gameObject.AddComponent<PointCloudBuilder>();
            m_pointCloudBuilder.Initialize(m_depthSamplerCS, m_maxPointsPerObject, m_maxDepthMeters);

            Log("Awake complete.");
        }

        void Update()
        {
            if (m_uiInference == null)
            {
                if (!m_loggedNullUI) { LogError("m_uiInference null in Update."); m_loggedNullUI = true; }
                return;
            }

            if (m_depthProvider.DepthRT == null)
            {
                if (!m_loggedNullDepthRT) { Log("DepthRT not ready — waiting..."); m_loggedNullDepthRT = true; }
                return;
            }
            if (!m_depthWasReady)
            {
                Log($"DepthRT READY: {m_depthProvider.DepthRT.width}x{m_depthProvider.DepthRT.height} " +
                    $"ZBufParams={Shader.GetGlobalVector("_EnvironmentDepthZBufferParams")}");
                m_depthWasReady = true;
                m_loggedNullDepthRT = false;
            }

            // ── Build depth camera VP inverse (public API only) ───────────────
            if (!TryBuildDepthCameraVPInv(out Matrix4x4 depthVPInv))
                return;

            Vector4 depthZBufParams = Shader.GetGlobalVector("_EnvironmentDepthZBufferParams");

            // ── Expire stale slots ────────────────────────────────────────────
            for (int i = m_slots.Count - 1; i >= 0; i--)
            {
                if (Time.time - m_slots[i].LastSeen > SlotTimeout)
                {
                    Log($"Slot {i} (class={m_slots[i].ClassId}) timed out.");
                    m_slots[i].Smoother.Reset();
                    Destroy(m_slots[i].Cuboid.gameObject);
                    Destroy(m_slots[i].Label.gameObject);
                    m_slots.RemoveAt(i);
                }
            }

            var boxes = m_uiInference.m_boxDrawn;
            int detCount = boxes == null ? 0 : boxes.Count;
            if (detCount != m_lastDetectionCount)
            {
                Log(detCount == 0 ? "Detections → 0." : $"Detections → {detCount}.");
                m_lastDetectionCount = detCount;
            }
            if (detCount == 0) return;

            Camera eyeCam = Camera.main;
            if (eyeCam == null) { LogError("Camera.main is null."); return; }

            // Eye camera VP — used by compute shader to reproject depth→screen
            Matrix4x4 eyeVP = eyeCam.projectionMatrix * eyeCam.worldToCameraMatrix;

            var matched = new bool[m_slots.Count];

            for (int i = 0; i < boxes.Count; i++)
            {
                var box = boxes[i];
                string tag = $"[box {i} cls={box.ClassId} '{box.ClassName}']";

                // ── Step 1: compute eye-cam UV rect from YOLO box ─────────────
                if (!TryComputeEyeMaskRect(box, tag, eyeCam, out Vector4 eyeMaskRect))
                    continue;

                // ── Step 2: point cloud — GPU reprojection inside compute shader
                Vector3[] rawPts = m_pointCloudBuilder.BuildPointCloud(
                    m_depthProvider.DepthRT,
                    depthVPInv,
                    eyeVP,
                    eyeMaskRect,
                    depthZBufParams);

                Log($"{tag} Points: raw={rawPts.Length} (min={m_minPointsForOBB})");

                if (rawPts.Length < m_minPointsForOBB)
                {
                    Log($"{tag} SKIP — too few raw points.");
                    continue;
                }

                // ── Step 3: outlier rejection + downsample ────────────────────
                var clean = OutlierRejector.RemoveStatisticalOutliers(rawPts, 8, 1.5f);
                var sparse = OutlierRejector.VoxelDownsample(clean, m_voxelSize);
                int minClean = m_minPointsForOBB / 2;

                Log($"{tag} Points: clean={clean.Length} sparse={sparse.Length} (min={minClean})");

                if (sparse.Length < minClean)
                {
                    Log($"{tag} SKIP — too few after cleaning.");
                    continue;
                }

                // ── Step 4: PCA OBB ───────────────────────────────────────────
                var obb = PCAOBBFitter.Fit(sparse);
                Log($"{tag} OBB: center={obb.Center:F3} extents={obb.Extents:F3} " +
                    $"euler={obb.Orientation.eulerAngles:F1}");

                obb = PCAOBBFitter.CorrectCameraAlignment(obb, eyeCam);

                // ── Step 5: slot ──────────────────────────────────────────────
                OBBSlot slot = FindOrCreateSlot(box.ClassId, obb.Center, matched);
                slot.LastSeen = Time.time;
                slot.ClassId = box.ClassId;
                slot.LastCenter = obb.Center;
                obb = slot.Smoother.Smooth(obb);

                // ── Step 6: render ────────────────────────────────────────────
                if (slot.Cuboid == null || slot.Label == null)
                {
                    LogError($"{tag} Slot Cuboid or Label is null.");
                    continue;
                }

                bool wasActive = slot.Cuboid.gameObject.activeSelf;
                slot.Cuboid.gameObject.SetActive(true);
                slot.Label.gameObject.SetActive(true);
                slot.Cuboid.UpdateFromOBB(obb);
                slot.Label.UpdateFromOBB(obb, box.ClassName);

                if (!wasActive)
                    Log($"{tag} *** CUBOID ACTIVATED *** center={obb.Center:F3} extents={obb.Extents:F3}");
            }

            for (int i = 0; i < m_slots.Count; i++)
                if (!matched[i]) HideSlot(m_slots[i]);
        }

        // ─────────────────────────────────────────────────────────────────────
        // Build the depth camera inverse VP matrix using ONLY public Meta SDK API.
        //
        // Public API used:
        //   Unity.XR.Oculus.Utils.GetEnvironmentDepthFrameDesc(int eye)
        //   → EnvironmentDepthFrameDesc with:
        //       isValid, fovLeftAngle, fovRightAngle, fovTopAngle, fovDownAngle (radians)
        //       nearZ, farZ
        //       createPoseLocation (Vector3)   — sensor origin in tracking space
        //       createPoseRotation (Quaternion) — sensor orientation in tracking space
        //
        // Projection: off-centre perspective from the four asymmetric FOV angles.
        // This mirrors what EnvironmentDepthUtils.CalculateDepthCameraMatrices()
        // does internally but uses only fields we can actually access.
        //
        // View: world→camera = inverse of the sensor's tracking-space TRS.
        // ─────────────────────────────────────────────────────────────────────
        private bool TryBuildDepthCameraVPInv(out Matrix4x4 vpInv)
        {
            vpInv = Matrix4x4.identity;

            var desc = Utils.GetEnvironmentDepthFrameDesc(0);  // eye 0 = left/centre depth
            if (!desc.isValid)
            {
                Log("DepthFrameDesc not valid yet — skipping frame.");
                return false;
            }

            float nearZ = desc.nearZ;
            float farZ = desc.farZ;
            if (nearZ <= 0f) nearZ = 0.1f;
            if (farZ <= nearZ) farZ = nearZ + 0.001f;

            // ── Asymmetric (off-centre) perspective projection ────────────────
            // fovLeftAngle, fovRightAngle, fovTopAngle, fovDownAngle are all
            // positive angles in radians from the optical axis.
            // left/right/top/bottom are frustum extents AT the near plane.
            float left = -Mathf.Tan(desc.fovLeftAngle) * nearZ;
            float right = Mathf.Tan(desc.fovRightAngle) * nearZ;
            float bottom = -Mathf.Tan(desc.fovDownAngle) * nearZ;
            float top = Mathf.Tan(desc.fovTopAngle) * nearZ;

            // Column-major Unity Matrix4x4 off-centre perspective:
            // Same formula as GL_PROJECTION off-centre, left-handed (Unity).
            float x = 2f * nearZ / (right - left);
            float y = 2f * nearZ / (top - bottom);
            float a = (right + left) / (right - left);
            float b = (top + bottom) / (top - bottom);
            float c = -(farZ + nearZ) / (farZ - nearZ);
            float d = -(2f * farZ * nearZ) / (farZ - nearZ);

            var depthProj = new Matrix4x4();
            depthProj.SetColumn(0, new Vector4(x, 0, 0, 0));
            depthProj.SetColumn(1, new Vector4(0, y, 0, 0));
            depthProj.SetColumn(2, new Vector4(a, b, c, -1));
            depthProj.SetColumn(3, new Vector4(0, 0, d, 0));

            // ── View matrix: world → depth sensor ────────────────────────────
            // createPoseLocation/Rotation give sensor pose in tracking space.
            // Tracking space = world space on Quest (OVRCameraRig at origin).
            // worldToCamera = inverse of TRS(position, rotation, identity)
            // createPoseRotation is Vector4 (xyzw quaternion) — convert to Quaternion
            var poseRot = new Quaternion(desc.createPoseRotation.x, desc.createPoseRotation.y,
                                           desc.createPoseRotation.z, desc.createPoseRotation.w);
            var sensorTRS = Matrix4x4.TRS(desc.createPoseLocation, poseRot, Vector3.one);
            var depthView = sensorTRS.inverse;

            vpInv = (depthProj * depthView).inverse;
            return true;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Convert a YOLO bounding box RectTransform into a normalised eye-cam
        // screen UV rect (minU, minV, maxU, maxV) in [0,1] bottom-left origin.
        //
        // This rect is passed directly to the compute shader as _EyeMaskRect.
        // The shader reprojects each depth texel into eye-cam space and tests
        // against this rect — no CPU Texture2D needed.
        // ─────────────────────────────────────────────────────────────────────
        private bool TryComputeEyeMaskRect(
            SentisInferenceUiManager.BoundingBoxData box,
            string tag,
            Camera eyeCam,
            out Vector4 eyeMaskRect)
        {
            eyeMaskRect = Vector4.zero;

            RectTransform rt = box.BoxRectTransform;
            if (rt == null) { LogError($"{tag} BoxRectTransform null."); return false; }

            Canvas canvas = rt.GetComponentInParent<Canvas>();
            if (canvas == null) { LogError($"{tag} No Canvas in parents."); return false; }

            // Canvas pixel rect → screen pixels
            Rect screenRect = RectTransformUtility.PixelAdjustRect(rt, canvas);
            float sw = Screen.width;
            float sh = Screen.height;

            float cx = sw * 0.5f + screenRect.x;
            float cy = sh * 0.5f + screenRect.y;
            float hw = screenRect.width * 0.5f;
            float hh = screenRect.height * 0.5f;

            // Normalised UV [0,1] bottom-left origin
            float minU = Mathf.Clamp01((cx - hw) / sw);
            float maxU = Mathf.Clamp01((cx + hw) / sw);
            float minV = Mathf.Clamp01((cy - hh) / sh);
            float maxV = Mathf.Clamp01((cy + hh) / sh);

            Log($"{tag} EyeMaskRect UV: U=[{minU:F3},{maxU:F3}] V=[{minV:F3},{maxV:F3}]");

            if (maxU - minU < 0.01f || maxV - minV < 0.01f)
            {
                LogError($"{tag} SKIP — UV rect too small.");
                return false;
            }

            // Small padding so border pixels aren't clipped
            const float kPad = 0.005f;
            eyeMaskRect = new Vector4(
                Mathf.Max(0f, minU - kPad),
                Mathf.Max(0f, minV - kPad),
                Mathf.Min(1f, maxU + kPad),
                Mathf.Min(1f, maxV + kPad));

            return true;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Slot matching / creation
        // ─────────────────────────────────────────────────────────────────────
        private OBBSlot FindOrCreateSlot(int classId, Vector3 worldCenter, bool[] matched)
        {
            for (int i = 0; i < m_slots.Count; i++)
            {
                if (!matched[i]
                    && m_slots[i].ClassId == classId
                    && Vector3.Distance(m_slots[i].LastCenter, worldCenter) < SlotMatchRadius)
                {
                    matched[i] = true;
                    return m_slots[i];
                }
            }
            for (int i = 0; i < m_slots.Count; i++)
            {
                if (!matched[i] && Time.time - m_slots[i].LastSeen > SlotTimeout * 0.5f)
                {
                    m_slots[i].Smoother.Reset();
                    m_slots[i].ClassId = classId;
                    matched[i] = true;
                    Log($"Reusing stale slot {i} for class {classId}.");
                    return m_slots[i];
                }
            }

            if (m_wireframePrefab == null || m_labelPrefab == null)
            {
                if (!m_loggedNullPrefabs)
                {
                    LogError("Cannot instantiate — prefabs null.");
                    m_loggedNullPrefabs = true;
                }
                return new OBBSlot { ClassId = classId, LastCenter = worldCenter };
            }

            var cuboidGO = Instantiate(m_wireframePrefab);
            var labelGO = Instantiate(m_labelPrefab);
            var cuboid = cuboidGO.GetComponent<WireframeCuboidRenderer>();
            var label = labelGO.GetComponent<DimensionLabelRenderer>();

            m_totalSlotsCreated++;
            Log($"*** INSTANTIATED slot #{m_totalSlotsCreated} class={classId} ***\n" +
                $"  Cuboid: '{cuboidGO.name}' WireframeCuboidRenderer={cuboid != null}\n" +
                $"  Label:  '{labelGO.name}'  DimensionLabelRenderer={label != null}");

            if (cuboid == null) LogError("WireframeCuboidRenderer NOT on wireframePrefab.");
            if (label == null) LogError("DimensionLabelRenderer NOT on labelPrefab.");

            var newSlot = new OBBSlot
            {
                Cuboid = cuboid,
                Label = label,
                ClassId = classId,
                LastCenter = worldCenter
            };
            m_slots.Add(newSlot);
            return newSlot;
        }

        private static void HideSlot(OBBSlot slot)
        {
            if (slot.Cuboid != null) slot.Cuboid.gameObject.SetActive(false);
            if (slot.Label != null) slot.Label.gameObject.SetActive(false);
        }

        private static void Log(string msg)
        {
#if DEBUG_OBB3D
            Debug.Log("[OBB3D] " + msg);
#endif
        }

        private static void LogError(string msg) =>
            Debug.LogError("[OBB3D] " + msg);
    }
}