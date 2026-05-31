// BoxProjectionCrop.cs — v3
//
// CHANGES FROM v2:
//
//   REMOVED: Entire WorldToViewport() method with Newton-Raphson / bilinear inversion.
//            PassthroughCameraAccess.WorldToViewportPoint() already does this correctly
//            using real camera intrinsics (focal length, principal point, sensor crop).
//            The hand-rolled version was both wrong and unnecessary.
//
//   FIXED:   ComputeCropRect() now calls pca.WorldToViewportPoint(worldCorner, cameraPose)
//            directly. cameraPose must be the Pose returned by pca.GetCameraPose() at
//            snapshot time — NOT pcaCamera.transform (that's always at world origin).
//
//   REMOVED: SnapshotMode parameter and all manual X/Y flip logic from this file.
//            Orientation correction belongs in the snapshot path (SnapshotViaPcaColors
//            already applies Rotate180). By the time we project box corners into viewport
//            space and then into pixel space on the already-rotated texture, we only need
//            the standard Texture2D Y-flip (Y=0 bottom). That is now handled here
//            uniformly regardless of snapshot mode.
//
//   NOTE:    PCA.WorldToViewportPoint() returns Y=0 at bottom already (viewport convention
//            matches Texture2D). No additional Y flip needed here.

using Meta.XR;
using UnityEngine;

namespace PassthroughCameraSamples.MultiObjectDetection
{
    /// <summary>
    /// Result of projecting a 3D bounding box into camera 2D space.
    /// NormRect is in normalised [0,1] camera texture coordinates, Y=0 at BOTTOM.
    /// </summary>
    public readonly struct BoxProjectionResult
    {
        public readonly Rect NormRect;
        public readonly bool IsValid;
        public readonly int VisibleCorners;

        public BoxProjectionResult(Rect normRect, bool isValid, int visibleCorners)
        {
            NormRect = normRect;
            IsValid = isValid;
            VisibleCorners = visibleCorners;
        }
    }

    /// <summary>
    /// Projects a BoxManipulator's world-space OBB into camera texture space
    /// using PassthroughCameraAccess.WorldToViewportPoint() with real intrinsics.
    /// Stateless — call ComputeCropRect() anytime after the box has been adjusted.
    /// </summary>
    public static class BoxProjectionCrop
    {
        // 8 normalised local-space corners of a unit cube centred at origin.
        // BoxManipulator uses localScale as box size and position as center.
        private static readonly Vector3[] s_localCorners = new Vector3[]
        {
            new(-0.5f, -0.5f, -0.5f),
            new( 0.5f, -0.5f, -0.5f),
            new(-0.5f,  0.5f, -0.5f),
            new( 0.5f,  0.5f, -0.5f),
            new(-0.5f, -0.5f,  0.5f),
            new( 0.5f, -0.5f,  0.5f),
            new(-0.5f,  0.5f,  0.5f),
            new( 0.5f,  0.5f,  0.5f),
        };

        /// <summary>
        /// Projects the 8 world-space corners of <paramref name="boxTransform"/> into
        /// camera viewport space using PCA's real intrinsics.
        /// </summary>
        /// <param name="boxTransform">Transform of the BoxManipulator GameObject.</param>
        /// <param name="pca">PassthroughCameraAccess instance.</param>
        /// <param name="frozenCameraPose">
        ///   Pose captured via pca.GetCameraPose() at the moment the frame pixels were grabbed.
        ///   Must NOT be pcaCamera.transform — that is always at world origin.
        /// </param>
        /// <param name="paddingNorm">Extra normalised padding added to each edge.</param>
        public static BoxProjectionResult ComputeCropRect(
            Transform boxTransform,
            PassthroughCameraAccess pca,
            Pose frozenCameraPose,
            float paddingNorm = 0.01f)
        {
            if (boxTransform == null || pca == null)
                return new BoxProjectionResult(Rect.zero, false, 0);

            float xMin = float.MaxValue;
            float xMax = float.MinValue;
            float yMin = float.MaxValue;
            float yMax = float.MinValue;
            int visible = 0;

            for (int i = 0; i < 8; i++)
            {
                Vector3 worldCorner = boxTransform.TransformPoint(s_localCorners[i]);

                // Use PCA's own WorldToViewportPoint with the frozen pose.
                // This uses real focal length + principal point + sensor crop region.
                // Returns [0,1] viewport coords, Y=0 at bottom (Texture2D convention).
                Vector2 vp = pca.WorldToViewportPoint(worldCorner, frozenCameraPose);

                bool inFrustum = vp.x >= 0f && vp.x <= 1f && vp.y >= 0f && vp.y <= 1f;
                if (inFrustum) visible++;

                xMin = Mathf.Min(xMin, vp.x);
                xMax = Mathf.Max(xMax, vp.x);
                yMin = Mathf.Min(yMin, vp.y);
                yMax = Mathf.Max(yMax, vp.y);
            }

            if (visible == 0)
                return new BoxProjectionResult(Rect.zero, false, 0);

            // The PCA Rotate180 in SnapshotViaPcaColors flips both X and Y in the
            // stored Texture2D. WorldToViewportPoint returns coords relative to the
            // RAW sensor frame. We must mirror both axes to match the rotated texture.
            float finalXMin = 1f - xMax;
            float finalXMax = 1f - xMin;
            float finalYMin = 1f - yMax;
            float finalYMax = 1f - yMin;

            finalXMin = Mathf.Clamp01(finalXMin - paddingNorm);
            finalXMax = Mathf.Clamp01(finalXMax + paddingNorm);
            finalYMin = Mathf.Clamp01(finalYMin - paddingNorm);
            finalYMax = Mathf.Clamp01(finalYMax + paddingNorm);

            float w = finalXMax - finalXMin;
            float h = finalYMax - finalYMin;

            if (w <= 0f || h <= 0f)
                return new BoxProjectionResult(Rect.zero, false, visible);

            return new BoxProjectionResult(new Rect(finalXMin, finalYMin, w, h), true, visible);
        }
    }
}