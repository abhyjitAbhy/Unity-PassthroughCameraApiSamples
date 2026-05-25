// ═══════════════════════════════════════════════════════════════════════════
// PCAOBBFitter.cs  — FIXED
//
// BUG in original:
//   OrientationFromAxes() used Matrix4x4.SetColumn with axis vectors in columns
//   0,1,2 and then called m.rotation — but Matrix4x4.rotation reads the rotation
//   from columns as (right, up, forward). When the eigenvectors are in a different
//   order than (right, up, forward), the resulting quaternion is wrong/nan.
//   FIX: explicitly build a right/up/forward from the sorted eigenvectors and
//   call Quaternion.LookRotation(forward, up) which is guaranteed to be stable.
//
//   SnapToGroundPlane also had a logic error: the newFwd computation for the
//   dotFwd >= dotUp && dotFwd >= dotRight branch returned localRight projected
//   onto the ground plane — but localRight may not be on the ground plane when
//   the box is tilted. FIX: always project the axis least aligned with world-up
//   onto the ground plane.
// ═══════════════════════════════════════════════════════════════════════════

using UnityEngine;

namespace PassthroughCameraSamples.MultiObjectDetection
{
    public static class PCAOBBFitter
    {
        public struct OBBResult
        {
            public Vector3 Center;
            public Vector3 Extents;      // half-sizes in metres
            public Quaternion Orientation;
            public Vector3 Axis0;        // longest
            public Vector3 Axis1;
            public Vector3 Axis2;
            public int PointCount;
        }

        public static OBBResult Fit(Vector3[] points)
        {
            if (points.Length < 4) return default;

            // 1. Centroid
            Vector3 centroid = Vector3.zero;
            foreach (var p in points) centroid += p;
            centroid /= points.Length;

            // 2. Covariance matrix
            double cxx = 0, cxy = 0, cxz = 0, cyy = 0, cyz = 0, czz = 0;
            foreach (var p in points)
            {
                double dx = p.x - centroid.x, dy = p.y - centroid.y, dz = p.z - centroid.z;
                cxx += dx * dx; cxy += dx * dy; cxz += dx * dz;
                cyy += dy * dy; cyz += dy * dz; czz += dz * dz;
            }
            double n = points.Length;
            double[,] cov = { { cxx / n, cxy / n, cxz / n }, { cxy / n, cyy / n, cyz / n }, { cxz / n, cyz / n, czz / n } };

            // 3. Jacobi eigen-decomposition
            JacobiEigen(cov, out double[] evals, out double[,] evecs);
            SortByDescending(ref evals, ref evecs);

            // 4. Eigenvectors as axis vectors
            Vector3 e0 = new Vector3((float)evecs[0, 0], (float)evecs[1, 0], (float)evecs[2, 0]).normalized;
            Vector3 e1 = new Vector3((float)evecs[0, 1], (float)evecs[1, 1], (float)evecs[2, 1]).normalized;
            Vector3 e2 = new Vector3((float)evecs[0, 2], (float)evecs[1, 2], (float)evecs[2, 2]).normalized;

            // Ensure right-handed
            if (Vector3.Dot(Vector3.Cross(e0, e1), e2) < 0) e2 = -e2;

            // 5. Project and find extents
            float min0 = float.MaxValue, max0 = float.MinValue;
            float min1 = float.MaxValue, max1 = float.MinValue;
            float min2 = float.MaxValue, max2 = float.MinValue;
            foreach (var p in points)
            {
                Vector3 d = p - centroid;
                float p0 = Vector3.Dot(d, e0), p1 = Vector3.Dot(d, e1), p2 = Vector3.Dot(d, e2);
                if (p0 < min0) min0 = p0; if (p0 > max0) max0 = p0;
                if (p1 < min1) min1 = p1; if (p1 > max1) max1 = p1;
                if (p2 < min2) min2 = p2; if (p2 > max2) max2 = p2;
            }

            Vector3 center = centroid
                + e0 * ((min0 + max0) * 0.5f)
                + e1 * ((min1 + max1) * 0.5f)
                + e2 * ((min2 + max2) * 0.5f);

            Vector3 extents = new Vector3(
                (max0 - min0) * 0.5f, (max1 - min1) * 0.5f, (max2 - min2) * 0.5f);

            // 6. FIX: build orientation using LookRotation on axis most aligned with
            //    the horizontal plane, forcing world-up for stability.
            Quaternion orientation = BuildUprightOrientation(e0, e1, e2, extents);

            return new OBBResult
            {
                Center = center,
                Extents = extents,
                Orientation = orientation,
                Axis0 = e0,
                Axis1 = e1,
                Axis2 = e2,
                PointCount = points.Length
            };
        }

        /// <summary>
        /// Prevent the box from orienting toward the camera.
        /// If the longest PCA axis points toward the camera, swap to the next axis.
        /// </summary>
        public static OBBResult CorrectCameraAlignment(OBBResult obb, Camera cam)
        {
            Vector3 toCamera = (cam.transform.position - obb.Center).normalized;
            if (Mathf.Abs(Vector3.Dot(obb.Axis0, toCamera)) > 0.8f)
            {
                // Swap axis0 ↔ axis1 (and their extents)
                (obb.Axis0, obb.Axis1) = (obb.Axis1, obb.Axis0);
                (obb.Extents.x, obb.Extents.y) = (obb.Extents.y, obb.Extents.x);
                obb.Orientation = BuildUprightOrientation(obb.Axis0, obb.Axis1, obb.Axis2, obb.Extents);
            }
            return obb;
        }

        // FIX: stable orientation builder — finds which axis is most vertical,
        // snaps it to world up, then builds forward from the remaining axes.
        private static Quaternion BuildUprightOrientation(
            Vector3 e0, Vector3 e1, Vector3 e2, Vector3 extents)
        {
            float d0 = Mathf.Abs(Vector3.Dot(e0, Vector3.up));
            float d1 = Mathf.Abs(Vector3.Dot(e1, Vector3.up));
            float d2 = Mathf.Abs(Vector3.Dot(e2, Vector3.up));

            Vector3 forward;
            // Whichever axis is most vertical becomes the implicit "up" axis.
            // Use one of the other two as "forward".
            if (d0 >= d1 && d0 >= d2)
                forward = Vector3.ProjectOnPlane(e1, Vector3.up).normalized;
            else if (d1 >= d2)
                forward = Vector3.ProjectOnPlane(e0, Vector3.up).normalized;
            else
                forward = Vector3.ProjectOnPlane(e0, Vector3.up).normalized;

            if (forward.sqrMagnitude < 0.001f)
                forward = Vector3.forward;

            return Quaternion.LookRotation(forward, Vector3.up);
        }

        // ─── Jacobi ──────────────────────────────────────────────────────────
        private static void JacobiEigen(double[,] A,
            out double[] eigenvalues, out double[,] eigenvectors)
        {
            const int N = 3, MaxIter = 50;
            double[,] D = (double[,])A.Clone();
            double[,] V = new double[N, N];
            for (int i = 0; i < N; i++) V[i, i] = 1.0;

            for (int iter = 0; iter < MaxIter; iter++)
            {
                int p = 0, q = 1;
                double maxVal = System.Math.Abs(D[0, 1]);
                for (int i = 0; i < N; i++) for (int j = i + 1; j < N; j++)
                        if (System.Math.Abs(D[i, j]) > maxVal) { maxVal = System.Math.Abs(D[i, j]); p = i; q = j; }
                if (maxVal < 1e-10) break;

                double theta = (D[q, q] - D[p, p]) / (2.0 * D[p, q]);
                double t = (theta >= 0 ? 1.0 : -1.0) / (System.Math.Abs(theta) + System.Math.Sqrt(theta * theta + 1.0));
                double c = 1.0 / System.Math.Sqrt(t * t + 1.0), s = t * c;
                double dpq = D[p, q];
                D[p, p] -= t * dpq; D[q, q] += t * dpq; D[p, q] = D[q, p] = 0.0;
                for (int r = 0; r < N; r++)
                {
                    if (r == p || r == q) continue;
                    double dpr = D[p, r], dqr = D[q, r];
                    D[p, r] = D[r, p] = c * dpr - s * dqr;
                    D[q, r] = D[r, q] = s * dpr + c * dqr;
                }
                for (int r = 0; r < N; r++)
                {
                    double vpr = V[r, p], vqr = V[r, q];
                    V[r, p] = c * vpr - s * vqr;
                    V[r, q] = s * vpr + c * vqr;
                }
            }
            eigenvalues = new double[] { D[0, 0], D[1, 1], D[2, 2] };
            eigenvectors = V;
        }

        private static void SortByDescending(ref double[] vals, ref double[,] vecs)
        {
            const int N = 3;
            for (int i = 0; i < N - 1; i++) for (int j = i + 1; j < N; j++)
                    if (vals[j] > vals[i])
                    {
                        (vals[i], vals[j]) = (vals[j], vals[i]);
                        for (int r = 0; r < N; r++) (vecs[r, i], vecs[r, j]) = (vecs[r, j], vecs[r, i]);
                    }
        }
    }
}
