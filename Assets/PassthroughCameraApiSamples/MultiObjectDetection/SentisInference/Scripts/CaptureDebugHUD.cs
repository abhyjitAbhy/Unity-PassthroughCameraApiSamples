// CaptureDebugHUD.cs — v2
//
// Direct property reads from SegmentCaptureCoordinator (no reflection).
// No execution-order patch — the coordinator does not use _wasPinching.
// Displays the full capture chain state every frame.
//
// Drop on any GameObject. Assign debugText in Inspector or it auto-finds.

using TMPro;
using UnityEngine;

#if MRUK_INSTALLED
using Meta.XR.BuildingBlocks.AIBlocks;
#endif

public sealed class CaptureDebugHUD : MonoBehaviour
{
    [Header("Display")]
    [SerializeField] private TextMeshProUGUI debugText;

    [Header("References — auto-found if null")]
    [SerializeField] private HandPinchDetector pinchDetector;
    [SerializeField] private SegmentCaptureCoordinator coordinator;
    [SerializeField] private OVRHand leftHand;

#if MRUK_INSTALLED
    [SerializeField] private SegmentationAnchorController anchorController;
#endif

    // Counters
    private int _pinchHeldFrames;
    private int _captureAttempts;
    private bool _prevCaptureInProgress;
    private string _lastSuccessTime = "never";

    private void Awake()
    {
        if (pinchDetector == null)
            pinchDetector = FindAnyObjectByType<HandPinchDetector>();
        if (coordinator == null)
            coordinator = FindAnyObjectByType<SegmentCaptureCoordinator>();
        if (debugText == null)
            debugText = GetComponentInChildren<TextMeshProUGUI>();

#if MRUK_INSTALLED
        if (anchorController == null)
            anchorController = FindAnyObjectByType<SegmentationAnchorController>();
#endif

        if (leftHand == null)
        {
            foreach (var h in FindObjectsByType<OVRHand>(
                         FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if ((OVRHand.Hand)h.GetHand() == OVRHand.Hand.HandLeft)
                { leftHand = h; break; }
            }
        }
    }

    private void LateUpdate()
    {
        // Track capture completions for the counter
        if (coordinator != null)
        {
            bool inProgress = coordinator.DbgCaptureInProgress;
            if (_prevCaptureInProgress && !inProgress
                && coordinator.DbgLastBlockReason.StartsWith("OK — crop"))
            {
                _captureAttempts++;
                _lastSuccessTime = System.DateTime.Now.ToString("HH:mm:ss");
            }
            _prevCaptureInProgress = inProgress;
        }

        BuildText();
    }

    private void BuildText()
    {
        if (debugText == null) return;

        // ── Pinch state ───────────────────────────────────────────────────
        bool leftTracked = leftHand != null && leftHand.IsTracked;
        float middleStrength = leftHand != null
            ? leftHand.GetFingerPinchStrength(OVRHand.HandFinger.Middle) : -1f;
        bool leftMiddle = pinchDetector != null && pinchDetector.leftMiddlePinch;
        bool rightIndex = pinchDetector != null && pinchDetector.rightIndexPinch;
        bool rightMiddle = pinchDetector != null && pinchDetector.rightMiddlePinch;

        if (leftMiddle) _pinchHeldFrames++; else _pinchHeldFrames = 0;

#if MRUK_INSTALLED
        // ── SAC state ─────────────────────────────────────────────────────
        // SegmentationAnchorController doesn't expose public props yet —
        // check coordinator's mirrored locked state instead (set via OnSystemLocked).
        bool sacFound = anchorController != null;
#endif

        // ── Coordinator state ─────────────────────────────────────────────
        bool coordFound = coordinator != null;
        bool coordLocked = coordFound && coordinator.DbgIsLocked;
        bool captureInProgress = coordFound && coordinator.DbgCaptureInProgress;
        float cooldownRemaining = coordFound ? coordinator.DbgCooldownRemaining : -1f;
        bool cooldownClear = cooldownRemaining <= 0f;
        bool hasFrozenTex = coordFound && coordinator.DbgHasFrozenTex2D;
        bool hasFrozenRT = coordFound && coordinator.DbgHasFrozenRT;
        Vector2Int frozenRes = coordFound ? coordinator.DbgFrozenResolution : Vector2Int.zero;
        Pose frozenPose = coordFound ? coordinator.DbgFrozenCameraPose : default;
        bool hasBoxTransform = coordFound && coordinator.DbgLockedBoxTransform != null;
        string boxLabel = coordFound ? coordinator.DbgLockedBoxLabel : "—";
        int frozenBoxCount = coordFound ? coordinator.DbgFrozenBoxCount : 0;
        SnapshotMode snapMode = coordFound ? coordinator.DbgSnapshotMode : default;
        CaptureMode capMode = coordFound ? coordinator.DbgCaptureMode : default;
        bool pcaPlaying = coordFound && coordinator.DbgPcaIsPlaying;
        int projCorners = coordFound ? coordinator.DbgLastProjectionCorners : -1;
        Rect projRect = coordFound ? coordinator.DbgLastProjectionRect : Rect.zero;
        string blockReason = coordFound ? coordinator.DbgLastBlockReason : "coordinator not found";

        // ── Block reason: first failing gate ─────────────────────────────
        string gate;
        if (!coordFound) gate = "SegmentCaptureCoordinator NOT FOUND";
        else if (!pcaPlaying) gate = "PCA camera NOT PLAYING";
        else if (!leftTracked) gate = "Left hand NOT TRACKED";
        else if (!leftMiddle) gate = "Left middle pinch not active";
        else if (!coordLocked) gate = "Coordinator NOT LOCKED (right-index-pinch to lock)";
        else if (!hasBoxTransform) gate = "No box transform (OnSystemLocked not called?)";
        else if (captureInProgress) gate = "Capture IN PROGRESS";
        else if (!cooldownClear) gate = $"Cooldown {cooldownRemaining:F1}s remaining";
        else gate = "All gates CLEAR — pinch should fire";

        bool gateOk = gate.StartsWith("All gates");

        // ── Frozen frame summary ──────────────────────────────────────────
        string frameStatus;
        if (snapMode == SnapshotMode.PcaGetColors)
            frameStatus = hasFrozenTex
                ? $"Tex2D {frozenRes.x}x{frozenRes.y}"
                : "NO Tex2D yet";
        else
            frameStatus = hasFrozenRT
                ? $"RT {frozenRes.x}x{frozenRes.y}"
                : "NO RT yet";

        // ── Projection summary ────────────────────────────────────────────
        string projStatus = projCorners < 0
            ? "never run"
            : projCorners == 0
                ? "<color=red>0/8 corners visible — FAILED</color>"
                : $"<color=green>{projCorners}/8 corners</color> " +
                  $"rect({projRect.xMin:F2},{projRect.yMin:F2} {projRect.width:F2}x{projRect.height:F2})";

        debugText.text =
            "<b>── CAPTURE DEBUG ──</b>\n\n" +

            "<b>COMPONENTS</b>\n" +
            $"Coordinator      {B(coordFound)}\n" +
#if MRUK_INSTALLED
            $"AnchorController {B(sacFound)}\n" +
#endif
            $"PinchDetector    {B(pinchDetector != null)}\n" +
            $"PCA playing      {B(pcaPlaying)}\n\n" +

            "<b>HAND / PINCH</b>\n" +
            $"Left tracked     {B(leftTracked)}\n" +
            $"Middle strength  {middleStrength:F2}  (≥0.70 needed)\n" +
            $"Left middle      {B(leftMiddle)}  held {_pinchHeldFrames} frames\n" +
            $"Right index      {B(rightIndex)}  (lock)\n" +
            $"Right middle     {B(rightMiddle)}  (unlock)\n\n" +

            "<b>LOCK STATE</b>\n" +
            $"Coordinator locked   {B(coordLocked)}\n" +
            $"Has box transform    {B(hasBoxTransform)}\n" +
            $"Box label            {boxLabel}\n" +
            $"Frozen box count     {frozenBoxCount}\n\n" +

            "<b>CAMERA / FRAME</b>\n" +
            $"Snapshot mode    {snapMode}\n" +
            $"Capture mode     {capMode}\n" +
            $"Frozen frame     {frameStatus}\n" +
            $"Frozen pose pos  {frozenPose.position:F3}\n\n" +

            "<b>CAPTURE GATE</b>\n" +
            $"Cooldown clear   {B(cooldownClear)}" +
            (!cooldownClear ? $"  ({cooldownRemaining:F1}s)" : "") + "\n" +
            $"In progress      {B(captureInProgress)}\n" +
            $"Gate:  <color={(gateOk ? "green" : "red")}>{gate}</color>\n\n" +

            "<b>LAST PROJECTION</b>\n" +
            $"{projStatus}\n\n" +

            "<b>LAST BLOCK REASON</b>\n" +
            $"<color={(blockReason.StartsWith("OK") ? "green" : "orange")}>{blockReason}</color>\n\n" +

            "<b>RESULTS</b>\n" +
            $"Saves this session  {_captureAttempts}\n" +
            $"Last success        {_lastSuccessTime}";
    }

    private static string B(bool v) =>
        v ? "<color=green>TRUE</color>" : "<color=red>FALSE</color>";

#if !MRUK_INSTALLED
    private void Awake()
    {
        if (debugText != null)
            debugText.text = "MRUK_INSTALLED not defined.\nAdd to Scripting Define Symbols.";
    }
#endif
}