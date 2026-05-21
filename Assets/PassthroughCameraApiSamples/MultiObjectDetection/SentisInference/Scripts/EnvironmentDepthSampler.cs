// Copyright (c) Meta Platforms, Inc. and affiliates.

using System;
using System.Collections.Generic;
using Meta.XR;
using Meta.XR.EnvironmentDepth;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace PassthroughCameraSamples.MultiObjectDetection
{
    /// <summary>
    /// EnvironmentDepthSampler.cs
    ///
    /// Reads depth data using the correct Meta Depth API texture pipeline,
    /// confirmed via Reddit post (r/vrdev/1pyehka) and SDK source files.
    ///
    /// KEY FACTS FROM REDDIT POST + SDK SOURCE:
    ///   • The depth texture is a Texture2DArray named _EnvironmentDepthTexture
    ///     (slice 0 = left eye, slice 1 = right eye)
    ///   • _PreprocessedEnvironmentDepthTexture exists only when
    ///     OcclusionShadersMode = SoftOcclusion — we don't require that mode
    ///   • We blit slice 0 of the Texture2DArray into a flat RenderTexture
    ///     so AsyncGPUReadback can read it as a simple float texture
    ///   • Pixel values in _EnvironmentDepthTexture are RAW NDC depth [0,1]
    ///   • Convert NDC → linear metres via _EnvironmentDepthZBufferParams:
    ///       linear = abs(zbuf.x / (ndc + zbuf.y))
    ///   • Unproject UV+depth → world via inverse of _EnvironmentDepthReprojectionMatrices[0]
    ///
    /// SETUP:
    ///   1. Add EnvironmentDepthManager to your scene and enable it.
    ///      OcclusionShadersMode can be None — we only need the raw depth texture.
    ///   2. Add this component alongside Box3DManager.
    ///   3. Assign [depthManager] and [cameraAccess] in the Inspector.
    /// </summary>
    public class EnvironmentDepthSampler : MonoBehaviour
    {
        // ── Inspector ────────────────────────────────────────────────────────

        [Header("References")]
        [SerializeField] private EnvironmentDepthManager m_depthManager;
        [SerializeField] private PassthroughCameraAccess m_cameraAccess;

        [Header("Depth Filtering")]
        [SerializeField] private float m_minValidDepth = 0.15f;
        [SerializeField] private float m_maxValidDepth = 10f;

        [Tooltip("Percentile for frontZ — ignores nearest N% of pixels (sensor noise).")]
        [SerializeField, Range(0f, 0.45f)] private float m_frontPercentile = 0.05f;

        [Tooltip("Percentile for backZ — ignores farthest N% of pixels (background wall).")]
        [SerializeField, Range(0.5f, 1f)] private float m_backPercentile = 0.92f;

        // ── Internal ─────────────────────────────────────────────────────────

        // We blit one slice of the Texture2DArray into this flat RT so
        // AsyncGPUReadback can read it without dealing with array textures.
        private RenderTexture m_sliceRT;

        // Blit material — samples slice 0 of a Tex2DArray into a flat RT
        private Material m_blitMaterial;

        // Shader property IDs
        private static readonly int DepthTexID = Shader.PropertyToID("_EnvironmentDepthTexture");
        private static readonly int ZBufParamsID = Shader.PropertyToID("_EnvironmentDepthZBufferParams");

        // ── Public result type ───────────────────────────────────────────────

        public struct BoxVolume
        {
            public Vector3 Center;
            public Vector3 Size;
            public Quaternion Rotation;
            public bool IsValid;
        }

        // ── Unity Lifecycle ──────────────────────────────────────────────────

        private void OnDestroy()
        {
            if (m_sliceRT != null) { m_sliceRT.Release(); Destroy(m_sliceRT); }
            if (m_blitMaterial != null) Destroy(m_blitMaterial);
        }

        // ── Public API ───────────────────────────────────────────────────────

        public void RequestDepthForRect(
            Rect normRect,
            Pose cameraPose,
            Vector2Int inputSize,
            Vector4 boundingBox,
            Action<BoxVolume> callback)
        {
            if (m_depthManager == null || !m_depthManager.IsDepthAvailable)
            {
                callback?.Invoke(new BoxVolume { IsValid = false });
                return;
            }

            // ── Get the Texture2DArray from global shader state ───────────────
            // _EnvironmentDepthTexture is a Texture2DArray (2 slices, one per eye).
            // GetGlobalTexture returns the base Texture — cast to Texture2DArray.
            var depthArray = Shader.GetGlobalTexture(DepthTexID) as Texture2DArray;
            if (depthArray == null)
            {
                // Fallback: some SDK versions set a plain RenderTexture instead
                var depthRT2 = Shader.GetGlobalTexture(DepthTexID) as RenderTexture;
                if (depthRT2 != null)
                {
                    ReadFromFlatRT(depthRT2, normRect, cameraPose, callback);
                    return;
                }
                callback?.Invoke(new BoxVolume { IsValid = false });
                return;
            }

            // ── Blit slice 0 (left eye) into a flat RenderTexture ────────────
            // AsyncGPUReadback does not support Texture2DArray slices directly.
            // We blit to a flat R32 RT first, then read that back.
            EnsureSliceRT(depthArray.width, depthArray.height);

            // Graphics.CopyTexture can copy one array slice to a flat texture
            // (both must have same size and format)
            Graphics.CopyTexture(
                depthArray, 0, 0,   // src: array slice 0, mip 0
                m_sliceRT, 0, 0);  // dst: flat RT, mip 0

            ReadFromFlatRT(m_sliceRT, normRect, cameraPose, callback);
        }

        // ── Internal helpers ─────────────────────────────────────────────────

        private void EnsureSliceRT(int width, int height)
        {
            if (m_sliceRT != null &&
                m_sliceRT.width == width &&
                m_sliceRT.height == height)
                return;

            if (m_sliceRT != null) { m_sliceRT.Release(); Destroy(m_sliceRT); }

            m_sliceRT = new RenderTexture(width, height, 0, RenderTextureFormat.RFloat)
            {
                name = "DepthSlice_Eye0",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };
            m_sliceRT.Create();
        }

        private void ReadFromFlatRT(
            RenderTexture rt,
            Rect normRect,
            Pose cameraPose,
            Action<BoxVolume> callback)
        {
            // Get ZBuffer params and reprojection matrix — snapshot before async
            Vector4 zbuf = Shader.GetGlobalVector(ZBufParamsID);
            Matrix4x4[] mats = Shader.GetGlobalMatrixArray("_EnvironmentDepthReprojectionMatrices");
            Matrix4x4 reprojInv = (mats != null && mats.Length > 0)
                                 ? mats[0].inverse
                                 : Matrix4x4.identity;

            int rtW = rt.width;
            int rtH = rt.height;

            // Crop region in pixels
            int px = Mathf.Clamp(Mathf.FloorToInt(normRect.x * rtW), 0, rtW - 1);
            int py = Mathf.Clamp(Mathf.FloorToInt(normRect.y * rtH), 0, rtH - 1);
            int pw = Mathf.Clamp(Mathf.CeilToInt(normRect.width * rtW), 1, rtW - px);
            int ph = Mathf.Clamp(Mathf.CeilToInt(normRect.height * rtH), 1, rtH - py);

            // Capture all value-type locals — nothing reference-type crosses the async boundary
            Rect rect = normRect;
            Pose pose = cameraPose;
            float minD = m_minValidDepth;
            float maxD = m_maxValidDepth;
            float frontPct = m_frontPercentile;
            float backPct = m_backPercentile;
            PassthroughCameraAccess pca = m_cameraAccess;
            Matrix4x4 inv = reprojInv;
            Vector4 zb = zbuf;

            AsyncGPUReadback.Request(
                rt,
                0,                      // mip level
                px, pw,                 // x offset, width
                py, ph,                 // y offset, height
                0, 1,                  // z offset, depth (flat texture = 1)
                TextureFormat.RFloat,
                req => OnReadbackComplete(
                    req, rect, pose,
                    minD, maxD, frontPct, backPct,
                    inv, zb, pca, callback));
        }

        // ── Readback handler ─────────────────────────────────────────────────

        private static void OnReadbackComplete(
            AsyncGPUReadbackRequest req,
            Rect normRect,
            Pose cameraPose,
            float minValidDepth,
            float maxValidDepth,
            float frontPercentile,
            float backPercentile,
            Matrix4x4 reprojInv,
            Vector4 zbufParams,
            PassthroughCameraAccess pca,
            Action<BoxVolume> callback)
        {
            if (req.hasError)
            {
                callback?.Invoke(new BoxVolume { IsValid = false });
                return;
            }

            NativeArray<float> pixels = req.GetData<float>();

            // ── NDC → linear depth ────────────────────────────────────────────
            // zbufParams.x = invDepthFactor (negative)
            // zbufParams.y = depthOffset
            // linear = abs(x / (ndc + y))
            float invFactor = zbufParams.x;
            float offset = zbufParams.y;

            var validDepths = new List<float>(pixels.Length);
            for (int i = 0; i < pixels.Length; i++)
            {
                float ndc = pixels[i];
                if (ndc <= 0f || ndc >= 1f) continue;           // sentinel: invalid/sky

                float denom = ndc + offset;
                if (Mathf.Abs(denom) < 1e-6f) continue;

                float linear = Mathf.Abs(invFactor / denom);
                if (linear >= minValidDepth && linear <= maxValidDepth)
                    validDepths.Add(linear);
            }

            if (validDepths.Count < 10)
            {
                callback?.Invoke(new BoxVolume { IsValid = false });
                return;
            }

            // ── Percentile filter → frontZ / backZ ───────────────────────────
            validDepths.Sort();
            int fi = Mathf.Clamp(Mathf.FloorToInt(validDepths.Count * frontPercentile), 0, validDepths.Count - 1);
            int bi = Mathf.Clamp(Mathf.FloorToInt(validDepths.Count * backPercentile), 0, validDepths.Count - 1);

            float frontZ = validDepths[fi];
            float backZ = validDepths[bi];
            if (backZ <= frontZ) backZ = frontZ + 0.01f;

            // ── Unproject 4 UV corners × front/back → 8 world points ─────────
            //
            // reprojInv is the inverse of (proj * view * trackingWorldToLocal).
            // Build a clip-space point from UV and back-calculated NDC depth,
            // then multiply by reprojInv to get world space.
            //
            // Back-calculate NDC Z from linear depth:
            //   linear = abs(invFactor / (ndcZ + offset))
            //   ndcZ   = (invFactor / ±linear) - offset
            //   Because invFactor < 0 and linear > 0:  ndcZ = invFactor / (-linear) - offset

            float ndcZFront = (invFactor / -frontZ) - offset;
            float ndcZBack = (invFactor / -backZ) - offset;

            float uMin = normRect.xMin; float uMax = normRect.xMax;
            float vMin = normRect.yMin; float vMax = normRect.yMax;

            Vector2[] uvCorners = {
                new(uMin, vMin), new(uMax, vMin),
                new(uMax, vMax), new(uMin, vMax),
            };

            var worldCorners = new List<Vector3>(8);
            foreach (var uv in uvCorners)
            {
                float ndcX = uv.x * 2f - 1f;
                float ndcY = uv.y * 2f - 1f;

                Vector4 clipF = new(ndcX, ndcY, ndcZFront, 1f);
                Vector4 clipB = new(ndcX, ndcY, ndcZBack, 1f);

                Vector4 wF = reprojInv * clipF;
                Vector4 wB = reprojInv * clipB;

                if (Mathf.Abs(wF.w) > 1e-6f)
                    worldCorners.Add(new Vector3(wF.x, wF.y, wF.z) / wF.w);
                if (Mathf.Abs(wB.w) > 1e-6f)
                    worldCorners.Add(new Vector3(wB.x, wB.y, wB.z) / wB.w);
            }

            // Supplement with PassthroughCameraAccess rays (real lens intrinsics)
            // for better horizontal/vertical accuracy
            if (pca != null && pca.IsPlaying)
            {
                Vector3 camFwd = cameraPose.rotation * Vector3.forward;
                foreach (var uv in uvCorners)
                {
                    Ray ray = pca.ViewportPointToRay(uv, cameraPose);
                    float dot = Vector3.Dot(ray.direction.normalized, camFwd);
                    if (Mathf.Abs(dot) < 0.01f) continue;
                    worldCorners.Add(ray.GetPoint(frontZ / dot));
                    worldCorners.Add(ray.GetPoint(backZ / dot));
                }
            }

            if (worldCorners.Count < 6)
            {
                callback?.Invoke(new BoxVolume { IsValid = false });
                return;
            }

            if (!DepthSampler.FitCameraAlignedAABB(worldCorners, cameraPose, out Vector3 center, out Vector3 size))
            {
                callback?.Invoke(new BoxVolume { IsValid = false });
                return;
            }

            callback?.Invoke(new BoxVolume
            {
                Center = center,
                Size = size,
                Rotation = cameraPose.rotation,
                IsValid = true
            });
        }
    }
}