// Copyright (c) Meta Platforms, Inc. and affiliates.

using System.Buffers;
using System.Collections.Generic;
using Meta.XR;
using UnityEngine;

namespace PassthroughCameraSamples.MultiObjectDetection
{
    /// <summary>
    /// DepthSampler — static utility class for depth-based 3D point sampling and box fitting.
    ///
    /// New in this version (Gaps 1–2, 5–6, 7):
    ///   Gap 1 — SampleGridMaskGated: mask-gated + edge-excluded raycast sampling.
    ///   Gap 2 — SampleGridDepthTexture: hardware depth texture primary sampling path.
    ///   Gap 5 — ClusterFilter: hard depth-range pre-pass before any statistics.
    ///   Gap 6 — MeasurePhysicalSize: median-depth edge projection for W/H.
    ///   Gap 7 — ClusterFilter: ArrayPool<float> instead of array allocation;
    ///            optional inlierReuse parameter to avoid List<Vector3> alloc.
    ///
    /// All existing public method signatures are preserved unchanged.
    /// </summary>
    public static class DepthSampler
    {
        // ── SampleGrid ───────────────────────────────────────────────────────

        public static List<Vector3> SampleGrid(
            Rect normRect,
            PassthroughCameraAccess pca,
            EnvironmentRayCastSampleManager rm,
            int gridSize,
            Pose cameraPose,
            List<Vector3> reuse = null,
            bool jitter = true)
        {
            var hits = reuse ?? new List<Vector3>(gridSize * gridSize);
            hits.Clear();

            if (pca == null || !pca.IsPlaying || rm == null)
                return hits;

            float step = gridSize > 1 ? 1f / (gridSize - 1) : 0f;
            float jitterRange = gridSize > 1 ? step * 0.4f : 0f;

            for (int xi = 0; xi < gridSize; xi++)
            {
                for (int yi = 0; yi < gridSize; yi++)
                {
                    float fx = xi * step;
                    float fy = yi * step;

                    if (jitter && gridSize > 2)
                    {
                        fx += Random.Range(-jitterRange, jitterRange);
                        fy += Random.Range(-jitterRange, jitterRange);
                    }

                    float u = Mathf.Clamp01(normRect.x + fx * normRect.width);
                    float v = Mathf.Clamp01(normRect.y + fy * normRect.height);

                    Ray ray = pca.ViewportPointToRay(new Vector2(u, v), cameraPose);

                    Vector3? hit = rm.Raycast(ray);
                    if (hit.HasValue)
                        hits.Add(hit.Value);
                }
            }
            return hits;
        }

        // ── SampleGridMaskGated (Gap 1) ──────────────────────────────────────

        /// <summary>
        /// Mask-gated grid sampling with edge exclusion.
        /// Shrinks normRect inward by edgeExclusionFraction, then for each grid UV
        /// checks the optional segmentation mask before firing a raycast. Only pixels
        /// that pass the mask test (and the geometry raycast) are returned.
        /// </summary>
        /// <param name="normRect">Normalised bounding rect [0,1] from YOLO.</param>
        /// <param name="mask">Optional float mask array. null = edge-exclusion only.</param>
        /// <param name="maskWidth">Pixel width of mask.</param>
        /// <param name="maskHeight">Pixel height of mask.</param>
        /// <param name="maskAreLogits">True = threshold at 0; false = threshold at 0.5.</param>
        /// <param name="pca">PassthroughCameraAccess for ray generation.</param>
        /// <param name="rm">EnvironmentRayCastSampleManager for geometry hits.</param>
        /// <param name="gridSize">Grid dimension (gridSize × gridSize samples).</param>
        /// <param name="cameraPose">Pose used for ray generation.</param>
        /// <param name="reuse">Optional pre-allocated list to fill; avoids allocation.</param>
        /// <param name="jitter">Add sub-cell jitter to reduce aliasing.</param>
        /// <param name="edgeExclusionFraction">Fractional inset on each side (default 0.08).</param>
        public static List<Vector3> SampleGridMaskGated(
            Rect normRect,
            float[] mask,
            int maskWidth,
            int maskHeight,
            bool maskAreLogits,
            PassthroughCameraAccess pca,
            EnvironmentRayCastSampleManager rm,
            int gridSize,
            Pose cameraPose,
            List<Vector3> reuse = null,
            bool jitter = true,
            float edgeExclusionFraction = 0.08f)
        {
            var hits = reuse ?? new List<Vector3>(gridSize * gridSize);
            hits.Clear();

            if (pca == null || !pca.IsPlaying || rm == null)
                return hits;

            // Shrink rect inward to discard edge depth transitions
            float insetX = normRect.width * edgeExclusionFraction;
            float insetY = normRect.height * edgeExclusionFraction;
            Rect insetRect = new Rect(
                normRect.x + insetX,
                normRect.y + insetY,
                normRect.width - 2f * insetX,
                normRect.height - 2f * insetY);

            if (insetRect.width <= 0f || insetRect.height <= 0f)
                return hits;

            float step = gridSize > 1 ? 1f / (gridSize - 1) : 0f;
            float jitterRange = gridSize > 1 ? step * 0.4f : 0f;

            for (int xi = 0; xi < gridSize; xi++)
            {
                for (int yi = 0; yi < gridSize; yi++)
                {
                    float fx = xi * step;
                    float fy = yi * step;

                    if (jitter && gridSize > 2)
                    {
                        fx += Random.Range(-jitterRange, jitterRange);
                        fy += Random.Range(-jitterRange, jitterRange);
                    }

                    float u = Mathf.Clamp01(insetRect.x + fx * insetRect.width);
                    float v = Mathf.Clamp01(insetRect.y + fy * insetRect.height);

                    // Mask gate — check before raycasting to save GPU round-trips
                    if (mask != null)
                    {
                        int maskX = Mathf.Clamp((int)(u * maskWidth), 0, maskWidth - 1);
                        int maskY = Mathf.Clamp((int)(v * maskHeight), 0, maskHeight - 1);
                        int maskIdx = maskY * maskWidth + maskX;
                        bool passes = maskAreLogits ? mask[maskIdx] > 0f : mask[maskIdx] > 0.5f;
                        if (!passes) continue;
                    }

                    Ray ray = pca.ViewportPointToRay(new Vector2(u, v), cameraPose);
                    Vector3? hit = rm.Raycast(ray);
                    if (hit.HasValue)
                        hits.Add(hit.Value);
                }
            }
            return hits;
        }

        // ── SampleGridDepthTexture (Gap 2) ───────────────────────────────────

        /// <summary>
        /// Hardware depth-texture sampling path. No raycasting — reads depth at each
        /// grid UV via a VP-matrix projection into the depth buffer. Zero raycast cost.
        /// Uses DepthFrameData.CameraPose (NOT current frame pose) to avoid temporal
        /// misalignment between the depth snapshot and its VP matrices.
        /// </summary>
        /// <param name="normRect">Normalised bounding rect [0,1] from YOLO.</param>
        /// <param name="depthBuffer">ArrayPool copy of DepthFrameData.DepthTexturePixels.</param>
        /// <param name="vpMatrix">DepthFrameData.ViewProjectionMatrix (length 2).</param>
        /// <param name="eyeIdx">0 = left, 1 = right.</param>
        /// <param name="texSize">DepthTextureAccess.TextureSize (square, dynamic).</param>
        /// <param name="pca">PassthroughCameraAccess for ray generation.</param>
        /// <param name="framePose">DepthFrameData.CameraPose — must match the VP matrix.</param>
        /// <param name="gridSize">Grid dimension.</param>
        /// <param name="reuse">Optional pre-allocated list to fill.</param>
        /// <param name="jitter">Add sub-cell jitter.</param>
        /// <param name="edgeExclusionFraction">Fractional inset on each side (default 0.08).</param>
        public static List<Vector3> SampleGridDepthTexture(
            Rect normRect,
            float[] depthBuffer,
            Matrix4x4[] vpMatrix,
            int eyeIdx,
            int texSize,
            PassthroughCameraAccess pca,
            Pose framePose,
            int gridSize,
            List<Vector3> reuse = null,
            bool jitter = true,
            float edgeExclusionFraction = 0.08f)
        {
            var hits = reuse ?? new List<Vector3>(gridSize * gridSize);
            hits.Clear();

            if (pca == null || !pca.IsPlaying || depthBuffer == null || vpMatrix == null
                || vpMatrix.Length <= eyeIdx || texSize <= 0)
                return hits;

            // Shrink rect inward for edge exclusion
            float insetX = normRect.width * edgeExclusionFraction;
            float insetY = normRect.height * edgeExclusionFraction;
            Rect insetRect = new Rect(
                normRect.x + insetX,
                normRect.y + insetY,
                normRect.width - 2f * insetX,
                normRect.height - 2f * insetY);

            if (insetRect.width <= 0f || insetRect.height <= 0f)
                return hits;

            float step = gridSize > 1 ? 1f / (gridSize - 1) : 0f;
            float jitterRange = gridSize > 1 ? step * 0.4f : 0f;
            Matrix4x4 vp = vpMatrix[eyeIdx];
            int eyeOffset = eyeIdx * texSize * texSize;

            for (int xi = 0; xi < gridSize; xi++)
            {
                for (int yi = 0; yi < gridSize; yi++)
                {
                    float fx = xi * step;
                    float fy = yi * step;

                    if (jitter && gridSize > 2)
                    {
                        fx += Random.Range(-jitterRange, jitterRange);
                        fy += Random.Range(-jitterRange, jitterRange);
                    }

                    float u = Mathf.Clamp01(insetRect.x + fx * insetRect.width);
                    float v = Mathf.Clamp01(insetRect.y + fy * insetRect.height);

                    Ray ray = pca.ViewportPointToRay(new Vector2(u, v), framePose);

                    // Project a unit point along the ray into clip space
                    Vector4 world1m = new Vector4(
                        ray.origin.x + ray.direction.x,
                        ray.origin.y + ray.direction.y,
                        ray.origin.z + ray.direction.z,
                        1f);
                    Vector4 clip = vp * world1m;

                    if (clip.w <= 0f) continue;

                    // NDC -> UV
                    float ndcX = clip.x / clip.w;
                    float ndcY = clip.y / clip.w;
                    float uvX = ndcX * 0.5f + 0.5f;
                    float uvY = ndcY * 0.5f + 0.5f;

                    int sx = Mathf.Clamp((int)(uvX * texSize), 0, texSize - 1);
                    int sy = Mathf.Clamp((int)(uvY * texSize), 0, texSize - 1);
                    int idx = eyeOffset + sy * texSize + sx;

                    float depth = depthBuffer[idx];

                    // Hard-reject invalid / sentinel depth values before any arithmetic
                    if (depth <= 0f || depth > 20f || float.IsInfinity(depth) || float.IsNaN(depth))
                        continue;

                    Vector3 worldPoint = ray.origin + ray.direction * depth;
                    hits.Add(worldPoint);
                }
            }
            return hits;
        }

        // ── ClusterFilter (two-pass) — Gap 5 hard guard, Gap 7 ArrayPool ─────

        /// <summary>
        /// Two-pass median depth cluster filter.
        /// Pre-pass: hard-rejects points with physically invalid distances (sentinel
        /// values from the depth hardware) before any statistics are computed —
        /// prevents sentinels from shifting the median and admitting background inliers.
        /// Uses ArrayPool to avoid per-call float[] allocation.
        /// </summary>
        /// <param name="points">Input world-space point cloud.</param>
        /// <param name="cameraPos">Camera world position used to compute distances.</param>
        /// <param name="toleranceM">Max deviation from median to count as same surface (metres).</param>
        /// <param name="minCount">Minimum inliers required; returns empty list if below.</param>
        /// <param name="inlierReuse">Optional pre-allocated output list (avoids allocation).</param>
        public static List<Vector3> ClusterFilter(
            List<Vector3> points,
            Vector3 cameraPos,
            float toleranceM = 0.12f,
            int minCount = 6,
            List<Vector3> inlierReuse = null)
        {
            if (points == null || points.Count < minCount)
            {
                if (inlierReuse != null) { inlierReuse.Clear(); return inlierReuse; }
                return new List<Vector3>();
            }

            // ── Gap 5: Hard pre-pass — remove physically invalid points ───────
            // Prevents sentinel depth values (0, Inf, NaN) from entering Median()
            // and shifting the tolerance band so background leaks through as inliers.
            var validBuf = new List<Vector3>(points.Count);
            foreach (var p in points)
            {
                float d = Vector3.Distance(cameraPos, p);
                if (d > 0f && d <= 20f && !float.IsInfinity(d) && !float.IsNaN(d))
                    validBuf.Add(p);
            }

            if (validBuf.Count < minCount)
            {
                if (inlierReuse != null) inlierReuse.Clear();
                return inlierReuse ?? new List<Vector3>();
            }

            // ── Gap 7: ArrayPool for depth array — no per-call heap allocation ─
            float[] depths = ArrayPool<float>.Shared.Rent(validBuf.Count);
            try
            {
                // Pass 1
                for (int i = 0; i < validBuf.Count; i++)
                    depths[i] = Vector3.Distance(cameraPos, validBuf[i]);

                float median1 = MedianSlice(depths, validBuf.Count);

                var pass1 = new List<Vector3>(validBuf.Count);
                var depths1 = ArrayPool<float>.Shared.Rent(validBuf.Count);
                int depths1Count = 0;
                try
                {
                    for (int i = 0; i < validBuf.Count; i++)
                    {
                        if (Mathf.Abs(depths[i] - median1) <= toleranceM)
                        {
                            pass1.Add(validBuf[i]);
                            depths1[depths1Count++] = depths[i];
                        }
                    }

                    if (pass1.Count < minCount)
                    {
                        if (inlierReuse != null) inlierReuse.Clear();
                        return inlierReuse ?? new List<Vector3>();
                    }

                    // Pass 2 (tighter)
                    float median2 = MedianSlice(depths1, depths1Count);
                    float tight = toleranceM * 0.55f;

                    var result = inlierReuse ?? new List<Vector3>(pass1.Count);
                    result.Clear();
                    for (int i = 0; i < pass1.Count; i++)
                    {
                        if (Mathf.Abs(depths1[i] - median2) <= tight)
                            result.Add(pass1[i]);
                    }

                    if (result.Count < minCount)
                    {
                        result.Clear();
                        result.AddRange(pass1);
                    }

                    return result.Count >= minCount ? result : (result.Count == 0 ? result : result);
                }
                finally
                {
                    ArrayPool<float>.Shared.Return(depths1, clearArray: false);
                }
            }
            finally
            {
                ArrayPool<float>.Shared.Return(depths, clearArray: false);
            }
        }

        // ── MeasurePhysicalSize (Gap 6) ──────────────────────────────────────

        /// <summary>
        /// Computes physically grounded width and height by projecting the bounding box
        /// edges as rays at the median inlier depth. Independent of point density —
        /// correct even with only a handful of inlier points.
        /// </summary>
        /// <param name="normRect">Normalised bounding rect [0,1] from YOLO.</param>
        /// <param name="inliers">Cluster-filtered inlier point cloud.</param>
        /// <param name="pca">PassthroughCameraAccess for ray generation.</param>
        /// <param name="framePose">Pose at time of depth capture.</param>
        /// <param name="physicalWidth">Output: physical width in metres.</param>
        /// <param name="physicalHeight">Output: physical height in metres.</param>
        public static void MeasurePhysicalSize(
            Rect normRect,
            List<Vector3> inliers,
            PassthroughCameraAccess pca,
            Pose framePose,
            out float physicalWidth,
            out float physicalHeight)
        {
            physicalWidth = 0.01f;
            physicalHeight = 0.01f;

            if (inliers == null || inliers.Count == 0 || pca == null)
                return;

            // Compute median depth from inliers
            float[] dists = ArrayPool<float>.Shared.Rent(inliers.Count);
            try
            {
                for (int i = 0; i < inliers.Count; i++)
                    dists[i] = Vector3.Distance(framePose.position, inliers[i]);
                float medianDepth = MedianSlice(dists, inliers.Count);

                if (medianDepth <= 0f || medianDepth > 20f)
                    return;

                // 5 UVs: center, left, right, top, bottom
                float cx = normRect.x + normRect.width * 0.5f;
                float cy = normRect.y + normRect.height * 0.5f;

                Ray rLeft = pca.ViewportPointToRay(new Vector2(normRect.xMin, cy), framePose);
                Ray rRight = pca.ViewportPointToRay(new Vector2(normRect.xMax, cy), framePose);
                Ray rTop = pca.ViewportPointToRay(new Vector2(cx, normRect.yMax), framePose);
                Ray rBottom = pca.ViewportPointToRay(new Vector2(cx, normRect.yMin), framePose);

                Vector3 wLeft = rLeft.origin + rLeft.direction * medianDepth;
                Vector3 wRight = rRight.origin + rRight.direction * medianDepth;
                Vector3 wTop = rTop.origin + rTop.direction * medianDepth;
                Vector3 wBottom = rBottom.origin + rBottom.direction * medianDepth;

                physicalWidth = Mathf.Max(Vector3.Distance(wLeft, wRight), 0.01f);
                physicalHeight = Mathf.Max(Vector3.Distance(wTop, wBottom), 0.01f);
            }
            finally
            {
                ArrayPool<float>.Shared.Return(dists, clearArray: false);
            }
        }

        // ── FitPCA6DoF ───────────────────────────────────────────────────────
        //
        // MARKERLESS 6-DoF POSE ESTIMATION
        //
        // WHAT IT DOES:
        //   Runs Power Iteration PCA on the world-space inlier point cloud to
        //   find the three principal axes of the object's geometry. These axes
        //   define a rotation that makes the wireframe cuboid face the object's
        //   dominant surface — completely markerless, no model needed.
        //
        // HOW PCA GIVES US POSE:
        //   The 3x3 covariance matrix C of the centred points encodes how the
        //   cloud is spread in 3D. Its eigenvectors are the directions of maximum
        //   spread (PC0 = longest, PC1 = second, PC2 = normal/depth). We build a
        //   right-handed frame {PC0, PC1, PC0xPC1} then snap Y to world-up so
        //   the box never tilts with camera pitch.
        //
        // GRAVITY ALIGNMENT POST-PASS:
        //   After PCA we pick whichever principal axis is closest to world-up and
        //   force it to be exactly (0,1,0). The remaining two axes are
        //   re-orthogonalised. This keeps the box upright on flat surfaces while
        //   still capturing the horizontal facing direction from the point cloud.
        //
        // CAMERA-FACING Z:
        //   The Z axis (depth face of the cuboid) is flipped if it points away
        //   from the camera so the front face always faces the viewer.
        //
        // RETURNS false and falls back to FitGravityAlignedAABB if pts is too
        // small or PCA degenerates (collinear points, etc.).

        public static bool FitPCA6DoF(
            List<Vector3> pts,
            Pose cameraPose,
            out Vector3 center,
            out Vector3 size,
            out Quaternion rotation)
        {
            center = Vector3.zero;
            size = Vector3.one * 0.01f;
            rotation = Quaternion.identity;

            if (pts == null || pts.Count < 3)
                return false;

            // ── 1. Centroid ──────────────────────────────────────────────────
            Vector3 mean = Vector3.zero;
            for (int i = 0; i < pts.Count; i++) mean += pts[i];
            mean /= pts.Count;

            // ── 2. 3x3 covariance matrix (symmetric, upper triangle) ─────────
            float c00 = 0, c01 = 0, c02 = 0, c11 = 0, c12 = 0, c22 = 0;
            for (int i = 0; i < pts.Count; i++)
            {
                float dx = pts[i].x - mean.x;
                float dy = pts[i].y - mean.y;
                float dz = pts[i].z - mean.z;
                c00 += dx * dx; c01 += dx * dy; c02 += dx * dz;
                c11 += dy * dy; c12 += dy * dz;
                c22 += dz * dz;
            }
            float invN = 1f / pts.Count;
            c00 *= invN; c01 *= invN; c02 *= invN;
            c11 *= invN; c12 *= invN; c22 *= invN;

            // ── 3. Power Iteration: dominant eigenvector (PC0) ──────────────
            // Seed with horizontal camera-forward so the primary axis tends to
            // align with the face the user is looking at.
            Vector3 seed0 = HorizontalCameraForward(cameraPose);
            Vector3 pc0 = PowerIterate(c00, c01, c02, c11, c12, c22, seed0);

            // ── 4. Deflate and find secondary eigenvector (PC1) ──────────────
            DeflateCovariance(ref c00, ref c01, ref c02,
                              ref c11, ref c12, ref c22, pc0);
            Vector3 seed1 = cameraPose.rotation * Vector3.right;
            Vector3 pc1 = PowerIterate(c00, c01, c02, c11, c12, c22, seed1);

            // ── 5. Orthonormal basis ─────────────────────────────────────────
            Vector3 pc2 = Vector3.Cross(pc0, pc1).normalized;
            pc1 = Vector3.Cross(pc2, pc0).normalized; // re-orthogonalise

            // Degenerate check: collinear points produce a zero cross-product
            if (pc2.sqrMagnitude < 0.5f)
                return FitGravityAlignedAABB(pts, cameraPose, out center, out size, out rotation);

            // ── 6. Gravity alignment ─────────────────────────────────────────
            // Choose which PCA axis is closest to world-up and reassign it to
            // exactly (0,1,0). Re-derive the other two via cross products.
            float dotY0 = Mathf.Abs(Vector3.Dot(pc0, Vector3.up));
            float dotY1 = Mathf.Abs(Vector3.Dot(pc1, Vector3.up));
            float dotY2 = Mathf.Abs(Vector3.Dot(pc2, Vector3.up));

            Vector3 axisX, axisY, axisZ;
            Vector3 worldUp = Vector3.up;

            if (dotY0 >= dotY1 && dotY0 >= dotY2)
            {
                // pc0 most vertical -> becomes Y
                axisY = worldUp;
                axisZ = Vector3.Cross(pc1, axisY).normalized;
                if (axisZ.sqrMagnitude < 0.001f) axisZ = pc2;
                axisX = Vector3.Cross(axisY, axisZ).normalized;
            }
            else if (dotY1 >= dotY0 && dotY1 >= dotY2)
            {
                // pc1 most vertical -> becomes Y
                axisY = worldUp;
                axisZ = Vector3.Cross(pc0, axisY).normalized;
                if (axisZ.sqrMagnitude < 0.001f) axisZ = pc2;
                axisX = Vector3.Cross(axisY, axisZ).normalized;
            }
            else
            {
                // pc2 most vertical -> becomes Y
                axisY = worldUp;
                axisZ = Vector3.Cross(pc0, axisY).normalized;
                if (axisZ.sqrMagnitude < 0.001f) axisZ = pc1;
                axisX = Vector3.Cross(axisY, axisZ).normalized;
            }

            // ── 7. Camera-facing Z ───────────────────────────────────────────
            // Flip Z so the front face of the cuboid always faces the camera.
            Vector3 toCamera = cameraPose.position - mean;
            if (Vector3.Dot(axisZ, toCamera) < 0f)
            {
                axisZ = -axisZ;
                axisX = -axisX; // keep right-handed
            }

            // Final degenerate guard
            if (axisX.sqrMagnitude < 0.5f || axisZ.sqrMagnitude < 0.5f)
                return FitGravityAlignedAABB(pts, cameraPose, out center, out size, out rotation);

            // ── 8. Build Quaternion from axes ────────────────────────────────
            rotation = QuaternionFromAxes(axisX, axisY, axisZ);

            // ── 9. Project points into PCA frame -> AABB extents ─────────────
            Quaternion invRot = Quaternion.Inverse(rotation);
            Vector3 mn = invRot * (pts[0] - mean);
            Vector3 mx = mn;
            for (int i = 1; i < pts.Count; i++)
            {
                Vector3 local = invRot * (pts[i] - mean);
                mn = Vector3.Min(mn, local);
                mx = Vector3.Max(mx, local);
            }

            // Shift center to AABB centroid in local frame
            Vector3 localOffset = (mn + mx) * 0.5f;
            center = mean + rotation * localOffset;

            size = mx - mn;
            size.x = Mathf.Max(size.x, 0.01f);
            size.y = Mathf.Max(size.y, 0.01f);
            size.z = Mathf.Max(size.z, 0.01f);

            return true;
        }

        // ── PCA internals ────────────────────────────────────────────────────

        /// <summary>
        /// Power iteration (32 steps) on the symmetric 3x3 covariance matrix.
        /// Returns the dominant eigenvector, normalised.
        /// </summary>
        private static Vector3 PowerIterate(
            float c00, float c01, float c02,
            float c11, float c12, float c22,
            Vector3 seed)
        {
            Vector3 v = seed.sqrMagnitude > 0.001f ? seed.normalized : Vector3.right;
            for (int k = 0; k < 32; k++)
            {
                float nx = c00 * v.x + c01 * v.y + c02 * v.z;
                float ny = c01 * v.x + c11 * v.y + c12 * v.z;
                float nz = c02 * v.x + c12 * v.y + c22 * v.z;
                float mag = Mathf.Sqrt(nx * nx + ny * ny + nz * nz);
                if (mag < 1e-8f) break;
                v = new Vector3(nx / mag, ny / mag, nz / mag);
            }
            return v;
        }

        /// <summary>
        /// Hotelling deflation: removes the contribution of eigenvector v
        /// (eigenvalue lambda = v^T C v) so the next power iteration
        /// converges to the second eigenvector instead of the first.
        /// </summary>
        private static void DeflateCovariance(
            ref float c00, ref float c01, ref float c02,
            ref float c11, ref float c12, ref float c22,
            Vector3 v)
        {
            float lam = c00 * v.x * v.x + c11 * v.y * v.y + c22 * v.z * v.z
                      + 2f * (c01 * v.x * v.y + c02 * v.x * v.z + c12 * v.y * v.z);
            c00 -= lam * v.x * v.x;
            c01 -= lam * v.x * v.y;
            c02 -= lam * v.x * v.z;
            c11 -= lam * v.y * v.y;
            c12 -= lam * v.y * v.z;
            c22 -= lam * v.z * v.z;
        }

        /// <summary>Camera forward stripped of pitch/roll (horizontal only).</summary>
        private static Vector3 HorizontalCameraForward(Pose cameraPose)
        {
            Vector3 fwd = cameraPose.rotation * Vector3.forward;
            Vector3 flat = new Vector3(fwd.x, 0f, fwd.z);
            if (flat.sqrMagnitude > 0.01f) return flat.normalized;
            // Fallback: camera pointing straight up/down -> use camera right
            Vector3 right = cameraPose.rotation * Vector3.right;
            return new Vector3(right.x, 0f, right.z).normalized;
        }

        /// <summary>
        /// Build a Quaternion whose local axes are axisX (right), axisY (up),
        /// axisZ (forward), using Shepperd's trace method.
        /// Assumes the three vectors form an orthonormal right-handed basis.
        /// </summary>
        private static Quaternion QuaternionFromAxes(Vector3 axisX, Vector3 axisY, Vector3 axisZ)
        {
            // Columns of the rotation matrix
            float m00 = axisX.x, m10 = axisX.y, m20 = axisX.z;
            float m01 = axisY.x, m11 = axisY.y, m21 = axisY.z;
            float m02 = axisZ.x, m12 = axisZ.y, m22 = axisZ.z;

            float trace = m00 + m11 + m22;
            float qw, qx, qy, qz;

            if (trace > 0f)
            {
                float s = 0.5f / Mathf.Sqrt(trace + 1f);
                qw = 0.25f / s;
                qx = (m21 - m12) * s;
                qy = (m02 - m20) * s;
                qz = (m10 - m01) * s;
            }
            else if (m00 > m11 && m00 > m22)
            {
                float s = 2f * Mathf.Sqrt(1f + m00 - m11 - m22);
                qw = (m21 - m12) / s;
                qx = 0.25f * s;
                qy = (m01 + m10) / s;
                qz = (m02 + m20) / s;
            }
            else if (m11 > m22)
            {
                float s = 2f * Mathf.Sqrt(1f + m11 - m00 - m22);
                qw = (m02 - m20) / s;
                qx = (m01 + m10) / s;
                qy = 0.25f * s;
                qz = (m12 + m21) / s;
            }
            else
            {
                float s = 2f * Mathf.Sqrt(1f + m22 - m00 - m11);
                qw = (m10 - m01) / s;
                qx = (m02 + m20) / s;
                qy = (m12 + m21) / s;
                qz = 0.25f * s;
            }

            return new Quaternion(qx, qy, qz, qw).normalized;
        }

        // ── FitGravityAlignedAABB ────────────────────────────────────────────
        // Fallback when PCA degenerates. Also available as a direct API.

        public static bool FitGravityAlignedAABB(
            List<Vector3> pts,
            Pose cameraPose,
            out Vector3 center,
            out Vector3 size,
            out Quaternion rotation)
        {
            center = Vector3.zero;
            size = Vector3.zero;
            rotation = Quaternion.identity;

            if (pts == null || pts.Count == 0) return false;

            Vector3 camFwd = cameraPose.rotation * Vector3.forward;
            Vector3 flatFwd = new Vector3(camFwd.x, 0f, camFwd.z);
            if (flatFwd.sqrMagnitude < 0.01f)
            {
                Vector3 camRight = cameraPose.rotation * Vector3.right;
                flatFwd = new Vector3(camRight.x, 0f, camRight.z);
            }
            flatFwd.Normalize();

            rotation = Quaternion.LookRotation(flatFwd, Vector3.up);
            Quaternion invRotation = Quaternion.Inverse(rotation);

            Vector3 mn = invRotation * pts[0];
            Vector3 mx = mn;
            for (int i = 1; i < pts.Count; i++)
            {
                Vector3 local = invRotation * pts[i];
                mn = Vector3.Min(mn, local);
                mx = Vector3.Max(mx, local);
            }

            Vector3 localCenter = (mn + mx) * 0.5f;
            size = mx - mn;
            size.x = Mathf.Max(size.x, 0.01f);
            size.y = Mathf.Max(size.y, 0.01f);
            size.z = Mathf.Max(size.z, 0.01f);
            center = rotation * localCenter;
            return true;
        }

        // ── Legacy helpers (kept for compatibility) ──────────────────────────

        /// <summary>Camera-space AABB. Use FitPCA6DoF instead.</summary>
        public static bool FitCameraAlignedAABB(
            List<Vector3> pts,
            Pose cameraPose,
            out Vector3 center,
            out Vector3 size)
        {
            center = size = Vector3.zero;
            if (pts == null || pts.Count == 0) return false;

            Quaternion camRot = cameraPose.rotation;
            Quaternion invCamRot = Quaternion.Inverse(camRot);

            Vector3 mn = invCamRot * (pts[0] - cameraPose.position);
            Vector3 mx = mn;
            for (int i = 1; i < pts.Count; i++)
            {
                Vector3 local = invCamRot * (pts[i] - cameraPose.position);
                mn = Vector3.Min(mn, local);
                mx = Vector3.Max(mx, local);
            }

            Vector3 localCenter = (mn + mx) * 0.5f;
            size = mx - mn;
            size.x = Mathf.Max(size.x, 0.01f);
            size.y = Mathf.Max(size.y, 0.01f);
            size.z = Mathf.Max(size.z, 0.01f);
            center = cameraPose.position + camRot * localCenter;
            return true;
        }

        /// <summary>World-axis AABB. Kept for compatibility.</summary>
        public static bool FitAABB(List<Vector3> pts, out Vector3 center, out Vector3 size)
        {
            center = size = Vector3.zero;
            if (pts == null || pts.Count == 0) return false;

            Vector3 mn = pts[0], mx = pts[0];
            foreach (var p in pts) { mn = Vector3.Min(mn, p); mx = Vector3.Max(mx, p); }

            center = (mn + mx) * 0.5f;
            size = mx - mn;
            size.x = Mathf.Max(size.x, 0.01f);
            size.y = Mathf.Max(size.y, 0.01f);
            size.z = Mathf.Max(size.z, 0.01f);
            return true;
        }

        /// <summary>Median of a full array. Kept for compatibility.</summary>
        public static float Median(float[] a)
        {
            var s = (float[])a.Clone();
            System.Array.Sort(s);
            int n = s.Length;
            return n % 2 == 0 ? (s[n / 2 - 1] + s[n / 2]) * 0.5f : s[n / 2];
        }

        // ── Private helpers ──────────────────────────────────────────────────

        /// <summary>
        /// Median of the first <paramref name="count"/> elements of a rented array.
        /// Sorts in-place on a temporary ArrayPool copy to avoid mutating the caller's buffer.
        /// </summary>
        private static float MedianSlice(float[] a, int count)
        {
            if (count == 0) return 0f;
            float[] tmp = ArrayPool<float>.Shared.Rent(count);
            try
            {
                System.Array.Copy(a, tmp, count);
                System.Array.Sort(tmp, 0, count);
                return count % 2 == 0
                    ? (tmp[count / 2 - 1] + tmp[count / 2]) * 0.5f
                    : tmp[count / 2];
            }
            finally
            {
                ArrayPool<float>.Shared.Return(tmp, clearArray: false);
            }
        }
    }
}