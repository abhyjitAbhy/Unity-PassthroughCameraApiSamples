// SegmentationAnchorController.cs
//
// Updated to reference CustomImageSegmentationAgent and
// CustomImageSegmentationVisualizer instead of Meta's package-locked originals.
// All lock/unlock/anchor/pinch logic is unchanged.

using System.Collections;
using System.Collections.Generic;
using PassthroughCameraSamples.MultiObjectDetection;
using SmartMove;   // CustomImageSegmentationAgent, CustomImageSegmentationVisualizer
using UnityEngine;

namespace Meta.XR.BuildingBlocks.AIBlocks
{
    [RequireComponent(typeof(CustomImageSegmentationAgent))]
    [RequireComponent(typeof(CustomImageSegmentationVisualizer))]
    [RequireComponent(typeof(SegmentCaptureCoordinator))]
    public sealed class SegmentationAnchorController : MonoBehaviour
    {
#if MRUK_INSTALLED

        [Header("Settings")]
        [SerializeField] private bool spamLogs = true;

        // ── Internal refs ──────────────────────────────────────────────────────

        private CustomImageSegmentationAgent _agent;
        private HandPinchDetector _pinchDetector;
        private CustomImageSegmentationVisualizer _visualizer;
        private SegmentCaptureCoordinator _captureCoordinator;

        private OVRSpatialAnchor _spatialAnchor;

        private bool _isHeadsetTracking = true;
        private bool _isLocked;
        private int _savedSegmentEveryNFrames;

        private readonly List<GameObject> _frozenBoxes = new();
        private string _debugInfo = "";
        private int _frameCounter;
        private BoxManipulator _lockedManipulator;
        private string _lockedLabel = "object";

        // ── Lifecycle ──────────────────────────────────────────────────────────

        private void Awake()
        {
            _agent = GetComponent<CustomImageSegmentationAgent>();
            _pinchDetector = GetComponent<HandPinchDetector>();
            _visualizer = GetComponent<CustomImageSegmentationVisualizer>();
            _captureCoordinator = GetComponent<SegmentCaptureCoordinator>();

            if (_agent == null) LogError("CustomImageSegmentationAgent MISSING");
            if (_pinchDetector == null) LogError("HandPinchDetector MISSING");
            if (_visualizer == null) LogError("CustomImageSegmentationVisualizer MISSING");
            if (_captureCoordinator == null) LogError("SegmentCaptureCoordinator MISSING — add it to this GameObject");

            StartCoroutine(UpdateSpatialAnchor());

            OVRManager.TrackingLost += OnTrackingLost;
            OVRManager.TrackingAcquired += OnTrackingAcquired;
        }

        private void OnDestroy()
        {
            EraseSpatialAnchor();
            OVRManager.TrackingLost -= OnTrackingLost;
            OVRManager.TrackingAcquired -= OnTrackingAcquired;
        }

        // ── Update ─────────────────────────────────────────────────────────────

        private void Update()
        {
            _frameCounter++;
            if (_pinchDetector == null) return;

            bool rightIndex = _pinchDetector.rightIndexPinch;
            bool rightMiddle = _pinchDetector.rightMiddlePinch;
            bool leftMiddle = _pinchDetector.leftMiddlePinch;

            if (rightIndex) LockAll();
            if (rightMiddle) UnlockAll();
            if (leftMiddle && _isLocked) _captureCoordinator?.NotifyLeftMiddlePinch();
        }

        // ── LOCK ───────────────────────────────────────────────────────────────

        private void LockAll()
        {
            if (_isLocked) { LogWarning("ALREADY LOCKED"); return; }

            var liveMarkers = FindObjectsByType<BoundingBoxMarker>(
                FindObjectsInactive.Exclude,
                FindObjectsSortMode.None);

            if (liveMarkers.Length == 0)
            {
                LogError("NO LIVE MARKERS FOUND — nothing to lock");
                return;
            }

            _savedSegmentEveryNFrames = _agent.segmentEveryNFrames;
            _agent.segmentEveryNFrames = 0;

            if (_visualizer != null) _visualizer.enabled = false;

            _frozenBoxes.Clear();
            _lockedManipulator = null;
            _lockedLabel = "object";

            foreach (var marker in liveMarkers)
            {
                var go = marker.gameObject;
                go.transform.SetParent(null, worldPositionStays: true);
                marker.IsLocked = true;
                _frozenBoxes.Add(go);

                var manipulator = go.GetComponent<BoxManipulator>();
                if (manipulator == null) manipulator = go.AddComponent<BoxManipulator>();
                manipulator.Lock();

                if (_lockedManipulator == null)
                {
                    _lockedManipulator = manipulator;
                    _lockedLabel = go.name;
                }
            }

            _isLocked = true;
            Log($"LOCK COMPLETE — {_frozenBoxes.Count} boxes frozen, primary=[{_lockedLabel}]");

            _captureCoordinator?.OnSystemLocked(
                _frozenBoxes,
                liveMarkers,
                lockedBoxTransform: _lockedManipulator != null ? _lockedManipulator.transform : null,
                lockedLabel: _lockedLabel);
        }

        // ── UNLOCK ─────────────────────────────────────────────────────────────

        private void UnlockAll()
        {
            if (!_isLocked) { LogWarning("NOT LOCKED"); return; }

            _captureCoordinator?.OnSystemUnlocked();

            foreach (var go in _frozenBoxes)
            {
                if (go == null) continue;
                go.GetComponent<BoxManipulator>()?.Unlock();
                Destroy(go);
            }
            _frozenBoxes.Clear();

            _lockedManipulator = null;
            _lockedLabel = "object";

            if (_visualizer != null)
            {
                _visualizer.ResetState();   // exists on our custom class — safe
                _visualizer.enabled = true;
            }

            if (_agent != null)
                _agent.segmentEveryNFrames =
                    _savedSegmentEveryNFrames > 0 ? _savedSegmentEveryNFrames : 1;

            _isLocked = false;
            Log("UNLOCK COMPLETE");
        }

        // ── TRACKING ───────────────────────────────────────────────────────────

        private void OnTrackingLost() { _isHeadsetTracking = false; LogWarning("TRACKING LOST"); }
        private void OnTrackingAcquired() { _isHeadsetTracking = true; Log("TRACKING ACQUIRED"); }

        // ── SPATIAL ANCHOR ─────────────────────────────────────────────────────

        private IEnumerator UpdateSpatialAnchor()
        {
            while (true)
            {
                yield return null;

                if (_spatialAnchor == null)
                {
                    yield return CreateSpatialAnchorAndSave();
                    if (_spatialAnchor == null) continue;
                }

                if (!_spatialAnchor.IsTracked)
                    yield return RestoreSpatialAnchorTracking();
            }
        }

        private IEnumerator CreateSpatialAnchorAndSave()
        {
            _spatialAnchor = gameObject.AddComponent<OVRSpatialAnchor>();

            while (_spatialAnchor != null && !_spatialAnchor.Localized)
                yield return null;

            if (_spatialAnchor == null) yield break;

            var awaiter = _spatialAnchor.SaveAnchorAsync().GetAwaiter();
            while (!awaiter.IsCompleted) yield return null;

            var result = awaiter.GetResult();
            if (!result.Success) { LogError($"ANCHOR SAVE FAILED: {result}"); EraseSpatialAnchor(); }
            else Log("ANCHOR SAVED");
        }

        private IEnumerator RestoreSpatialAnchorTracking()
        {
            for (int i = 0; i < 20; i++)
            {
                yield return new WaitForSeconds(1f);
                if (!_isHeadsetTracking) continue;

                var unboundAnchors = new List<OVRSpatialAnchor.UnboundAnchor>(1);
                var awaiter = OVRSpatialAnchor.LoadUnboundAnchorsAsync(
                    new[] { _spatialAnchor.Uuid }, unboundAnchors).GetAwaiter();

                while (!awaiter.IsCompleted) yield return null;

                var result = awaiter.GetResult();
                if (!result.Success) { LogError($"ANCHOR LOAD FAILED: {result.Status}"); continue; }
                if (_spatialAnchor.IsTracked) { Log("TRACKING RESTORED"); yield break; }
            }

            LogError("TRACKING RESTORE FAILED AFTER 20 ATTEMPTS");
            EraseSpatialAnchor();
        }

        private void EraseSpatialAnchor()
        {
            if (_spatialAnchor == null) return;
            _spatialAnchor.EraseAnchorAsync();
            DestroyImmediate(_spatialAnchor);
            _spatialAnchor = null;
        }

        // ── LOGGING ────────────────────────────────────────────────────────────

        private void Log(string msg) { _debugInfo = msg; if (spamLogs) Debug.Log($"[SAC] {msg}"); }
        private void LogWarning(string msg) { _debugInfo = msg; Debug.LogWarning($"[SAC] {msg}"); }
        private void LogError(string msg) { _debugInfo = msg; Debug.LogError($"[SAC] {msg}"); }

#endif
    }
}