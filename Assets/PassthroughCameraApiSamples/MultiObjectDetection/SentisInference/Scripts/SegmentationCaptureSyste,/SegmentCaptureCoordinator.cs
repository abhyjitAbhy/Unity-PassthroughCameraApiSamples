// SegmentCaptureCoordinator.cs  — v6  (Pinch-time Snapshot + Real Camera Pose)
//
// FIXES APPLIED (cumulative from v4):
//
//   v5 — Snapshot at pinch time, not lock time:
//     OnSystemLocked() no longer calls SnapshotVia*() or TryTriggerCapture().
//     TryTriggerCapture() (via NotifyLeftMiddlePinch) takes the snapshot immediately
//     before CaptureFromLockedBox() so frame + pose + box are all from the same instant.
//
//   v6 — Real camera pose via GetCameraPose():
//     OLD: _frozenCameraPose = new Pose(pcaCamera.transform.position, ...)
//          pcaCamera.transform is a static scene GameObject — always at (0,0,0).
//          This caused WorldToViewportPoint to project from the wrong origin → visibleCorners=0.
//     NEW: _frozenCameraPose = pcaCamera.GetCameraPose()
//          Uses the hardware frame timestamp (_timestampNsMonotonic) + LensOffset
//          to return the actual tracked physical camera position in world space.
//
//   Pairs with BoxProjectionCrop.cs v3 which drops the fake bilinear inversion
//   and calls pca.WorldToViewportPoint() directly (real intrinsics). The snapshotMode
//   parameter is removed from ComputeCropRect() — orientation is always Rotate180.
//
// UNCHANGED: YOLO fallback paths, save, HUD, spatial anchor logic.

using System;
using System.Collections;
using System.Collections.Generic;
using Meta.XR;
using PassthroughCameraSamples;
using PassthroughCameraSamples.MultiObjectDetection;
using UnityEngine;
using SmartMove.AI;
#if MRUK_INSTALLED
using Meta.XR.BuildingBlocks.AIBlocks;
#endif

public enum CaptureMode { BestObject, AllObjects, LockedBox }
public enum SnapshotMode { PcaGetColors, WebCamTexture }

public sealed class SegmentCaptureCoordinator : MonoBehaviour
{
    // ── Inspector ──────────────────────────────────────────────────────────

    [Header("Camera Sources")]
    [SerializeField] private PassthroughCameraAccess pcaCamera;
    [SerializeField] private WebCamTextureManager webCamManager;

    [Tooltip("PcaGetColors: frame-accurate, one blocking GPU readback per capture.\n" +
             "WebCamTexture: zero stall, 1-3 frame lag vs seg boxes.")]
    [SerializeField] private SnapshotMode snapshotMode = SnapshotMode.PcaGetColors;

    [Header("Save")]
    [SerializeField] private CaptureImageSaver imageSaver;

    [Header("HUD")]
    [SerializeField] private CaptureHUDFeedback hud;

    [Header("Capture Settings")]
    [Tooltip("LockedBox = use user-adjusted 3D box projection (recommended).\n" +
             "BestObject / AllObjects = legacy YOLO rect path.")]
    [SerializeField] private CaptureMode captureMode = CaptureMode.LockedBox;

    [Tooltip("Extra normalised padding around the projected box rect (0=none, 0.02=2%).")]
    [SerializeField, Range(0f, 0.1f)] private float boxProjectionPadding = 0.015f;

    [Tooltip("Pixel padding used by the legacy YOLO rect crop path.")]
    [SerializeField, Range(0, 64)] private int cropPadding = 20;

    [Tooltip("Minimum seconds between captures.")]
    [SerializeField, Range(0.1f, 5f)] private float captureCooldownSec = 1.0f;

    [Header("Debug")]
    [SerializeField] private bool debugLog = true;

    // ── Frozen frame state ─────────────────────────────────────────────────

    private Texture2D _frozenTex2D;
    private RenderTexture _frozenRT;
    private int _frozenW, _frozenH;

    /// <summary>
    /// Camera pose recorded at the exact moment the frame pixels were captured.
    /// Used by CaptureFromLockedBox() so the ray grid matches the frozen frame.
    /// NOT the current live head pose.
    /// </summary>
    private Pose _frozenCameraPose;

    // Legacy YOLO rect fallback
    private readonly List<ObjectSegRect> _frozenRects = new();

    private struct ObjectSegRect
    {
        public string Label;
        public int ObjectIndex;
        public Rect NormRect;   // Y=0 at bottom
        public float Score;
    }

    // ── Locked box state ───────────────────────────────────────────────────

    /// <summary>
    /// World-space transform of the user-adjusted BoxManipulator.
    /// Set by OnSystemLocked(). Used exclusively by CaptureFromLockedBox().
    /// </summary>
    private Transform _lockedBoxTransform;
    private string _lockedBoxLabel = "object";

    // ── State pushed by SegmentationAnchorController ──────────────────────

    private bool _isLocked;
    private List<GameObject> _frozenBoxes = new();

#if MRUK_INSTALLED
    private BoundingBoxMarker[] _frozenMarkers = Array.Empty<BoundingBoxMarker>();
    private SegmentationResult _lastSegResult;
    private CustomImageSegmentationAgent _agent;
#endif

    // ── Runtime ───────────────────────────────────────────────────────────

    private float _lastCaptureTime = -999f;
    private bool _captureInProgress;

    // ── Debug properties — read by CaptureDebugHUD, no reflection needed ──

    /// <summary>Coordinator has received OnSystemLocked and is awaiting a pinch.</summary>
    public bool DbgIsLocked => _isLocked;
    /// <summary>A capture coroutine/method is currently executing.</summary>
    public bool DbgCaptureInProgress => _captureInProgress;
    /// <summary>Seconds remaining on the cooldown gate. 0 = clear.</summary>
    public float DbgCooldownRemaining => Mathf.Max(0f, captureCooldownSec - (Time.time - _lastCaptureTime));
    /// <summary>A Texture2D from the PCA path is ready.</summary>
    public bool DbgHasFrozenTex2D => _frozenTex2D != null;
    /// <summary>A RenderTexture from the WebCam path is ready.</summary>
    public bool DbgHasFrozenRT => _frozenRT != null;
    /// <summary>Resolution of the frozen frame. (0,0) if none.</summary>
    public Vector2Int DbgFrozenResolution => new(_frozenW, _frozenH);
    /// <summary>Camera pose recorded at last snapshot time.</summary>
    public Pose DbgFrozenCameraPose => _frozenCameraPose;
    /// <summary>World-space transform of the locked box. Null if not locked.</summary>
    public Transform DbgLockedBoxTransform => _lockedBoxTransform;
    /// <summary>Label of the locked object.</summary>
    public string DbgLockedBoxLabel => _lockedBoxLabel;
    /// <summary>Number of frozen boxes from the last lock.</summary>
    public int DbgFrozenBoxCount => _frozenBoxes?.Count ?? 0;
    /// <summary>Active snapshot mode (PcaGetColors or WebCamTexture).</summary>
    public SnapshotMode DbgSnapshotMode => snapshotMode;
    /// <summary>Active capture mode.</summary>
    public CaptureMode DbgCaptureMode => captureMode;
    /// <summary>PCA camera IsPlaying state.</summary>
    public bool DbgPcaIsPlaying => pcaCamera != null && pcaCamera.IsPlaying;
    /// <summary>Last projection result corner count. -1 = never run.</summary>
    public int DbgLastProjectionCorners { get; private set; } = -1;
    /// <summary>Last projection rect. Zero if never run or invalid.</summary>
    public Rect DbgLastProjectionRect { get; private set; } = Rect.zero;
    /// <summary>Last block reason string — why TryTriggerCapture failed, or "OK" if it proceeded.</summary>
    public string DbgLastBlockReason { get; private set; } = "not triggered yet";

    // ── Lifecycle ──────────────────────────────────────────────────────────

    private void Awake()
    {
        if (imageSaver == null) imageSaver = GetComponent<CaptureImageSaver>();
        if (hud == null) hud = FindAnyObjectByType<CaptureHUDFeedback>();
        if (pcaCamera == null) pcaCamera = FindAnyObjectByType<PassthroughCameraAccess>();
        if (webCamManager == null) webCamManager = FindAnyObjectByType<WebCamTextureManager>();

#if MRUK_INSTALLED
        _agent = GetComponent<CustomImageSegmentationAgent>();
        if (_agent == null)
            Debug.LogError("[Coordinator] ImageSegmentationAgent missing on this GameObject.");
#endif
        if (pcaCamera == null) Debug.LogWarning("[Coordinator] PassthroughCameraAccess not found.");
        if (webCamManager == null) Debug.LogWarning("[Coordinator] WebCamTextureManager not found.");
        if (imageSaver == null) Debug.LogError("[Coordinator] CaptureImageSaver missing.");
    }

    private void OnEnable()
    {
#if MRUK_INSTALLED
        if (_agent != null) _agent.OnSegmentationUpdated += CacheSegResult;
#endif
        if (imageSaver != null) imageSaver.OnSaveComplete += OnSaveComplete;
    }

    private void OnDisable()
    {
#if MRUK_INSTALLED
        if (_agent != null) _agent.OnSegmentationUpdated -= CacheSegResult;
#endif
        if (imageSaver != null) imageSaver.OnSaveComplete -= OnSaveComplete;
    }

    private void OnDestroy()
    {
        if (_frozenRT != null) { _frozenRT.Release(); Destroy(_frozenRT); }
        if (_frozenTex2D != null) Destroy(_frozenTex2D);
    }

    // ── Seg result cache — runs every inference tick while unlocked ────────

#if MRUK_INSTALLED
    private void CacheSegResult(SegmentationResult result)
    {
        if (result == null || result.numObjects == 0) return;

        _lastSegResult = result;

        // Keep a fresh snapshot ready for immediate use at lock time.
        switch (snapshotMode)
        {
            case SnapshotMode.PcaGetColors: SnapshotViaPcaColors(); break;
            case SnapshotMode.WebCamTexture: SnapshotViaWebCam(); break;
        }

        // Also build YOLO rects as fallback
        BuildNormRects(result);
    }
#endif

    // ── Called by SegmentationAnchorController ─────────────────────────────
    //
    // OVERLOAD A — new path: pass the locked BoxManipulator transform.

#if MRUK_INSTALLED
    public void OnSystemLocked(
        List<GameObject> frozenBoxes,
        BoundingBoxMarker[] markers,
        Transform lockedBoxTransform,
        string lockedLabel = "object")
    {
        _frozenBoxes = frozenBoxes;
        _frozenMarkers = markers ?? Array.Empty<BoundingBoxMarker>();
        _lockedBoxTransform = lockedBoxTransform;
        _lockedBoxLabel = string.IsNullOrEmpty(lockedLabel) ? "object" : lockedLabel;
        _isLocked = true;

        // Do NOT snapshot and do NOT call TryTriggerCapture here.
        // Snapshot happens at pinch time (NotifyLeftMiddlePinch → TryTriggerCapture
        // → CaptureFromLockedBox) so frame + pose + box are from the same instant.

        if (debugLog)
            Debug.Log($"[Coordinator] Locked — waiting for pinch. " +
                      $"label={_lockedBoxLabel} boxes={_frozenBoxes.Count} " +
                      $"boxTransform={(lockedBoxTransform != null ? lockedBoxTransform.name : "null")} " +
                      $"mode={snapshotMode}");
    }

    // OVERLOAD B — legacy path (no box transform). Falls back to YOLO rects.
    public void OnSystemLocked(List<GameObject> frozenBoxes, BoundingBoxMarker[] markers)
    {
        OnSystemLocked(frozenBoxes, markers, null, "object");
    }
#else
    public void OnSystemLocked(
        List<GameObject> frozenBoxes,
        object[]         markers,
        Transform        lockedBoxTransform = null,
        string           lockedLabel        = "object")
    {
        _frozenBoxes        = frozenBoxes;
        _lockedBoxTransform = lockedBoxTransform;
        _lockedBoxLabel     = lockedLabel;
        _isLocked           = true;
        // No snapshot, no TryTriggerCapture — pinch fires capture.
    }
#endif

    public void OnSystemUnlocked()
    {
        _isLocked = false;
        _lockedBoxTransform = null;
        _frozenBoxes = new List<GameObject>();
#if MRUK_INSTALLED
        _frozenMarkers = Array.Empty<BoundingBoxMarker>();
#endif
        _frozenRects.Clear();
        if (debugLog) Debug.Log("[Coordinator] Unlocked.");
    }

    public void NotifyLeftMiddlePinch()
    {
        if (_isLocked) TryTriggerCapture();
    }

    public void TriggerCapture() => TryTriggerCapture();
    public string SaveDirectory => imageSaver?.GetSavePath() ?? string.Empty;

    // ── Snapshot: PCA GetColors() path ────────────────────────────────────
    // Rotate180 = flip both X and Y to correct Quest sensor orientation.
    // _frozenCameraPose is recorded HERE — same moment as the pixels.

    private void SnapshotViaPcaColors()
    {
        if (pcaCamera == null || !pcaCamera.IsPlaying)
        {
            if (debugLog) Debug.LogWarning("[Coordinator] PCA not playing — falling back to WebCam.");
            SnapshotViaWebCam();
            return;
        }

        var colors = pcaCamera.GetColors();
        if (!colors.IsCreated || colors.Length == 0)
        {
            if (debugLog) Debug.LogWarning("[Coordinator] PCA GetColors() empty — falling back to WebCam.");
            SnapshotViaWebCam();
            return;
        }

        // ── Record pose at pixel-grab time ────────────────────────────────
        // GetCameraPose() uses the hardware timestamp and applies LensOffset —
        // this is the actual tracked camera pose. pcaCamera.transform is always
        // at world origin and must NOT be used here.
        _frozenCameraPose = pcaCamera.GetCameraPose();

        int w = pcaCamera.CurrentResolution.x;
        int h = pcaCamera.CurrentResolution.y;

        if (_frozenTex2D == null || _frozenTex2D.width != w || _frozenTex2D.height != h)
        {
            if (_frozenTex2D != null) Destroy(_frozenTex2D);
            _frozenTex2D = new Texture2D(w, h, TextureFormat.RGBA32, false);
        }

        // Rotate180: flip both X and Y to correct Quest PCA sensor orientation.
        var rotated = Rotate180(colors, w, h);
        _frozenTex2D.SetPixels32(rotated);
        _frozenTex2D.Apply(false);

        _frozenW = w;
        _frozenH = h;

        if (debugLog)
            Debug.Log($"[Coordinator] PCA snapshot {w}x{h} (Rotate180) " +
                      $"pose={_frozenCameraPose.position:F3}");
    }

    // Rotate180: dst[y*w+x] = src[(h-1-y)*w + (w-1-x)]
    // Input:  top-down, X=0 right (raw PCA convention)
    // Output: bottom-up, X=0 left (Texture2D convention) + 180° rotation
    private static Color32[] Rotate180(
        Unity.Collections.NativeArray<Color32> src, int w, int h)
    {
        var dst = new Color32[w * h];
        for (int y = 0; y < h; y++)
        {
            int srcRow = h - 1 - y;
            for (int x = 0; x < w; x++)
                dst[y * w + x] = src[srcRow * w + (w - 1 - x)];
        }
        return dst;
    }

    // ── Snapshot: WebCamTexture path ───────────────────────────────────────
    // _frozenCameraPose is recorded HERE — same moment as the blit.

    private void SnapshotViaWebCam()
    {
        if (webCamManager == null) return;

        WebCamTexture wcTex = webCamManager.WebCamTexture;
        if (wcTex == null || !wcTex.isPlaying)
        {
            if (debugLog) Debug.LogWarning("[Coordinator] WebCamTexture not playing.");
            return;
        }

        int w = wcTex.width;
        int h = wcTex.height;

        if (_frozenRT == null || _frozenRT.width != w || _frozenRT.height != h)
        {
            if (_frozenRT != null) { _frozenRT.Release(); Destroy(_frozenRT); }
            _frozenRT = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32);
            _frozenRT.filterMode = FilterMode.Bilinear;
            _frozenRT.Create();
        }

        // ── Record pose at blit time ──────────────────────────────────────
        // Same rule: GetCameraPose() not pcaCamera.transform.
        _frozenCameraPose = pcaCamera != null ? pcaCamera.GetCameraPose() : default;

        Graphics.Blit(wcTex, _frozenRT);
        GL.Flush();

        _frozenW = w;
        _frozenH = h;

        if (debugLog)
            Debug.Log($"[Coordinator] WebCam snapshot {w}x{h} " +
                      $"pose={_frozenCameraPose.position:F3}");
    }

    // ── Build YOLO norm rects (legacy fallback only) ───────────────────────

#if MRUK_INSTALLED
    private void BuildNormRects(SegmentationResult result)
    {
        _frozenRects.Clear();

        for (int i = 0; i < result.numObjects; i++)
        {
            int classId = result.classIds[i];
            string label = (result.labels != null && classId < result.labels.Length)
                              ? result.labels[classId] : $"obj_{classId}";

            int o = i * 4;
            float cxN = result.boxes[o + 0];
            float cyN = result.boxes[o + 1];
            float wN = result.boxes[o + 2];
            float hN = result.boxes[o + 3];
            float score = (result.scores != null && i < result.scores.Length)
                           ? result.scores[i] : 1f;

            // PCA path: both X and Y mirrored (Rotate180 applied to texture).
            // WebCam path: Y-only mirror.
            Rect normRect;
            if (snapshotMode == SnapshotMode.PcaGetColors)
            {
                float newCx = 1f - cxN;
                float newCy = 1f - cyN;
                normRect = new Rect(newCx - wN * 0.5f, newCy - hN * 0.5f, wN, hN);
            }
            else
            {
                float cyFlipped = 1f - cyN;
                normRect = new Rect(cxN - wN * 0.5f, cyFlipped - hN * 0.5f, wN, hN);
            }

            _frozenRects.Add(new ObjectSegRect
            {
                Label = label,
                ObjectIndex = i,
                NormRect = normRect,
                Score = score
            });
        }
    }
#endif

    // ── Capture gate ───────────────────────────────────────────────────────

    private void TryTriggerCapture()
    {
        if (_captureInProgress)
        {
            DbgLastBlockReason = "capture already in progress";
            if (debugLog) Debug.Log("[Coordinator] Already in progress.");
            return;
        }

        float now = Time.time;
        float cooldown = captureCooldownSec - (now - _lastCaptureTime);
        if (cooldown > 0f)
        {
            DbgLastBlockReason = $"cooldown {cooldown:F1}s";
            if (debugLog) Debug.Log($"[Coordinator] Cooldown {cooldown:F1}s.");
            return;
        }

        _lastCaptureTime = now;
        _captureInProgress = true;

        // ── Primary path: 3D box projection ──────────────────────────────
        // Takes its own fresh snapshot — do NOT pre-check for a frozen frame here.
        if (captureMode == CaptureMode.LockedBox && _lockedBoxTransform != null)
        {
            DbgLastBlockReason = "OK — snapshot+capture started";
            CaptureFromLockedBox();
            return;
        }

        // ── Legacy YOLO rect paths — need a pre-warmed frozen frame ───────
        bool hasFrame = (snapshotMode == SnapshotMode.PcaGetColors && _frozenTex2D != null)
                     || (snapshotMode == SnapshotMode.WebCamTexture && _frozenRT != null);
        if (!hasFrame)
        {
            DbgLastBlockReason = "no frozen frame for YOLO path (camera not warmed up)";
            Debug.LogWarning("[Coordinator] No frozen frame available for YOLO path.");
            hud?.ShowError("Camera frame unavailable");
            _captureInProgress = false;
            return;
        }

        if (_frozenRects.Count == 0)
        {
            DbgLastBlockReason = "no YOLO rects — was inference running before lock?";
            Debug.LogWarning("[Coordinator] No seg rects — was inference running before lock?");
            hud?.ShowError("No objects detected");
            _captureInProgress = false;
            return;
        }

        DbgLastBlockReason = "OK — YOLO path started";
        switch (captureMode)
        {
            case CaptureMode.BestObject: CaptureBestObject(); break;
            case CaptureMode.AllObjects: StartCoroutine(CaptureAllObjects()); break;
            default:
                _captureInProgress = false;
                break;
        }
    }

    // ── PRIMARY: Capture from locked 3D box ───────────────────────────────

    private void CaptureFromLockedBox()
    {
        if (pcaCamera == null)
        {
            DbgLastBlockReason = "PCA camera is null";
            Debug.LogError("[Coordinator] CaptureFromLockedBox: PCA camera is null.");
            _captureInProgress = false;
            return;
        }

        // Snapshot at pinch time — frame, pose, and box are all from this instant.
        switch (snapshotMode)
        {
            case SnapshotMode.PcaGetColors: SnapshotViaPcaColors(); break;
            case SnapshotMode.WebCamTexture: SnapshotViaWebCam(); break;
        }

        bool hasFrame = (snapshotMode == SnapshotMode.PcaGetColors && _frozenTex2D != null)
                     || (snapshotMode == SnapshotMode.WebCamTexture && _frozenRT != null);
        if (!hasFrame)
        {
            DbgLastBlockReason = "pinch-time snapshot failed — camera source unavailable";
            Debug.LogError("[Coordinator] Pinch-time snapshot failed.");
            hud?.ShowError("Camera frame unavailable");
            _captureInProgress = false;
            return;
        }

        if (debugLog)
            Debug.Log($"[Coordinator] Pinch snapshot OK. frame={_frozenW}x{_frozenH} " +
                      $"pose={_frozenCameraPose.position:F3}");

        var projection = BoxProjectionCrop.ComputeCropRect(
            _lockedBoxTransform,
            pcaCamera,
            _frozenCameraPose,
            boxProjectionPadding);

        // Write debug state regardless of validity
        DbgLastProjectionCorners = projection.VisibleCorners;
        DbgLastProjectionRect = projection.NormRect;

        if (!projection.IsValid)
        {
            DbgLastBlockReason = $"box projection invalid — visibleCorners={projection.VisibleCorners}/8. " +
                                 $"posePos={_frozenCameraPose.position:F3} " +
                                 $"boxPos={_lockedBoxTransform.position:F3}";
            Debug.LogWarning($"[Coordinator] Box projection invalid " +
                             $"(visibleCorners={projection.VisibleCorners}) " +
                             $"frozenPose={_frozenCameraPose.position:F3} " +
                             $"boxPos={_lockedBoxTransform.position:F3}");
            hud?.ShowError("Box not visible");
            _captureInProgress = false;
            return;
        }

        if (debugLog)
            Debug.Log($"[Coordinator] BoxProjection [{_lockedBoxLabel}] " +
                      $"rect=({projection.NormRect.xMin:F3},{projection.NormRect.yMin:F3}," +
                      $"{projection.NormRect.width:F3},{projection.NormRect.height:F3}) " +
                      $"corners={projection.VisibleCorners}/8");

        Texture2D tex = CropFromFrozenFrame(projection.NormRect, _lockedBoxLabel, objectIndex: 0);
        if (tex == null)
        {
            DbgLastBlockReason = "CropFromFrozenFrame returned null";
            _captureInProgress = false;
            return;
        }

        DbgLastBlockReason = $"OK — crop {tex.width}x{tex.height} saved";
        hud?.ShowCaptureTriggered(_lockedBoxLabel, 1f, 1);
        DispatchSaveAndHUD(tex, _lockedBoxLabel, 0, projection.NormRect);
        _captureInProgress = false;
    }

    // ── Legacy: BestObject (YOLO rect) ────────────────────────────────────

    private void CaptureBestObject()
    {
#if MRUK_INSTALLED
        ObjectSegRect best = default;
        float bestScore = -1f;
        foreach (var sr in _frozenRects)
            if (sr.Score > bestScore) { bestScore = sr.Score; best = sr; }

        if (bestScore < 0f)
        {
            hud?.ShowError("No valid object");
            _captureInProgress = false;
            return;
        }

        hud?.ShowCaptureTriggered(best.Label, best.Score, 1);
        Texture2D tex = CropFromFrozenFrame(best.NormRect, best.Label, best.ObjectIndex);
        if (tex == null) { _captureInProgress = false; return; }

        DispatchSaveAndHUD(tex, best.Label, best.ObjectIndex, best.NormRect);
        _captureInProgress = false;
#else
        _captureInProgress = false;
#endif
    }

    // ── Legacy: AllObjects (YOLO rects) ───────────────────────────────────

    private IEnumerator CaptureAllObjects()
    {
#if MRUK_INSTALLED
        hud?.ShowCaptureTriggered("All objects", 1f, _frozenRects.Count);
        int saved = 0;

        foreach (var sr in _frozenRects)
        {
            Texture2D tex = CropFromFrozenFrame(sr.NormRect, sr.Label, sr.ObjectIndex);
            if (tex == null) { yield return null; continue; }
            DispatchSaveAndHUD(tex, sr.Label, sr.ObjectIndex, sr.NormRect);
            saved++;
            yield return null;
        }

        if (debugLog)
            Debug.Log($"[Coordinator] AllObjects: {saved}/{_frozenRects.Count} saved.");
        _captureInProgress = false;
#else
        _captureInProgress = false;
        yield break;
#endif
    }

    // ── Core crop dispatcher ───────────────────────────────────────────────

    private Texture2D CropFromFrozenFrame(Rect normRect, string label, int objectIndex)
    {
        if (snapshotMode == SnapshotMode.PcaGetColors && _frozenTex2D != null)
            return CropFromTex2D(normRect, label, objectIndex);
        if (snapshotMode == SnapshotMode.WebCamTexture && _frozenRT != null)
            return CropFromRT(normRect, label, objectIndex);

        Debug.LogError("[Coordinator] No valid frozen frame to crop from.");
        return null;
    }

    // Crop from Texture2D (PCA path).
    // normRect: Y=0 at bottom (Texture2D convention) — already handled by BoxProjectionCrop.
    private Texture2D CropFromTex2D(Rect normRect, string label, int objectIndex)
    {
        int fw = _frozenTex2D.width;
        int fh = _frozenTex2D.height;

        int xMin = Mathf.Clamp(Mathf.FloorToInt(normRect.xMin * fw) - cropPadding, 0, fw - 1);
        int xMax = Mathf.Clamp(Mathf.CeilToInt(normRect.xMax * fw) + cropPadding, 1, fw);
        int yMin = Mathf.Clamp(Mathf.FloorToInt(normRect.yMin * fh) - cropPadding, 0, fh - 1);
        int yMax = Mathf.Clamp(Mathf.CeilToInt(normRect.yMax * fh) + cropPadding, 1, fh);

        int cropW = xMax - xMin;
        int cropH = yMax - yMin;

        if (cropW <= 0 || cropH <= 0)
        {
            Debug.LogWarning($"[Coordinator] Degenerate crop [{label}#{objectIndex}]: {cropW}x{cropH}");
            return null;
        }

        Color[] pixels = _frozenTex2D.GetPixels(xMin, yMin, cropW, cropH);
        var crop = new Texture2D(cropW, cropH, TextureFormat.RGB24, false);
        crop.SetPixels(pixels);
        crop.Apply(false);

        if (debugLog)
            Debug.Log($"[Coordinator] Tex2D crop [{label}#{objectIndex}] " +
                      $"({xMin},{yMin}) {cropW}x{cropH}");
        return crop;
    }

    // Crop from RenderTexture (WebCam path).
    private Texture2D CropFromRT(Rect normRect, string label, int objectIndex)
    {
        int fw = _frozenW;
        int fh = _frozenH;

        int xMin = Mathf.Clamp(Mathf.FloorToInt(normRect.xMin * fw) - cropPadding, 0, fw - 1);
        int xMax = Mathf.Clamp(Mathf.CeilToInt(normRect.xMax * fw) + cropPadding, 1, fw);
        int yMin = Mathf.Clamp(Mathf.FloorToInt(normRect.yMin * fh) - cropPadding, 0, fh - 1);
        int yMax = Mathf.Clamp(Mathf.CeilToInt(normRect.yMax * fh) + cropPadding, 1, fh);

        int cropW = xMax - xMin;
        int cropH = yMax - yMin;

        if (cropW <= 0 || cropH <= 0)
        {
            Debug.LogWarning($"[Coordinator] Degenerate crop [{label}#{objectIndex}]: {cropW}x{cropH}");
            return null;
        }

        var prevActive = RenderTexture.active;
        RenderTexture.active = _frozenRT;

        var crop = new Texture2D(cropW, cropH, TextureFormat.RGB24, false);
        crop.ReadPixels(new Rect(xMin, yMin, cropW, cropH), 0, 0);
        crop.Apply(false);

        RenderTexture.active = prevActive;

        if (debugLog)
            Debug.Log($"[Coordinator] RT crop [{label}#{objectIndex}] " +
                      $"({xMin},{yMin}) {cropW}x{cropH}");
        return crop;
    }

    // ── Save + HUD ─────────────────────────────────────────────────────────

    private void DispatchSaveAndHUD(Texture2D tex, string label, int objectIndex, Rect normRect)
    {
        var crop = new ObjectCrop
        {
            Texture = tex,
            Label = label,
            Score = 1f,
            ClassId = objectIndex,
            ObjectIndex = objectIndex,
            CameraRect = new Rect(
                                normRect.xMin * _frozenW, normRect.yMin * _frozenH,
                                normRect.width * _frozenW, normRect.height * _frozenH),
            WorldPosition = _lockedBoxTransform != null
                            ? _lockedBoxTransform.position
                            : Vector3.zero,
            CaptureTime = DateTime.Now
        };

        imageSaver?.SaveCropAsync(crop);
        hud?.ShowSaveSuccess(
            new SaveResult(true, $"{label}_{DateTime.Now:HHmmss_fff}.png", label), tex);

        StartCoroutine(DelayedDestroy(tex, 4f));
    }

    private void OnSaveComplete(SaveResult r)
    {
        if (r.Success) Debug.Log($"[Coordinator] ✓ {System.IO.Path.GetFileName(r.FilePath)}");
        else Debug.LogError($"[Coordinator] ✗ [{r.Label}]: {r.Error}");
    }

    private IEnumerator DelayedDestroy(UnityEngine.Object obj, float delay)
    {
        yield return new WaitForSeconds(delay);
        if (obj != null) Destroy(obj);
    }
}