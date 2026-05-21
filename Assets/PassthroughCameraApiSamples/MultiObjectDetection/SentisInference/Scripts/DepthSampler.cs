// Copyright (c) Meta Platforms, Inc. and affiliates.

using System.Collections.Generic;
using Meta.XR;
using UnityEngine;

namespace PassthroughCameraSamples.MultiObjectDetection
{
    /// <summary>
    /// DepthSampler.cs
    ///
    /// Changes in this version:
    ///   + FitGravityAlignedAABB — orients the bounding box with world-up Y and
    ///     the camera's horizontal forward (yaw only).  This keeps the box
    ///     perfectly upright and correctly sized regardless of the viewing angle.
    ///     The previous FitCameraAlignedAABB tilted the box with the full camera
    ///     rotation, so looking down at an object made the box lean forward and
    ///     miss the object's true vertical extent.
    ///
    ///   (All previous fixes — grid spacing, ClusterFilter two-pass, jitter —
    ///    are retained unchanged.)
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

        // ── ClusterFilter (two-pass) ─────────────────────────────────────────

        public static List<Vector3> ClusterFilter(
            List<Vector3> points,
            Vector3 cameraPos,
            float toleranceM = 0.12f,
            int minCount = 6)
        {
            if (points == null || points.Count < minCount)
                return new List<Vector3>();

            // Pass 1
            float[] depths = new float[points.Count];
            for (int i = 0; i < points.Count; i++)
                depths[i] = Vector3.Distance(cameraPos, points[i]);

            float median1 = Median(depths);

            var pass1 = new List<Vector3>(points.Count);
            var depths1 = new List<float>(points.Count);
            for (int i = 0; i < points.Count; i++)
            {
                if (Mathf.Abs(depths[i] - median1) <= toleranceM)
                {
                    pass1.Add(points[i]);
                    depths1.Add(depths[i]);
                }
            }

            if (pass1.Count < minCount)
                return new List<Vector3>();

            // Pass 2 (tighter)
            float median2 = Median(depths1.ToArray());
            float tight = toleranceM * 0.55f;

            var pass2 = new List<Vector3>(pass1.Count);
            for (int i = 0; i < pass1.Count; i++)
            {
                if (Mathf.Abs(depths1[i] - median2) <= tight)
                    pass2.Add(pass1[i]);
            }

            var result = pass2.Count >= minCount ? pass2 : pass1;
            return result.Count >= minCount ? result : new List<Vector3>();
        }

        // ── FitGravityAlignedAABB ────────────────────────────────────────────
        //
        // WHY THIS REPLACES FitCameraAlignedAABB:
        //
        // The old method projected points into the full camera rotation frame
        // (pitch + yaw + roll).  When the user looks down at a table, the
        // camera pitch tilts the Y-axis forward, so what the AABB measures as
        // "height" is actually a diagonal across the object — the box leans
        // forward and doesn't cover the object's real vertical extent.
        //
        // This method builds the projection frame from:
        //   • Y  = world up  (0,1,0)  — always vertical
        //   • Z  = camera forward projected onto the horizontal plane (yaw only)
        //   • X  = cross(Z, Y)        — horizontal left/right
        //
        // Result: the box is always axis-aligned with gravity, so it correctly
        // encloses the object from any viewing angle — top-down, side-on, etc.
        // The box faces the camera horizontally but never tilts with head pitch.

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

            // ── Compute centroid (mean) ──────────────────────────────────────
            // Points must be mean-centred before projection so that the local
            // AABB is symmetric around the origin.  Without this, localCenter
            // sits in rotated-world space and "rotation * localCenter" rotates
            // it a second time instead of transforming back — producing a center
            // that drifts far from the actual object.
            Vector3 mean = Vector3.zero;
            for (int i = 0; i < pts.Count; i++)
                mean += pts[i];
            mean /= pts.Count;

            // ── Build upright frame ──────────────────────────────────────────
            // Project camera forward onto the horizontal plane (strips pitch/roll).
            Vector3 camFwd = cameraPose.rotation * Vector3.forward;
            Vector3 flatFwd = new Vector3(camFwd.x, 0f, camFwd.z);

            // Guard: if camera is pointing nearly straight up or down, fall back
            // to the camera's right projected onto horizontal.
            if (flatFwd.sqrMagnitude < 0.01f)
            {
                Vector3 camRight = cameraPose.rotation * Vector3.right;
                flatFwd = new Vector3(camRight.x, 0f, camRight.z);
            }
            flatFwd.Normalize();

            // LookRotation(forward, up) → Z = flatFwd, Y = world up, X = right
            rotation = Quaternion.LookRotation(flatFwd, Vector3.up);
            Quaternion invRotation = Quaternion.Inverse(rotation);

            // ── Project mean-centred points into the upright frame ───────────
            // Subtract mean first so the AABB is centred at the origin in local
            // space; we add mean back (in world space) after rotating.
            Vector3 mn = invRotation * (pts[0] - mean);
            Vector3 mx = mn;

            for (int i = 1; i < pts.Count; i++)
            {
                Vector3 local = invRotation * (pts[i] - mean);
                mn = Vector3.Min(mn, local);
                mx = Vector3.Max(mx, local);
            }

            // ── Compute center & size in local frame, convert center to world ─
            Vector3 localCenter = (mn + mx) * 0.5f;

            size = mx - mn;
            size.x = Mathf.Max(size.x, 0.01f);
            size.y = Mathf.Max(size.y, 0.01f);
            size.z = Mathf.Max(size.z, 0.01f);

            // Rotate local center back to world space and add the centroid.
            // (rotation * localCenter) converts the local offset to world axes;
            // adding mean translates it to the correct world position.
            center = mean + rotation * localCenter;
            return true;
        }

        // ── Legacy helpers (kept for compatibility) ──────────────────────────

        /// <summary>
        /// Camera-space AABB — kept for reference. Use FitGravityAlignedAABB instead.
        /// </summary>
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

        /// <summary>World-axis AABB — kept for compatibility.</summary>
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

        public static float Median(float[] a)
        {
            var s = (float[])a.Clone();
            System.Array.Sort(s);
            int n = s.Length;
            return n % 2 == 0 ? (s[n / 2 - 1] + s[n / 2]) * 0.5f : s[n / 2];
        }
    }
}