using System.Collections;
using System.Collections.Generic;
using PassthroughCameraSamples.MultiObjectDetection;
using TMPro;
using UnityEngine;

namespace Meta.XR.BuildingBlocks.AIBlocks
{
    [RequireComponent(typeof(ImageSegmentationAgent))]
    public sealed class SegmentationAnchorController_Debug : MonoBehaviour
    {
#if MRUK_INSTALLED

        [Header("UI")]
        [SerializeField] private TextMeshProUGUI debugText;

        [Header("Settings")]
        [SerializeField] private bool spamLogs = true;

        private ImageSegmentationAgent _agent;
        private HandPinchDetector _pinchDetector;
        private ImageSegmentationVisualizer _visualizer;

        private OVRSpatialAnchor _spatialAnchor;

        private bool _isHeadsetTracking = true;
        private bool _isLocked;
        private int _savedSegmentEveryNFrames;

        private readonly List<GameObject> _frozenBoxes = new();
        private string _debugInfo = "";
        private int _frameCounter;

        private void Awake()
        {
            _agent = GetComponent<ImageSegmentationAgent>();
            _pinchDetector = GetComponent<HandPinchDetector>();
            _visualizer = GetComponent<ImageSegmentationVisualizer>();

            if (_agent == null) LogError("ImageSegmentationAgent MISSING");
            if (_pinchDetector == null) LogError("HandPinchDetector MISSING");
            if (_visualizer == null) LogError("ImageSegmentationVisualizer MISSING");

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

        private void Update()
        {
            _frameCounter++;

            if (_pinchDetector == null) return;

            bool indexPinch = _pinchDetector.rightIndexPinch;
            bool middlePinch = _pinchDetector.rightMiddlePinch;

            if (debugText != null)
            {
                debugText.text =
                    $"FRAME: {_frameCounter}\n\n" +
                    $"INDEX PINCH:  {indexPinch}\n" +
                    $"MIDDLE PINCH: {middlePinch}\n\n" +
                    $"LOCKED: {_isLocked}\n" +
                    $"HEADSET TRACKING: {_isHeadsetTracking}\n\n" +
                    $"ANCHOR EXISTS:  {_spatialAnchor != null}\n" +
                    $"ANCHOR TRACKED: {(_spatialAnchor != null && _spatialAnchor.IsTracked)}\n\n" +
                    $"FROZEN BOXES: {_frozenBoxes.Count}\n\n" +
                    $"SEGMENT N: {(_agent != null ? _agent.segmentEveryNFrames : -1)}\n\n" +
                    $"LAST: {_debugInfo}";
            }

            if (indexPinch) LockAll();
            if (middlePinch) UnlockAll();
        }

        // =========================================================
        // LOCK
        // =========================================================

        private void LockAll()
        {
            if (_isLocked) { LogWarning("ALREADY LOCKED"); return; }

            // Grab every live BoundingBoxMarker currently active in scene
            var liveMarkers = FindObjectsByType<BoundingBoxMarker>(
                FindObjectsInactive.Exclude,
                FindObjectsSortMode.None);

            if (liveMarkers.Length == 0)
            {
                LogError("NO LIVE MARKERS FOUND");
                return;
            }

            // Stop segmentation — this stops Draw3D from being invoked,
            // which means the visualizer will no longer touch _live or _pool
            _savedSegmentEveryNFrames = _agent.segmentEveryNFrames;
            _agent.segmentEveryNFrames = 0;

            // Also disable the visualizer's Update loop so EMA expiry
            // doesn't reclaim our frozen boxes via the pool
            if (_visualizer != null)
                _visualizer.enabled = false;

            _frozenBoxes.Clear();

            foreach (var marker in liveMarkers)
            {
                var go = marker.gameObject;
                go.transform.SetParent(null, worldPositionStays: true);
                marker.IsLocked = true;
                _frozenBoxes.Add(go);

                // ADD THIS — triggers handle spawn
                var manipulator = go.GetComponent<BoxManipulator>();
                if (manipulator == null)
                    manipulator = go.AddComponent<BoxManipulator>();
                manipulator.Lock();
            }

            _isLocked = true;
            Log($"LOCK COMPLETE — {_frozenBoxes.Count} boxes frozen");
        }

        // =========================================================
        // UNLOCK
        // =========================================================

        private void UnlockAll()
        {
            if (!_isLocked) { LogWarning("NOT LOCKED"); return; }

            foreach (var go in _frozenBoxes)
            {
                if (go == null) continue;
                var manipulator = go.GetComponent<BoxManipulator>();
                if (manipulator != null) manipulator.Unlock(); // unregisters from BoxInteractionManager + destroys handles
                Destroy(go);
            }
            _frozenBoxes.Clear();

            // Reset visualizer internal state BEFORE re-enabling
            // so stale _live/_emaStates references don't block new box creation
            if (_visualizer != null)
            {
                _visualizer.ResetState();
                _visualizer.enabled = true;
            }

            if (_agent != null)
            {
                _agent.segmentEveryNFrames =
                    _savedSegmentEveryNFrames > 0
                    ? _savedSegmentEveryNFrames
                    : 1;
            }

            _isLocked = false;
            Log("UNLOCK COMPLETE");
        }

        // =========================================================
        // TRACKING
        // =========================================================

        private void OnTrackingLost() { _isHeadsetTracking = false; LogWarning("TRACKING LOST"); }
        private void OnTrackingAcquired() { _isHeadsetTracking = true; Log("TRACKING ACQUIRED"); }

        // =========================================================
        // ANCHOR
        // =========================================================

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
            if (!result.Success)
            {
                LogError($"SAVE FAILED: {result}");
                EraseSpatialAnchor();
            }
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
                if (!result.Success) { LogError($"LOAD FAILED: {result.Status}"); continue; }
                if (_spatialAnchor.IsTracked) { Log("TRACKING RESTORED"); yield break; }
            }

            LogError("TRACKING RESTORE FAILED");
            EraseSpatialAnchor();
        }

        private void EraseSpatialAnchor()
        {
            if (_spatialAnchor == null) return;
            _spatialAnchor.EraseAnchorAsync();
            DestroyImmediate(_spatialAnchor);
            _spatialAnchor = null;
        }

        // =========================================================
        // LOGGING
        // =========================================================

        private void Log(string msg) { _debugInfo = msg; if (spamLogs) Debug.Log($"[SAC] {msg}"); }
        private void LogWarning(string msg) { _debugInfo = msg; Debug.LogWarning($"[SAC] {msg}"); }
        private void LogError(string msg) { _debugInfo = msg; Debug.LogError($"[SAC] {msg}"); }

#endif
    }
}