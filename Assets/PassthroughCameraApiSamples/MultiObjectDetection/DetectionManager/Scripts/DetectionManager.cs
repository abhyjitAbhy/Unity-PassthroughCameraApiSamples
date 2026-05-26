// Copyright (c) Meta Platforms, Inc. and affiliates.

using System.Collections;
using System.Collections.Generic;
using Meta.XR;
using Meta.XR.Samples;
using UnityEngine;
using UnityEngine.Events;

namespace PassthroughCameraSamples.MultiObjectDetection
{
    [MetaCodeSample("PassthroughCameraApiSamples-MultiObjectDetection")]
    public class DetectionManager : MonoBehaviour
    {
        [SerializeField] private PassthroughCameraAccess m_cameraAccess;

        [Header("Placement configuration")]
        [SerializeField] private DetectionSpawnMarkerAnim m_spawnMarker;
        [SerializeField] private SentisInferenceUiManager m_uiInference;

        [SerializeField] private Box3DManager m_box3DManager;
        [SerializeField] private GameObject Gameobject_box3DManager;

        [Space(10)]
        public UnityEvent<int> OnObjectsIdentified;

        private readonly List<DetectionSpawnMarkerAnim> m_spawnedEntities = new();
        private bool m_isStarted;
        internal OVRSpatialAnchor m_spatialAnchor;
        private bool m_isHeadsetTracking;

        // Tracks whether the right-hand pinch is currently held so we only
        // fire ToggleGlobalFreeze once per pinch gesture (rising-edge only).
        private bool m_rightPinchWasDown;

        private void Awake()
        {
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
        private void Start()
        {
            m_box3DManager = Gameobject_box3DManager.GetComponent<Box3DManager>();
        }
        private void OnTrackingLost() => m_isHeadsetTracking = false;
        private void OnTrackingAcquired() => m_isHeadsetTracking = true;

        private void Update()
        {
            if (!m_isStarted)
            {
                if (m_cameraAccess.IsPlaying)
                    m_isStarted = true;
                return;
            }

            // ── Button A / left pinch — spawn markers + lock matching boxes ──
            if (InputManager.IsButtonADownOrPinchStarted())
                SpawnCurrentDetectedObjects();

            // ── Button B / middle-finger pinch — clear markers + unlock boxes ─
            if (InputManager.IsButtonBDownOrMiddleFingerPinchStarted())
                CleanMarkers();

            // ── Right-hand index pinch — toggle global freeze on all 3D boxes ─
            //
            // Rising-edge detection: only fires once when the pinch starts,
            // not every frame while it is held.
            //
            // Controller: right index trigger
            // Hand tracking: right hand index-tip pinch (OVRHand strength > 0.9)
            bool rightPinchDown = IsRightHandPinchDown();
            if (rightPinchDown && !m_rightPinchWasDown)
                m_box3DManager?.ToggleGlobalFreeze();
            m_rightPinchWasDown = rightPinchDown;
        }

        // ── Right-pinch detection ────────────────────────────────────────────

        /// <summary>
        /// Returns true while the right-hand index pinch (or right controller
        /// index trigger) is held.  The caller does rising-edge detection so
        /// ToggleGlobalFreeze fires only once per gesture.
        /// </summary>
        private static bool IsRightHandPinchDown()
        {
            // Controller mode: right index trigger past halfway
            if (OVRInput.Get(OVRInput.RawAxis1D.RIndexTrigger) > 0.5f)
                return true;

            // Hand-tracking mode: right index-tip pinch strength
            // OVRPlugin.GetHandState reports pinch strength in [0,1];
            // we use OVRInput's high-level button which maps to the same thing.
            if (OVRInput.Get(OVRInput.Button.PrimaryHandTrigger, OVRInput.Controller.RHand))
                return true;

            return false;
        }

        // ── Spatial anchor management ────────────────────────────────────────

        private IEnumerator UpdateSpatialAnchor()
        {
            while (true)
            {
                yield return null;
                if (m_spatialAnchor == null)
                {
                    yield return CreateSpatialAnchorAndSave();
                    if (m_spatialAnchor == null) continue;
                }
                if (!m_spatialAnchor.IsTracked)
                    yield return RestoreSpatialAnchorTracking();
            }

            IEnumerator CreateSpatialAnchorAndSave()
            {
                m_spatialAnchor = m_uiInference.ContentParent.gameObject.AddComponent<OVRSpatialAnchor>();

                while (true)
                {
                    if (m_spatialAnchor == null) yield break;
                    if (m_spatialAnchor.Localized) break;
                    yield return null;
                }

                var awaiter = m_spatialAnchor.SaveAnchorAsync().GetAwaiter();
                while (!awaiter.IsCompleted) yield return null;

                var result = awaiter.GetResult();
                if (!result.Success)
                {
                    LogSpatialAnchor($"SaveAnchorAsync() failed {result}", LogType.Error);
                    EraseSpatialAnchor();
                    yield break;
                }
                LogSpatialAnchor("created");
            }

            IEnumerator RestoreSpatialAnchorTracking()
            {
                LogSpatialAnchor("tracking was lost, restoring...");
                const int numRetries = 20;
                for (int i = 0; i < numRetries; i++)
                {
                    yield return new WaitForSeconds(1f);
                    if (!m_isHeadsetTracking)
                    {
                        LogSpatialAnchor($"{nameof(m_isHeadsetTracking)} is false, retrying ({i})");
                        continue;
                    }

                    var unboundAnchors = new List<OVRSpatialAnchor.UnboundAnchor>(1);
                    var awaiter = OVRSpatialAnchor.LoadUnboundAnchorsAsync(
                        new[] { m_spatialAnchor.Uuid }, unboundAnchors).GetAwaiter();
                    while (!awaiter.IsCompleted) yield return null;

                    var loadResult = awaiter.GetResult();
                    if (!loadResult.Success)
                    {
                        LogSpatialAnchor($"LoadUnboundAnchorsAsync() failed {loadResult.Status}, retrying ({i})", LogType.Error);
                        continue;
                    }
                    if (unboundAnchors.Count != 0)
                    {
                        LogSpatialAnchor($"LoadUnboundAnchorsAsync() unexpected count:{unboundAnchors.Count}, retrying ({i})", LogType.Error);
                        continue;
                    }

                    yield return null;
                    if (!m_spatialAnchor.IsTracked)
                    {
                        LogSpatialAnchor($"tracking is not restored, retrying ({i})");
                        continue;
                    }
                    LogSpatialAnchor("tracking was restored successfully");
                    yield break;
                }
                LogSpatialAnchor($"tracking restoration failed after {numRetries} retries", LogType.Warning);
                EraseSpatialAnchor();
            }
        }

        private void EraseSpatialAnchor()
        {
            if (m_spatialAnchor != null)
            {
                LogSpatialAnchor("EraseSpatialAnchor");
                m_spatialAnchor.EraseAnchorAsync();
                DestroyImmediate(m_spatialAnchor);
                m_spatialAnchor = null;
                CleanMarkers();
                m_uiInference.ClearAnnotations();
            }
        }

        private void CleanMarkers()
        {
            LogSpatialAnchor("CleanMarkers");
            foreach (var e in m_spawnedEntities)
                Destroy(e.gameObject);
            m_spawnedEntities.Clear();
            OnObjectsIdentified?.Invoke(-1);

            // Unlock all 3D boxes (Button B always fully unlocks)
            m_box3DManager?.UnlockAll();
        }

        private static void LogSpatialAnchor(string message, LogType logType = LogType.Log)
        {
            Debug.unityLogger.Log(logType, $"{nameof(OVRSpatialAnchor)}: {message}");
        }

        private void SpawnCurrentDetectedObjects()
        {
            var newCount = 0;
            foreach (var box in m_uiInference.m_boxDrawn)
            {
                if (!HasExistingMarkerInBoundingBox(box))
                {
                    LogSpatialAnchor($"spawn marker {box.ClassName}");

                    var marker = Instantiate(
                        m_spawnMarker,
                        box.BoxRectTransform.position,
                        box.BoxRectTransform.rotation,
                        m_uiInference.ContentParent);

                    marker.GetComponent<DetectionSpawnMarkerAnim>().SetYoloClassName(box.ClassName);
                    m_spawnedEntities.Add(marker);
                    newCount++;

                    // Lock the specific 3D box at this world position
                    m_box3DManager?.LockBox(box.ClassId, box.BoxRectTransform.position);
                }
            }
            OnObjectsIdentified?.Invoke(newCount);

            bool HasExistingMarkerInBoundingBox(SentisInferenceUiManager.BoundingBoxData box)
            {
                foreach (var marker in m_spawnedEntities)
                {
                    if (marker.GetYoloClassName() == box.ClassName)
                    {
                        var markerWorldPos = marker.transform.position;
                        Vector2 localPos = box.BoxRectTransform.InverseTransformPoint(markerWorldPos);
                        var sizeDelta = box.BoxRectTransform.sizeDelta;
                        var currentBox = new Rect(
                            -sizeDelta.x * 0.5f,
                            -sizeDelta.y * 0.5f,
                             sizeDelta.x,
                             sizeDelta.y);

                        if (currentBox.Contains(localPos))
                            return true;
                    }
                }
                return false;
            }
        }
    }
}