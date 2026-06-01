// CustomImageSegmentationVisualizer.cs
//
// Standalone copy of Meta's ImageSegmentationVisualizer — fully git-trackable,
// outside the package folder, free to edit without SDK updates wiping changes.
//
// Namespace : YourProject.AI   (rename to match your project)
// Replaces  : Meta.XR.BuildingBlocks.AIBlocks.ImageSegmentationVisualizer
//
// Changes vs Meta original:
//   - Width / height now computed via MeasurePhysicalSize() (ray-at-median-depth)
//     instead of the original WorldLeft/Right/Top/Bottom distance approach.
//     This is more accurate for objects at varying distances and with sparse masks.
//   - ResetState() added — called by SegmentationAnchorController on unlock.
//   - Class is no longer sealed — subclass freely.
//   - References CustomImageSegmentationAgent instead of ImageSegmentationAgent.
//
// Everything else (depth sampling, mask grid, centroid, rotation, depth-extent,
// box pooling, label scaling) is identical to Meta's original.

using System.Buffers;
using System.Collections.Generic;
using Meta.XR;
// Meta package types that are NOT being copied (they live in the SDK and are stable).
using Meta.XR.BuildingBlocks.AIBlocks;                    // SegmentationResult, PassthroughCameraAccess, DepthTextureAccess
using PassthroughCameraSamples.MultiObjectDetection;       // Box3DDimensionDisplay
using Unity.Collections;
using UnityEngine;
using UnityEngine.UI;
namespace SmartMove
{
    public class CustomImageSegmentationVisualizer : MonoBehaviour
    {
#if MRUK_INSTALLED

        // ── Inspector ──────────────────────────────────────────────────────────

        [Tooltip("Prefab used to render each 3D bounding box.")]
        [SerializeField] private GameObject boundingBoxPrefab;

        [Tooltip("Enable or disable bounding box visualization.")]
        [SerializeField] private bool showBoundingBoxes = true;

        [Tooltip("Enable or disable label visualization.")]
        [SerializeField] private bool showLabels = true;

        [Tooltip("Scale factor for text labels relative to bounding box size.")]
        [Range(0f, 1f)]
        [SerializeField] private float labelScale = 0.5f;

        // ── Internal refs ──────────────────────────────────────────────────────

        private CustomImageSegmentationAgent _agent;
        private PassthroughCameraAccess _cam;
        private DepthTextureAccess _depth;
        private Matrix4x4[] _vpBuf;
        private float[] _depthBuf;
        private int _eyeIdx;

        // ── Object pools / caches ──────────────────────────────────────────────

        private readonly List<GameObject> _live = new();
        private readonly Queue<GameObject> _pool = new();
        private readonly List<Vector3> _worldPointsCache = new();
        private readonly List<float> _depthsCache = new();
        private readonly Dictionary<GameObject, (Renderer renderer, Text label, Box3DDimensionDisplay dimDisplay)> _componentCache = new();

        // ── Frame data ─────────────────────────────────────────────────────────

        private struct FrameData
        {
            public Pose Pose;
            public float[] Depth;
            public Matrix4x4[] ViewProjectionMatrix;
        }

        private FrameData _frame;

        // ── Lifecycle ──────────────────────────────────────────────────────────

        private void Awake()
        {
            _agent = GetComponent<CustomImageSegmentationAgent>();
            _cam = FindAnyObjectByType<PassthroughCameraAccess>();

            if (_cam == null)
            {
                Debug.LogError("[CustomImageSegmentationVisualizer] PassthroughCameraAccess not found.");
                enabled = false;
                return;
            }

            _depth = GetComponent<DepthTextureAccess>();
            _eyeIdx = _cam.CameraPosition == PassthroughCameraAccess.CameraPositionType.Left ? 0 : 1;
        }

        private void OnEnable()
        {
            if (_agent != null) _agent.OnSegmentationUpdated += Draw3D;
            if (_depth != null) _depth.OnDepthTextureUpdateCPU += OnDepth;
        }

        private void OnDisable()
        {
            if (_agent != null) _agent.OnSegmentationUpdated -= Draw3D;
            if (_depth != null) _depth.OnDepthTextureUpdateCPU -= OnDepth;
            ReturnBuffers();
        }

        // ── Depth callback ─────────────────────────────────────────────────────

        private void OnDepth(DepthTextureAccess.DepthFrameData d)
        {
            _frame.Pose = d.CameraPose;

            if (_depthBuf == null || _depthBuf.Length < d.DepthTexturePixels.Length)
            {
                if (_depthBuf != null) ArrayPool<float>.Shared.Return(_depthBuf);
                _depthBuf = ArrayPool<float>.Shared.Rent(d.DepthTexturePixels.Length);
            }

            if (_vpBuf == null || _vpBuf.Length < d.ViewProjectionMatrix.Length)
            {
                if (_vpBuf != null) ArrayPool<Matrix4x4>.Shared.Return(_vpBuf);
                _vpBuf = ArrayPool<Matrix4x4>.Shared.Rent(d.ViewProjectionMatrix.Length);
            }

            NativeArray<float>.Copy(d.DepthTexturePixels, _depthBuf, d.DepthTexturePixels.Length);
            System.Array.Copy(d.ViewProjectionMatrix, _vpBuf, d.ViewProjectionMatrix.Length);

            _frame.Depth = _depthBuf;
            _frame.ViewProjectionMatrix = _vpBuf;
        }

        private void ReturnBuffers()
        {
            if (_depthBuf != null)
            {
                ArrayPool<float>.Shared.Return(_depthBuf, clearArray: true);
                _depthBuf = null;
            }
            if (_vpBuf != null)
            {
                ArrayPool<Matrix4x4>.Shared.Return(_vpBuf, clearArray: true);
                _vpBuf = null;
            }
        }

        // ── Draw3D ─────────────────────────────────────────────────────────────

        public void Draw3D(SegmentationResult result)
        {
            if (!showBoundingBoxes && !showLabels) { ClearAll(); return; }
            if (result is not { numObjects: > 0 } || _frame.Depth == null) { ClearAll(); return; }

            // Return all live boxes to pool before rebuilding.
            foreach (var g in _live)
            {
                g.SetActive(false);
                _pool.Enqueue(g);
            }
            _live.Clear();

            if (!boundingBoxPrefab)
            {
                Debug.LogWarning("[CustomImageSegmentationVisualizer] No boundingBoxPrefab assigned.");
                return;
            }

            var cameraTexture = _cam.GetTexture();
            if (!cameraTexture)
            {
                Debug.LogWarning("[CustomImageSegmentationVisualizer] Camera texture is null.");
                return;
            }

            for (var i = 0; i < result.numObjects; i++)
            {
                var classId = result.classIds[i];
                var o = i * 4;
                var cxNorm = result.boxes[o + 0];
                var cyNorm = result.boxes[o + 1];
                var wNorm = result.boxes[o + 2];
                var hNorm = result.boxes[o + 3];

                var label = "Unknown";
                if (result.labels != null && classId < result.labels.Length)
                    label = result.labels[classId];

                var centerX = cxNorm * cameraTexture.width;
                var centerY = cyNorm * cameraTexture.height;
                var width = wNorm * cameraTexture.width;
                var height = hNorm * cameraTexture.height;

                var xmin = centerX - width * 0.5f;
                var ymin = centerY - height * 0.5f;
                var xmax = centerX + width * 0.5f;
                var ymax = centerY + height * 0.5f;

                if (!TryProject(cameraTexture, result, i, xmin, ymin, xmax, ymax, label,
                        out var pos, out var rot, out var scl))
                {
                    Debug.LogWarning($"[CustomImageSegmentationVisualizer] Failed to project object {i} ({label})");
                    continue;
                }

                var box = _pool.Count > 0 ? _pool.Dequeue() : Instantiate(boundingBoxPrefab);
                box.SetActive(true);
                box.transform.SetPositionAndRotation(pos, rot);
                box.transform.localScale = scl;
                _live.Add(box);

                if (!_componentCache.TryGetValue(box, out var cached))
                {
                    cached = (
                        box.GetComponent<Renderer>(),
                        box.GetComponentInChildren<Text>(),
                        box.GetComponent<Box3DDimensionDisplay>()
                    );
                    _componentCache[box] = cached;
                }

                if (cached.renderer)
                    cached.renderer.enabled = showBoundingBoxes;

                // Activate dimension display — reads localScale automatically each frame
                cached.dimDisplay?.SetFrozen(false);

                if (!cached.label) continue;
                cached.label.enabled = showLabels;

                var score = result.scores != null && i < result.scores.Length ? result.scores[i] : 0f;
                cached.label.text = $"{label} {score:0.00}";

                var avgScale = (scl.x + scl.y + scl.z) / 3f;
                var uniformScale = avgScale * labelScale;

                cached.label.transform.localScale = new Vector3(
                    uniformScale / Mathf.Max(scl.x, 0.001f),
                    uniformScale / Mathf.Max(scl.y, 0.001f),
                    uniformScale / Mathf.Max(scl.z, 0.001f));
            }
        }

        // ── TryProject ─────────────────────────────────────────────────────────
        // Identical to Meta original EXCEPT width/height are now computed via
        // MeasurePhysicalSize() instead of WorldLeft/Right/Top/Bottom distances.

        private bool TryProject(
            Texture cameraTexture, SegmentationResult result, int objectIndex,
            float xmin, float ymin, float xmax, float ymax, string label,
            out Vector3 world, out Quaternion rot, out Vector3 scale)
        {
            world = default;
            rot = default;
            scale = default;

            var maskPixelsPerObject = result.maskWidth * result.maskHeight;
            var maskOffset = objectIndex * maskPixelsPerObject;

            _worldPointsCache.Clear();
            const int gridSize = 10;
            var stepX = (xmax - xmin) / (gridSize - 1);
            var stepY = (ymax - ymin) / (gridSize - 1);

            // ── Mask-guided depth sampling (UNCHANGED from Meta original) ──────
            for (var i = 0; i < gridSize; i++)
            {
                for (var j = 0; j < gridSize; j++)
                {
                    var px = xmin + i * stepX;
                    var py = ymin + j * stepY;

                    var maskX = (int)((px / cameraTexture.width) * result.maskWidth);
                    var maskY = (int)((py / cameraTexture.height) * result.maskHeight);
                    maskX = Mathf.Clamp(maskX, 0, result.maskWidth - 1);
                    maskY = Mathf.Clamp(maskY, 0, result.maskHeight - 1);

                    var maskIdx = maskOffset + maskY * result.maskWidth + maskX;
                    if (maskIdx >= result.masks.Length || maskIdx < 0) continue;

                    var maskValue = result.masks[maskIdx];
                    var isObjectPixel = result.maskAreLogits ? maskValue > 0.0f : maskValue > 0.5f;
                    if (!isObjectPixel) continue;

                    var normalizedX = px / cameraTexture.width;
                    var normalizedY = py / cameraTexture.height;

                    var ray = _cam.ViewportPointToRay(new Vector2(normalizedX, 1.0f - normalizedY), _frame.Pose);
                    var world1M = ray.origin + ray.direction;
                    var clip = _frame.ViewProjectionMatrix[_eyeIdx] *
                                 new Vector4(world1M.x, world1M.y, world1M.z, 1f);
                    if (clip.w <= 0) continue;

                    var uv = (new Vector2(clip.x, clip.y) / clip.w) * 0.5f + Vector2.one * 0.5f;

                    if (!_depth || !_depth.IsInitialized) continue;

                    var texSize = _depth.TextureSize;
                    var sx = Mathf.Clamp((int)(uv.x * texSize), 0, texSize - 1);
                    var sy = Mathf.Clamp((int)(uv.y * texSize), 0, texSize - 1);
                    var idx = _eyeIdx * texSize * texSize + sy * texSize + sx;
                    var depth = _frame.Depth[idx];

                    if (depth is <= 0 or > 20 || float.IsInfinity(depth)) continue;

                    _worldPointsCache.Add(ray.origin + ray.direction * depth);
                }
            }

            if (_worldPointsCache.Count < 3)
            {
                Debug.LogWarning(
                    $"[CustomImageSegmentationVisualizer] {label}: Not enough valid depth points ({_worldPointsCache.Count})");
                return false;
            }

            // ── Centroid (UNCHANGED) ───────────────────────────────────────────
            world = ComputeCentroid(_worldPointsCache);

            // ── Median depth (UNCHANGED) ───────────────────────────────────────
            _depthsCache.Clear();
            foreach (var p in _worldPointsCache)
                _depthsCache.Add((p - _frame.Pose.position).magnitude);
            _depthsCache.Sort();
            var medianDepth = _depthsCache[_depthsCache.Count / 2];

            // ── Width / Height via MeasurePhysicalSize (REPLACES original) ─────
            // Builds normRect in UV space (Y=0 at bottom) then casts four edge
            // rays at medianDepth — accurate regardless of point density.
            var normRect = new Rect(
                xmin / cameraTexture.width,
                1f - ymax / cameraTexture.height,       // image→UV Y-flip
                (xmax - xmin) / cameraTexture.width,
                (ymax - ymin) / cameraTexture.height);

            MeasurePhysicalSize(normRect, medianDepth, _cam, _frame.Pose,
                out float physW, out float physH);

            // ── Depth extent (UNCHANGED) ───────────────────────────────────────
            var normalizedCenterX = ((xmin + xmax) * 0.5f) / cameraTexture.width;
            var normalizedCenterY = ((ymin + ymax) * 0.5f) / cameraTexture.height;
            var rayCenter = _cam.ViewportPointToRay(
                new Vector2(normalizedCenterX, 1.0f - normalizedCenterY), _frame.Pose);
            var worldCenter = rayCenter.origin + rayCenter.direction * medianDepth;
            var viewDir = (worldCenter - _frame.Pose.position).normalized;

            var minDepthProj = float.MaxValue;
            var maxDepthProj = float.MinValue;
            foreach (var p in _worldPointsCache)
            {
                var proj = Vector3.Dot(p - world, viewDir);
                minDepthProj = Mathf.Min(minDepthProj, proj);
                maxDepthProj = Mathf.Max(maxDepthProj, proj);
            }
            var depthExtent = Mathf.Clamp(maxDepthProj - minDepthProj, 0.02f, 0.5f);

            // ── Rotation (UNCHANGED) ───────────────────────────────────────────
            var toObject = (world - _frame.Pose.position).normalized;
            var forward = new Vector3(toObject.x, 0f, toObject.z).normalized;
            if (forward.magnitude < 0.1f) forward = Vector3.forward;
            rot = Quaternion.LookRotation(forward, Vector3.up);

            // ── Final scale ────────────────────────────────────────────────────
            scale = new Vector3(
                Mathf.Clamp(physW, 0.02f, 2.0f),
                Mathf.Clamp(physH, 0.02f, 2.0f),
                Mathf.Clamp(depthExtent, 0.02f, 2.0f));

            return true;
        }

        // ── MeasurePhysicalSize ────────────────────────────────────────────────
        // Projects bounding-box edges as rays at the provided median depth.
        // normRect: Y=0 at bottom (UV convention, already flipped by caller).

        private static void MeasurePhysicalSize(
            Rect normRect,
            float medianDepth,
            PassthroughCameraAccess pca,
            Pose framePose,
            out float physicalWidth,
            out float physicalHeight)
        {
            physicalWidth = 0.01f;
            physicalHeight = 0.01f;

            if (medianDepth <= 0f || medianDepth > 20f || pca == null) return;

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

        // ── Helpers ────────────────────────────────────────────────────────────

        private static Vector3 ComputeCentroid(List<Vector3> points)
        {
            var c = Vector3.zero;
            foreach (var p in points) c += p;
            return c / points.Count;
        }

        // ── Public API ─────────────────────────────────────────────────────────

        /// <summary>
        /// Returns all live boxes to the pool and clears internal state.
        /// Call from SegmentationAnchorController.UnlockAll() instead of ResetState()
        /// on Meta's original (which didn't have this method).
        /// </summary>
        public void ResetState()
        {
            ClearAll();
            _componentCache.Clear();
        }

        private void ClearAll()
        {
            foreach (var g in _live)
            {
                if (g)
                {
                    // Freeze dimension display while pooled so it doesn't run while inactive
                    if (_componentCache.TryGetValue(g, out var cached))
                        cached.dimDisplay?.SetFrozen(true);

                    g.SetActive(false);
                    _pool.Enqueue(g);
                }
            }
            _live.Clear();
        }

#endif
    }
}

