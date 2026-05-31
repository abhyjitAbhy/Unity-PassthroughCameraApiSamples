/*
 * LockedObjectCapture.cs
 *
 * Captures a screenshot-quality cropped image of a locked detected object
 * at the moment the user pinches to freeze detection.
 *
 * Quality source: OVR eye buffer (same source as headset screenshots) —
 * full compositor resolution, passthrough blended, no YUV artifacts.
 * On Quest 3: 2064×2208 per eye before lens correction.
 *
 * ── How to integrate with your existing lock system ──────────────────────────
 *
 *  In your existing pinch/lock handler, after you call SetLocked(true):
 *
 *      _capture.CaptureLockedObject(
 *          worldCenter,    // Vector3 — EMA-smoothed center from your visualizer
 *          worldSize,      // Vector3 — EMA-smoothed size
 *          worldRotation,  // Quaternion — from your visualizer
 *          label           // string
 *      );
 *
 *  Subscribe to OnCaptureDone to receive the result:
 *
 *      _capture.OnCaptureDone += r => {
 *          // r.Texture   — Texture2D, owned by you, call Destroy() when done
 *          // r.Label     — "bottle" etc
 *          // r.WorldSize — metres
 *      };
 *
 * ── No passthrough camera dependency ─────────────────────────────────────────
 *  Uses OVR eye buffer directly. Does not touch PassthroughCameraAccess.
 *  Does not require MRUK_INSTALLED.
 * ─────────────────────────────────────────────────────────────────────────────
 */

using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace Meta.XR.BuildingBlocks.AIBlocks
{
    // ── Result type ───────────────────────────────────────────────────────────

    public struct CaptureResult
    {
        /// <summary>
        /// Screenshot-quality Texture2D of the locked object crop.
        /// YOU own this — call Destroy(result.Texture) when finished with it.
        /// </summary>
        public Texture2D Texture;

        /// <summary>Label from the segmentation model.</summary>
        public string Label;

        /// <summary>World-space size in metres (from your EMA-smoothed box).</summary>
        public Vector3 WorldSize;

        /// <summary>Pixel rect that was cropped from the eye buffer (for debug).</summary>
        public RectInt EyeBufferCropRect;
    }

    // ── Component ─────────────────────────────────────────────────────────────

    public class LockedObjectCapture : MonoBehaviour
    {
        // ── Inspector ─────────────────────────────────────────────────────────

        [Tooltip("Pixels of padding added around the projected bounding box in eye buffer space. "
               + "Increase if the crop clips object edges.")]
        [SerializeField] public int cropPaddingPixels = 24;

        [Tooltip("Which eye buffer to capture from. Left is default; both eyes give "
               + "identical passthrough quality on Quest 3.")]
        [SerializeField] private Camera captureEye;

        [Tooltip("Optional: maximum output texture dimension. 0 = full crop resolution. "
               + "1024 is a good balance of quality vs memory for display purposes.")]
        [SerializeField] public int maxOutputResolution = 1024;

        // ── Event ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Fired on the frame after the eye buffer readback completes (1-2 frames after
        /// CaptureLockedObject is called). Texture is ready to display or encode.
        /// </summary>
        public event Action<CaptureResult> OnCaptureDone;

        // ── Private ───────────────────────────────────────────────────────────

        private bool _capturing;

        private void Awake()
        {
            // If no eye camera assigned, find the main (left eye) camera
            if (captureEye == null)
                captureEye = Camera.main;

            if (captureEye == null)
                Debug.LogError("[LockedObjectCapture] No camera found. Assign captureEye in Inspector.");
        }

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>
        /// Call this from your existing lock handler at the moment the user pinches.
        /// Pass the EMA-smoothed world-space box from your visualizer.
        /// Non-blocking — result arrives via OnCaptureDone 1-2 frames later.
        /// </summary>
        public void CaptureLockedObject(
            Vector3 worldCenter, Vector3 worldSize, Quaternion worldRotation, string label)
        {
            if (_capturing)
            {
                Debug.LogWarning("[LockedObjectCapture] Capture already in progress, ignoring.");
                return;
            }

            if (captureEye == null)
            {
                Debug.LogError("[LockedObjectCapture] No capture camera set.");
                return;
            }

            StartCoroutine(CaptureCoroutine(worldCenter, worldSize, worldRotation, label));
        }

        // ── Capture coroutine ─────────────────────────────────────────────────

        private IEnumerator CaptureCoroutine(
            Vector3 worldCenter, Vector3 worldSize, Quaternion worldRotation, string label)
        {
            _capturing = true;

            // Wait for end of frame so the compositor has finished rendering
            // passthrough + scene for this frame. This is the same timing that
            // OVR's own screenshot mechanism uses.
            yield return new WaitForEndOfFrame();

            int screenW = Screen.width;
            int screenH = Screen.height;

            // ── Project world-space OBB corners into screen space ─────────────
            // Compute 8 corners of the oriented bounding box, project each into
            // screen space, take the 2D AABB of all projected corners.
            // This is coordinate-system-agnostic — works regardless of how the
            // passthrough camera UV maps to screen UV.

            RectInt cropRect = ProjectBoxToScreenRect(
                worldCenter, worldSize, worldRotation,
                captureEye, screenW, screenH, cropPaddingPixels);

            if (cropRect.width <= 0 || cropRect.height <= 0)
            {
                Debug.LogWarning("[LockedObjectCapture] Projected box is off-screen or degenerate.");
                _capturing = false;
                yield break;
            }

            // ── Read eye buffer ───────────────────────────────────────────────
            // ReadPixels from the current framebuffer at WaitForEndOfFrame is
            // the standard Unity path for screenshots. This is the full
            // compositor output — same quality as the headset screenshot button.

            var captureTex = new Texture2D(cropRect.width, cropRect.height, TextureFormat.RGB24, false);

            // ReadPixels reads from the active framebuffer.
            // On Quest the active RT at WaitForEndOfFrame is the eye buffer.
            captureTex.ReadPixels(
                new Rect(cropRect.x, cropRect.y, cropRect.width, cropRect.height), 0, 0);
            captureTex.Apply();

            // ── Downscale if requested ────────────────────────────────────────
            if (maxOutputResolution > 0 &&
                (cropRect.width > maxOutputResolution || cropRect.height > maxOutputResolution))
            {
                captureTex = ResizeTexture(captureTex, maxOutputResolution);
            }

            OnCaptureDone?.Invoke(new CaptureResult
            {
                Texture          = captureTex,
                Label            = label,
                WorldSize        = worldSize,
                EyeBufferCropRect = cropRect
            });

            _capturing = false;
        }

        // ── OBB → screen rect projection ──────────────────────────────────────

        /// <summary>
        /// Projects the 8 corners of the world-space OBB into screen pixels.
        /// Returns the padded 2D AABB of all projected corners, clamped to screen.
        /// </summary>
        private static RectInt ProjectBoxToScreenRect(
            Vector3 center, Vector3 size, Quaternion rotation,
            Camera cam, int screenW, int screenH, int padding)
        {
            // 8 unit-cube corners scaled and rotated into world space
            Vector3[] corners = new Vector3[8];
            int ci = 0;
            for (int sx = -1; sx <= 1; sx += 2)
            for (int sy = -1; sy <= 1; sy += 2)
            for (int sz = -1; sz <= 1; sz += 2)
            {
                corners[ci++] = center + rotation * new Vector3(
                    sx * size.x * 0.5f,
                    sy * size.y * 0.5f,
                    sz * size.z * 0.5f);
            }

            float xMin = float.MaxValue, xMax = float.MinValue;
            float yMin = float.MaxValue, yMax = float.MinValue;
            bool  anyInFront = false;

            foreach (var wc in corners)
            {
                // Skip corners behind the camera
                Vector3 viewPos = cam.WorldToViewportPoint(wc);
                if (viewPos.z <= 0f) continue;

                anyInFront = true;
                xMin = Mathf.Min(xMin, viewPos.x);
                xMax = Mathf.Max(xMax, viewPos.x);
                yMin = Mathf.Min(yMin, viewPos.y);
                yMax = Mathf.Max(yMax, viewPos.y);
            }

            if (!anyInFront) return default;

            // Convert viewport [0,1] to pixel coordinates
            // Note: ReadPixels Y=0 is bottom of screen (matches viewport Y)
            int pxMin = Mathf.FloorToInt(xMin * screenW)   - padding;
            int pxMax = Mathf.CeilToInt (xMax * screenW)   + padding;
            int pyMin = Mathf.FloorToInt(yMin * screenH)   - padding;
            int pyMax = Mathf.CeilToInt (yMax * screenH)   + padding;

            // Clamp to screen
            pxMin = Mathf.Clamp(pxMin, 0, screenW - 1);
            pxMax = Mathf.Clamp(pxMax, 0, screenW);
            pyMin = Mathf.Clamp(pyMin, 0, screenH - 1);
            pyMax = Mathf.Clamp(pyMax, 0, screenH);

            return new RectInt(pxMin, pyMin, pxMax - pxMin, pyMax - pyMin);
        }

        // ── Resize ────────────────────────────────────────────────────────────

        private static Texture2D ResizeTexture(Texture2D src, int maxDim)
        {
            float aspect = (float)src.width / src.height;
            int   newW, newH;

            if (aspect >= 1f)
            {
                newW = maxDim;
                newH = Mathf.Max(1, Mathf.RoundToInt(maxDim / aspect));
            }
            else
            {
                newH = maxDim;
                newW = Mathf.Max(1, Mathf.RoundToInt(maxDim * aspect));
            }

            // GPU resize via RenderTexture blit — no CPU bilinear loop
            var rt  = RenderTexture.GetTemporary(newW, newH, 0, RenderTextureFormat.ARGB32);
            Graphics.Blit(src, rt);

            var prev     = RenderTexture.active;
            RenderTexture.active = rt;

            var resized  = new Texture2D(newW, newH, TextureFormat.RGB24, false);
            resized.ReadPixels(new Rect(0, 0, newW, newH), 0, 0);
            resized.Apply();

            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
            Destroy(src); // free the original

            return resized;
        }
    }
}
