// PointCloudBuilder.cs — FIXED
//
// KEY CHANGES vs previous version:
//
//  1. No more CPU Texture2D mask upload every frame.
//     The compute shader now receives _EyeMaskRect (float4 UV rect in eye-cam
//     screen space) and _EyeVP (eye camera VP matrix) and does the
//     reprojection test on the GPU — depth texel → world → eye UV → rect test.
//     This eliminates the per-frame Texture2D alloc/blit/destroy overhead.
//
//  2. BuildPointCloud signature changed:
//       OLD: (RenderTexture depthRT, Texture2D maskTex, Matrix4x4 depthVPInv, Vector4 zBuf)
//       NEW: (RenderTexture depthRT, Matrix4x4 depthVPInv, Matrix4x4 eyeVP,
//             Vector4 eyeMaskRect, Vector4 zBuf)
//     eyeMaskRect = (minU, minV, maxU, maxV) in normalised eye-cam UV [0,1].
//
//  3. _MaskTexSize SetInts call removed (mask texture no longer exists).

using UnityEngine;

namespace PassthroughCameraSamples.MultiObjectDetection
{
    public class PointCloudBuilder : MonoBehaviour
    {
        [HideInInspector] public ComputeShader DepthSamplerCS;
        [HideInInspector] public int MaxPointsPerObject = 4096;
        [HideInInspector] public float MaxDepthMeters = 4.0f;

        private ComputeBuffer m_pointsBuffer;
        private ComputeBuffer m_countBuffer;
        private int m_kernelID;
        private bool m_initialized;

        public void Initialize(ComputeShader cs, int maxPoints, float maxDepth)
        {
            DepthSamplerCS = cs;
            MaxPointsPerObject = maxPoints;
            MaxDepthMeters = maxDepth;
            EnsureBuffers();
        }

        void OnDisable() => ReleaseBuffers();
        void OnDestroy() => ReleaseBuffers();

        private void EnsureBuffers()
        {
            if (m_initialized) return;
            if (DepthSamplerCS == null)
            {
                Debug.LogError("[PointCloudBuilder] DepthSamplerCS null — call Initialize() first.");
                return;
            }
            m_kernelID = DepthSamplerCS.FindKernel("ExtractMaskedDepth");
            m_pointsBuffer = new ComputeBuffer(MaxPointsPerObject, sizeof(float) * 3);
            m_countBuffer = new ComputeBuffer(1, sizeof(uint), ComputeBufferType.Raw);
            m_initialized = true;
            Debug.Log($"[PointCloudBuilder] Init — kernel={m_kernelID} " +
                      $"maxPts={MaxPointsPerObject} maxDepth={MaxDepthMeters}");
        }

        private void ReleaseBuffers()
        {
            m_pointsBuffer?.Release(); m_pointsBuffer = null;
            m_countBuffer?.Release(); m_countBuffer = null;
            m_initialized = false;
        }

        /// <summary>
        /// Extract world-space points from depth pixels whose re-projected eye-camera
        /// UV falls inside <paramref name="eyeMaskRect"/>.
        /// </summary>
        /// <param name="depthRT">
        ///   Mono RFloat RT — slice-0 blit of _EnvironmentDepthTexture (320×320).
        /// </param>
        /// <param name="depthVPInv">
        ///   (depthProj * depthView).inverse — built from FrameDesc FOV + pose.
        ///   Converts depth-cam NDC → world space.
        /// </param>
        /// <param name="eyeVP">
        ///   Camera.main.projectionMatrix * Camera.main.worldToCameraMatrix.
        ///   Re-projects world points into eye-cam screen space for YOLO box test.
        /// </param>
        /// <param name="eyeMaskRect">
        ///   YOLO box in normalised eye-cam UV [0,1] bottom-left origin:
        ///   (minU, minV, maxU, maxV).
        /// </param>
        /// <param name="zBufferParams">
        ///   Shader.GetGlobalVector("_EnvironmentDepthZBufferParams").
        ///   x=far/(far-near), y=far*near/(near-far). Used for raw→linear depth.
        /// </param>
        public Vector3[] BuildPointCloud(
            RenderTexture depthRT,
            Matrix4x4 depthVPInv,
            Matrix4x4 eyeVP,
            Vector4 eyeMaskRect,
            Vector4 zBufferParams)
        {
            EnsureBuffers();
            if (!m_initialized)
            {
                Debug.LogError("[PointCloudBuilder] Not initialized.");
                return System.Array.Empty<Vector3>();
            }
            if (depthRT == null)
            {
                Debug.LogError("[PointCloudBuilder] depthRT is null.");
                return System.Array.Empty<Vector3>();
            }

            // ── Reset counter ─────────────────────────────────────────────
            m_countBuffer.SetData(new uint[] { 0u });

            // ── Bind depth texture ────────────────────────────────────────
            DepthSamplerCS.SetTexture(m_kernelID, "_DepthTex", depthRT);

            // ── Depth camera matrices ─────────────────────────────────────
            DepthSamplerCS.SetMatrix("_DepthVPInv", depthVPInv);

            // ── Eye camera VP + YOLO rect (replaces CPU mask texture) ─────
            DepthSamplerCS.SetMatrix("_EyeVP", eyeVP);
            DepthSamplerCS.SetVector("_EyeMaskRect", eyeMaskRect);

            // ── Depth encoding ────────────────────────────────────────────
            DepthSamplerCS.SetVector("_ZBufferParams", zBufferParams);

            // ── Dimensions ───────────────────────────────────────────────
            DepthSamplerCS.SetInts("_DepthTexSize", depthRT.width, depthRT.height);

            // ── Limits ───────────────────────────────────────────────────
            DepthSamplerCS.SetFloat("_MaxDepthMeters", MaxDepthMeters);
            DepthSamplerCS.SetInt("_MaxPoints", MaxPointsPerObject);

            // ── Output buffers ────────────────────────────────────────────
            DepthSamplerCS.SetBuffer(m_kernelID, "_PointsOut", m_pointsBuffer);
            DepthSamplerCS.SetBuffer(m_kernelID, "_PointCount", m_countBuffer);

            // ── Dispatch ─────────────────────────────────────────────────
            int gx = Mathf.CeilToInt(depthRT.width / 16f);
            int gy = Mathf.CeilToInt(depthRT.height / 16f);
            DepthSamplerCS.Dispatch(m_kernelID, gx, gy, 1);

            // ── Read back count ───────────────────────────────────────────
            var countArr = new uint[1];
            m_countBuffer.GetData(countArr);
            int count = Mathf.Min((int)countArr[0], MaxPointsPerObject);

            if (count == 0) return System.Array.Empty<Vector3>();

            // ── Read back points ──────────────────────────────────────────
            var pts = new Vector3[count];
            m_pointsBuffer.GetData(pts, 0, 0, count);
            return pts;
        }
    }
}