/*
 * FULL DEBUG VERSION
 * SegmentationAnchorController_Debug.cs
 *
 * Debug goals:
 * 1. Verify pinch detection works
 * 2. Verify Update() runs
 * 3. Verify BoundingBoxMarker exists
 * 4. Verify LockAll() executes
 * 5. Verify prefab spawning works
 * 6. Verify segmentation freeze works
 * 7. Verify unlock works
 * 8. Verify anchor state
 * 9. Show everything in TMP + Console
 */

using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace Meta.XR.BuildingBlocks.AIBlocks
{
    [RequireComponent(typeof(ImageSegmentationAgent))]
    public sealed class SegmentationAnchorController_Debug : MonoBehaviour
    {
#if MRUK_INSTALLED

        // =========================================================
        // INSPECTOR
        // =========================================================

        [Header("Visuals")]
        [SerializeField] private GameObject lockedBoxPrefab;
        [SerializeField] private GameObject anchorIndicatorPrefab;

        [Header("UI")]
        [SerializeField] private TextMeshProUGUI debugText;

        [Header("Settings")]
        [SerializeField] private bool showLockedLabel = true;
        [SerializeField] private bool spamLogs = true;

        // =========================================================
        // RUNTIME
        // =========================================================

        private ImageSegmentationAgent _agent;
        private HandPinchDetector _pinchDetector;

        private OVRSpatialAnchor _spatialAnchor;

        private bool _isHeadsetTracking = true;
        private bool _isLocked;

        private int _savedSegmentEveryNFrames;

        private readonly List<GameObject> _lockedVisuals = new();
        private readonly List<GameObject> _anchorIndicators = new();

        // Debug
        private string _debugInfo = "";
        private int _frameCounter;

        // =========================================================
        // UNITY
        // =========================================================

        private void Awake()
        {
            Log("AWAKE START");

            _agent = GetComponent<ImageSegmentationAgent>();
            _pinchDetector = GetComponent<HandPinchDetector>();

            if (_agent == null)
                LogError("ImageSegmentationAgent MISSING");

            if (_pinchDetector == null)
                LogError("HandPinchDetector MISSING");

            if (lockedBoxPrefab == null)
                LogError("lockedBoxPrefab NOT ASSIGNED");

            StartCoroutine(UpdateSpatialAnchor());

            OVRManager.TrackingLost += OnTrackingLost;
            OVRManager.TrackingAcquired += OnTrackingAcquired;

            Log("AWAKE COMPLETE");
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

            if (_pinchDetector == null)
            {
                LogError("Pinch detector NULL in Update()");
                return;
            }

            // =====================================================
            // DEBUG PINCH STATES
            // =====================================================

            bool indexPinch = _pinchDetector.rightIndexPinch;
            bool middlePinch = _pinchDetector.rightMiddlePinch;

            // =====================================================
            // TMP DEBUG
            // =====================================================

            if (debugText != null)
            {
                debugText.text =
                    $"FRAME: {_frameCounter}\n\n" +

                    $"RIGHT INDEX PINCH: {indexPinch}\n" +
                    $"RIGHT MIDDLE PINCH: {middlePinch}\n\n" +

                    $"LOCKED: {_isLocked}\n" +
                    $"HEADSET TRACKING: {_isHeadsetTracking}\n\n" +

                    $"ANCHOR EXISTS: {_spatialAnchor != null}\n" +
                    $"ANCHOR TRACKED: {(_spatialAnchor != null ? _spatialAnchor.IsTracked : false)}\n\n" +

                    $"LOCKED VISUALS: {_lockedVisuals.Count}\n" +
                    $"ANCHOR INDICATORS: {_anchorIndicators.Count}\n\n" +

                    $"SEGMENT EVERY N FRAMES: {(_agent != null ? _agent.segmentEveryNFrames : -1)}\n\n" +

                    $"LAST DEBUG:\n{_debugInfo}";
            }

            // =====================================================
            // LOCK
            // =====================================================

            if (indexPinch)
            {
                Log("RIGHT INDEX PINCH DETECTED");
                LockAll();
            }

            // =====================================================
            // UNLOCK
            // =====================================================

            if (middlePinch)
            {
                Log("RIGHT MIDDLE PINCH DETECTED");
                UnlockAll();
            }
        }

        // =========================================================
        // TRACKING
        // =========================================================

        private void OnTrackingLost()
        {
            _isHeadsetTracking = false;
            LogWarning("TRACKING LOST");
        }

        private void OnTrackingAcquired()
        {
            _isHeadsetTracking = true;
            Log("TRACKING ACQUIRED");
        }

        // =========================================================
        // ANCHOR LOOP
        // =========================================================

        private IEnumerator UpdateSpatialAnchor()
        {
            Log("ANCHOR COROUTINE STARTED");

            while (true)
            {
                yield return null;

                if (_spatialAnchor == null)
                {
                    Log("CREATING SPATIAL ANCHOR");

                    yield return CreateSpatialAnchorAndSave();

                    if (_spatialAnchor == null)
                    {
                        LogError("ANCHOR CREATION FAILED");
                        continue;
                    }
                }

                if (!_spatialAnchor.IsTracked)
                {
                    LogWarning("ANCHOR NOT TRACKED");
                    yield return RestoreSpatialAnchorTracking();
                }
            }
        }

        private IEnumerator CreateSpatialAnchorAndSave()
        {
            Log("ADDING OVRSpatialAnchor COMPONENT");

            _spatialAnchor = gameObject.AddComponent<OVRSpatialAnchor>();

            while (true)
            {
                if (_spatialAnchor == null)
                {
                    LogError("SPATIAL ANCHOR NULL");
                    yield break;
                }

                if (_spatialAnchor.Localized)
                {
                    Log("ANCHOR LOCALIZED");
                    break;
                }

                yield return null;
            }

            Log("SAVING ANCHOR");

            var awaiter = _spatialAnchor.SaveAnchorAsync().GetAwaiter();

            while (!awaiter.IsCompleted)
                yield return null;

            var result = awaiter.GetResult();

            if (!result.Success)
            {
                LogError($"SAVE FAILED: {result}");
                EraseSpatialAnchor();
                yield break;
            }

            Log("ANCHOR SAVED SUCCESSFULLY");
        }

        private IEnumerator RestoreSpatialAnchorTracking()
        {
            LogWarning("RESTORING TRACKING");

            const int retries = 20;

            for (int i = 0; i < retries; i++)
            {
                yield return new WaitForSeconds(1f);

                Log($"TRACKING RETRY {i}");

                if (!_isHeadsetTracking)
                {
                    LogWarning("HEADSET NOT TRACKING");
                    continue;
                }

                var unboundAnchors =
                    new List<OVRSpatialAnchor.UnboundAnchor>(1);

                var awaiter =
                    OVRSpatialAnchor.LoadUnboundAnchorsAsync(
                        new[] { _spatialAnchor.Uuid },
                        unboundAnchors
                    ).GetAwaiter();

                while (!awaiter.IsCompleted)
                    yield return null;

                var result = awaiter.GetResult();

                if (!result.Success)
                {
                    LogError($"LOAD FAILED: {result.Status}");
                    continue;
                }

                if (!_spatialAnchor.IsTracked)
                {
                    LogWarning("ANCHOR STILL NOT TRACKED");
                    continue;
                }

                Log("TRACKING RESTORED");
                yield break;
            }

            LogError("TRACKING RESTORE FAILED");
            EraseSpatialAnchor();
        }

        private void EraseSpatialAnchor()
        {
            if (_spatialAnchor == null)
                return;

            LogWarning("ERASING SPATIAL ANCHOR");

            _spatialAnchor.EraseAnchorAsync();

            DestroyImmediate(_spatialAnchor);

            _spatialAnchor = null;
        }

        // =========================================================
        // LOCK
        // =========================================================

        private void LockAll()
        {
            Log("LOCK ALL CALLED");

            if (_isLocked)
            {
                LogWarning("ALREADY LOCKED");
                return;
            }

            var liveBoxes = FindObjectsByType<BoundingBoxMarker>(
                FindObjectsInactive.Exclude,
                FindObjectsSortMode.None);

            Log($"FOUND LIVE BOXES: {liveBoxes.Length}");

            if (liveBoxes.Length == 0)
            {
                LogError("NO BOUNDING BOXES FOUND");
                return;
            }

            if (_agent == null)
            {
                LogError("AGENT NULL");
                return;
            }

            _savedSegmentEveryNFrames =
                _agent.segmentEveryNFrames;

            Log($"OLD segmentEveryNFrames: {_savedSegmentEveryNFrames}");

            _agent.segmentEveryNFrames = 0;

            Log("SEGMENTATION DISABLED");

            _isLocked = true;

            foreach (var marker in liveBoxes)
            {
                if (marker == null)
                {
                    LogError("MARKER NULL");
                    continue;
                }

                if (marker.IsLocked)
                {
                    LogWarning("MARKER ALREADY LOCKED");
                    continue;
                }

                var go = marker.gameObject;

                Log($"PROCESSING OBJECT: {go.name}");

                var pos = go.transform.position;
                var rot = go.transform.rotation;
                var scl = go.transform.localScale;

                Log($"POSITION: {pos}");

                if (lockedBoxPrefab == null)
                {
                    LogError("LOCKED PREFAB NULL");
                    continue;
                }

                var locked =
                    Instantiate(lockedBoxPrefab, pos, rot);

                if (locked == null)
                {
                    LogError("FAILED TO INSTANTIATE LOCKED PREFAB");
                    continue;
                }

                locked.transform.localScale = scl;

                var markerComp =
                    locked.GetComponent<BoundingBoxMarker>();

                if (markerComp == null)
                {
                    LogWarning("ADDING BoundingBoxMarker");
                    markerComp =
                        locked.AddComponent<BoundingBoxMarker>();
                }

                markerComp.IsLocked = true;

                _lockedVisuals.Add(locked);

                Log($"LOCKED OBJECT CREATED: {locked.name}");

                if (anchorIndicatorPrefab != null)
                {
                    var indicator =
                        Instantiate(anchorIndicatorPrefab,
                            pos,
                            Quaternion.identity);

                    _anchorIndicators.Add(indicator);

                    Log("ANCHOR INDICATOR CREATED");
                }
            }

            Log("LOCK COMPLETE");
        }

        // =========================================================
        // UNLOCK
        // =========================================================

        private void UnlockAll()
        {
            Log("UNLOCK ALL CALLED");

            if (!_isLocked)
            {
                LogWarning("NOT LOCKED");
                return;
            }

            foreach (var obj in _lockedVisuals)
            {
                if (obj != null)
                    Destroy(obj);
            }

            foreach (var obj in _anchorIndicators)
            {
                if (obj != null)
                    Destroy(obj);
            }

            _lockedVisuals.Clear();
            _anchorIndicators.Clear();

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
        // LOGGING
        // =========================================================

        private void Log(string msg)
        {
            _debugInfo = msg;

            if (spamLogs)
                Debug.Log($"[SegmentationAnchorController] {msg}");
        }

        private void LogWarning(string msg)
        {
            _debugInfo = msg;

            Debug.LogWarning(
                $"[SegmentationAnchorController] {msg}");
        }

        private void LogError(string msg)
        {
            _debugInfo = msg;

            Debug.LogError(
                $"[SegmentationAnchorController] {msg}");
        }

#endif
    }

    // =============================================================
    // BOUNDING BOX MARKER
    // =============================================================


}