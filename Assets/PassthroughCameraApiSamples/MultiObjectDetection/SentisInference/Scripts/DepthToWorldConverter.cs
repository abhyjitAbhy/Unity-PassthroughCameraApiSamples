// DepthToWorldConverter.cs
using UnityEngine;

public static class DepthToWorldConverter
{
    /// <summary>
    /// Convert a single depth pixel to world space.
    /// </summary>
    /// <param name="uv">Normalized [0,1] UV in depth texture</param>
    /// <param name="rawDepth">Raw depth value from texture [0,1]</param>
    /// <param name="invProj">Inverse projection matrix of the eye camera</param>
    /// <param name="invView">Inverse view matrix (camera-to-world)</param>
    /// <param name="reprojMatrix">_EnvironmentDepthReprojection from shader global</param>
    /// <param name="zParams">_EnvironmentDepthZBufferParams</param>
    public static Vector3 DepthUVToWorld(
        Vector2 uv, float rawDepth,
        Matrix4x4 invProj, Matrix4x4 invView,
        Matrix4x4 reprojMatrix, Vector4 zParams)
    {
        // Step 1: Linearize depth
        float linearDepth = 1f / (zParams.z * rawDepth + zParams.w);

        // Step 2: Convert UV to NDC [-1, 1]
        Vector4 ndc = new Vector4(uv.x * 2f - 1f, uv.y * 2f - 1f,
                                  rawDepth * 2f - 1f, 1f);

        // Step 3: Unproject to eye space
        Vector4 eyePos = invProj * ndc;
        eyePos /= eyePos.w;

        // Step 4: Apply reprojection (handles depth-vs-color temporal offset)
        eyePos = reprojMatrix * eyePos;

        // Step 5: Eye space → world space
        Vector4 worldPos = invView * eyePos;
        return new Vector3(worldPos.x, worldPos.y, worldPos.z);
    }

    /// <summary>
    /// Bulk CPU conversion; prefer the compute shader version for real-time use.
    /// </summary>
    public static Vector3[] ConvertMaskedPixels(
        float[] depthPixels, byte[] maskPixels,
        int width, int height,
        Matrix4x4 invProj, Matrix4x4 invView,
        Matrix4x4 reprojMatrix, Vector4 zParams,
        float maxDepthMeters = 4f)
    {
        var pts = new System.Collections.Generic.List<Vector3>(width * height / 4);
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int i = y * width + x;
                if (maskPixels[i] == 0) continue;
                float raw = depthPixels[i];
                if (raw <= 0 || raw >= 1) continue;

                float linearD = 1f / (zParams.z * raw + zParams.w);
                if (linearD > maxDepthMeters) continue;

                var uv = new Vector2((float)x / width, (float)y / height);
                var pt = DepthUVToWorld(uv, raw, invProj, invView, reprojMatrix, zParams);
                pts.Add(pt);
            }
        return pts.ToArray();
    }
}