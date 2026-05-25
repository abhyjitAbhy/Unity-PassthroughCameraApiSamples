// DepthTextureProvider.cs — RUNTIME SAFE
//
// FIXES applied on top of previous version:
//  - Removed 'using UnityEditor' and ShaderUtil.CreateShaderAsset (Editor-only API,
//    causes CS0103 in player/Quest builds).
//  - The Texture2DArray blit shader is now loaded via a different strategy:
//    We use Graphics.CopyTexture (sub-resource copy) as the primary path since
//    it's zero-overhead and works on Quest's GLES3/Vulkan. The material blit
//    is kept as a fallback using Shader.Find("Hidden/BlitCopy") which is a
//    Unity built-in that is always present.
//  - All other logic (ZBufferParams, ReprojectionMatrix, RT resize) preserved.

using Meta.XR.EnvironmentDepth;
using UnityEngine;

namespace PassthroughCameraSamples.MultiObjectDetection
{
    public class DepthTextureProvider : MonoBehaviour
    {
        // ── shader property IDs matching Meta runtime globals exactly ────────
        private static readonly int s_PreprocessedDepthTex =
            Shader.PropertyToID("_PreprocessedEnvironmentDepthTexture");
        private static readonly int s_ZBufferParams =
            Shader.PropertyToID("_EnvironmentDepthZBufferParams");
        private static readonly int s_ReprojMatrix =
            Shader.PropertyToID("_EnvironmentDepthReprojection");

        // ── public output consumed by PointCloudBuilder ──────────────────────
        [HideInInspector] public RenderTexture DepthRT;
        [HideInInspector] public Matrix4x4 ReprojectionMatrix;
        [HideInInspector] public Vector4 ZBufferParams;

        // ── internals ────────────────────────────────────────────────────────
        private EnvironmentDepthManager m_depthManager;
        private Material m_blitMaterial;
        private bool m_useCopyTexture; // true = fast path, false = material blit

        void Awake()
        {
            // Ensure EnvironmentDepthManager is present
            m_depthManager = FindObjectOfType<EnvironmentDepthManager>();
            if (m_depthManager == null)
            {
                var go = new GameObject("EnvironmentDepthManager");
                m_depthManager = go.AddComponent<EnvironmentDepthManager>();
                Debug.LogWarning("[DepthTextureProvider] Created EnvironmentDepthManager automatically.");
            }

            // Determine blit strategy.
            // Graphics.CopyTexture with sub-resource indices is the preferred path on Quest
            // (GLES3 / Vulkan both support it). We'll confirm the texture is a Texture2DArray
            // at runtime and fall back to a material blit if CopyTexture isn't available.
            //
            // For the material blit fallback we use "Hidden/BlitCopy", a Unity built-in
            // that simply outputs _MainTex. Because the source is a Texture2DArray we
            // need a UNITY_SAMPLE_TEX2DARRAY call — but Hidden/BlitCopy treats it as a
            // plain Texture2D and only reads slice 0 on platforms that alias arrays.
            // On Quest (Vulkan/GLES3) CopyTexture sub-resource IS supported, so the
            // fallback path is mainly for Editor testing with a mock flat texture.
            m_useCopyTexture = SystemInfo.copyTextureSupport.HasFlag(
                UnityEngine.Rendering.CopyTextureSupport.TextureToRT);

            if (!m_useCopyTexture)
            {
                // Fallback: blit via Hidden/BlitCopy (always included in Unity builds)
                var shader = Shader.Find("Hidden/BlitCopy");
                if (shader == null) shader = Shader.Find("Unlit/Texture");
                m_blitMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            }
        }

        void Update()
        {
            // ── 1. Get the global depth texture (Texture2DArray set by Meta runtime)
            var depthTex = Shader.GetGlobalTexture(s_PreprocessedDepthTex);
            if (depthTex == null) return; // Not ready yet — normal for first few frames

            // ── 2. Read the accompanying matrices / params
            ZBufferParams = Shader.GetGlobalVector(s_ZBufferParams);
            ReprojectionMatrix = Shader.GetGlobalMatrix(s_ReprojMatrix);

            // ── 3. (Re)create the single-layer destination RT if size changed
            if (DepthRT == null
                || DepthRT.width != depthTex.width
                || DepthRT.height != depthTex.height)
            {
                if (DepthRT != null) DepthRT.Release();
                DepthRT = new RenderTexture(depthTex.width, depthTex.height, 0,
                    RenderTextureFormat.RFloat)
                {
                    name = "DepthRT_Slice0",
                    enableRandomWrite = true
                };
                DepthRT.Create();
            }

            // ── 4. Copy slice 0 of the Texture2DArray into DepthRT
            if (m_useCopyTexture && depthTex is Texture2DArray)
            {
                // Fast zero-overhead path: copy sub-resource (array slice 0, mip 0)
                // directly to the RenderTexture sub-resource.
                Graphics.CopyTexture(depthTex, 0 /*srcElement*/, 0 /*srcMip*/,
                                     DepthRT, 0 /*dstElement*/, 0 /*dstMip*/);
            }
            else
            {
                // Fallback: plain blit works when depthTex is already a flat Texture2D
                // (e.g. in-Editor mock) or when CopyTexture sub-resource isn't supported.
                if (m_blitMaterial != null)
                    Graphics.Blit(depthTex, DepthRT, m_blitMaterial);
                else
                    Graphics.Blit(depthTex, DepthRT);
            }
        }

        void OnDestroy()
        {
            if (DepthRT != null) { DepthRT.Release(); DepthRT = null; }
            if (m_blitMaterial != null) Destroy(m_blitMaterial);
        }

        /// <summary>
        /// Linearize a raw [0,1] depth value using Quest ZBufferParams.
        /// Result is metric distance in metres from the camera.
        /// </summary>
        public static float LinearizeDepth(float rawDepth, Vector4 zp)
            => 1f / (zp.z * rawDepth + zp.w);
    }
}