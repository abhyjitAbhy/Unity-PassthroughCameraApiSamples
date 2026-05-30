using System.Collections.Generic;
using UnityEngine;

namespace PassthroughCameraSamples.MultiObjectDetection
{
    public class BoxInteractionManager : MonoBehaviour
    {
        // ── Singleton / Registry ─────────────────────────────────────────────
        private static BoxInteractionManager _instance;
        private static readonly List<BoxManipulator> _boxes = new();

        public static void Register(BoxManipulator box) { if (!_boxes.Contains(box)) _boxes.Add(box); }
        public static void Unregister(BoxManipulator box) { _boxes.Remove(box); }

        // ── Inspector ────────────────────────────────────────────────────────

        [Header("OVR References (auto-located if null)")]
        [SerializeField] private OVRHand leftHand;
        [SerializeField] private OVRHand rightHand;
        [SerializeField] private OVRSkeleton leftSkeleton;
        [SerializeField] private OVRSkeleton rightSkeleton;

        [Header("Ray / Hover")]
        [SerializeField] private float hoverRadius = 0.10f;
        [Range(0f, 1f)]
        [SerializeField] private float fingerDirectionWeight = 0.85f;
        [Range(0.05f, 0.95f)]
        [SerializeField] private float raySmoothing = 0.18f;

        [Header("Pinch")]
        [SerializeField] private float pinchThreshold = 0.70f;
        [SerializeField] private float pinchReleaseThreshold = 0.45f;

        [Header("Scale Sensitivity")]
        [SerializeField] private float scaleSensitivity = 1f;

        [Header("Two-Hand Uniform Scale")]
        [SerializeField] private float twoHandActivationRadius = 1.5f;

        [Header("Ray Visual")]
        [SerializeField] private float rayWidth = 0.006f;
        [SerializeField] private Color rayHoverColor = new(0.4f, 0.9f, 1f, 0.9f);
        [SerializeField] private Color rayGrabColor = new(0f, 1f, 0.5f, 1f);
        [SerializeField] private float reticleRadius = 0.018f;

        // ── Per-hand runtime ─────────────────────────────────────────────────

        private class HandRuntime
        {
            public OVRHand Hand;
            public OVRSkeleton Skeleton;

            // Pinch
            public bool IsPinching;
            public bool WasPinching;

            // Smoothed ray (for hover + visual only)
            public Vector3 SmoothedOrigin;
            public Vector3 SmoothedDir;
            public bool RayReady;

            // Grab
            public BoxHandle GrabbedHandle;

            // Frozen at grab-start — NEVER updated during drag
            public Vector3 GrabStartPos;       // box world position at grab
            public Vector3 GrabStartScale;     // box localScale at grab
            public Vector3 GrabWorldAxis;      // face world axis at grab
            public float GrabStartAxisPos;   // Dot(indexTip_atGrab, GrabWorldAxis)

            // Visuals
            public LineRenderer RayLine;
            public Transform Reticle;
        }

        private HandRuntime _lRT;
        private HandRuntime _rRT;

        // ── Two-hand state ────────────────────────────────────────────────────

        private bool _twoHandActive;
        private BoxManipulator _twoHandBox;
        private float _twoHandStartSpan;
        private float _twoHandStartAngle;
        private Vector3 _twoHandStartScale;
        private Quaternion _twoHandStartRot;

        // ── Unity ─────────────────────────────────────────────────────────────

        private void Awake()
        {
            if (_instance != null && _instance != this) { Destroy(gameObject); return; }
            _instance = this;
            _lRT = BuildRuntime("Left");
            _rRT = BuildRuntime("Right");
        }

        private void Start()
        {
            if (leftHand == null || rightHand == null || leftSkeleton == null || rightSkeleton == null)
                AutoLocateHands();
            _lRT.Hand = leftHand; _lRT.Skeleton = leftSkeleton;
            _rRT.Hand = rightHand; _rRT.Skeleton = rightSkeleton;
        }

        private void OnDestroy()
        {
            _boxes.Clear();
            _instance = null;
        }

        private void Update()
        {
            if (_lRT.Hand == null || _rRT.Hand == null) return;

            foreach (var box in _boxes)
                if (box != null) box.SyncHandlePositions();

            UpdateSmoothedRay(_lRT);
            UpdateSmoothedRay(_rRT);
            UpdatePinch(_lRT);
            UpdatePinch(_rRT);

            // Two-hand only when neither hand is in a face-scale grab
            if (_lRT.GrabbedHandle == null && _rRT.GrabbedHandle == null)
                ProcessTwoHand();
            else if (_twoHandActive)
                ExitTwoHand();

            if (!_twoHandActive)
            {
                ProcessSingleHand(_lRT);
                ProcessSingleHand(_rRT);
            }

            UpdateRayVisuals(_lRT);
            UpdateRayVisuals(_rRT);
        }

        // ── Smoothed Ray (hover + visual only) ───────────────────────────────

        private void UpdateSmoothedRay(HandRuntime rt)
        {
            if (!TryBuildRawRay(rt, out Vector3 rawOrigin, out Vector3 rawDir)) return;

            if (!rt.RayReady)
            {
                rt.SmoothedOrigin = rawOrigin;
                rt.SmoothedDir = rawDir;
                rt.RayReady = true;
                return;
            }

            float k = Mathf.Clamp01(raySmoothing);
            rt.SmoothedOrigin = Vector3.Lerp(rawOrigin, rt.SmoothedOrigin, k);
            rt.SmoothedDir = Vector3.Slerp(rawDir, rt.SmoothedDir, k);
        }

        private bool TryBuildRawRay(HandRuntime rt, out Vector3 origin, out Vector3 dir)
        {
            origin = Vector3.zero; dir = Vector3.forward;
            if (rt.Skeleton == null) return false;

            // Thumb ray: Hand_Thumb2 (distal phalanx) as origin, ThumbTip as direction.
            Transform thumb2 = FindBone(rt.Skeleton, OVRSkeleton.BoneId.Hand_Thumb2);
            Transform thumbTip = FindBone(rt.Skeleton, OVRSkeleton.BoneId.Hand_ThumbTip);
            if (thumb2 == null || thumbTip == null) return false;

            origin = thumb2.position;

            Vector3 fv = thumbTip.position - thumb2.position;
            fv = fv.sqrMagnitude > 0.00001f ? fv.normalized : thumb2.forward;

            Vector3 headFwd = Camera.main != null ? Camera.main.transform.forward : Vector3.forward;
            dir = Vector3.Slerp(headFwd, fv, fingerDirectionWeight).normalized;
            return true;
        }

        /// <summary>
        /// Finds a bone transform by BoneId by iterating the list.
        /// Safe across SDK versions where list order may not match enum order.
        /// </summary>
        private static Transform FindBone(OVRSkeleton skeleton, OVRSkeleton.BoneId id)
        {
            var bones = skeleton.Bones;
            if (bones == null) return null;
            for (int i = 0; i < bones.Count; i++)
                if (bones[i].Id == id) return bones[i].Transform;
            return null;
        }

        private bool GetSmoothedRay(HandRuntime rt, out Ray ray)
        {
            ray = default;
            if (!rt.RayReady) return false;
            ray = new Ray(rt.SmoothedOrigin, rt.SmoothedDir);
            return true;
        }

        // ── Pinch ─────────────────────────────────────────────────────────────

        private void UpdatePinch(HandRuntime rt)
        {
            rt.WasPinching = rt.IsPinching;
            if (rt.Hand == null || !rt.Hand.IsTracked) { rt.IsPinching = false; return; }

            float s = rt.Hand.GetFingerPinchStrength(OVRHand.HandFinger.Index);
            if (!rt.IsPinching && s >= pinchThreshold) rt.IsPinching = true;
            else if (rt.IsPinching && s < pinchReleaseThreshold) rt.IsPinching = false;
        }

        // ── Single-Hand Face Scale ────────────────────────────────────────────
        //
        // CORE MECHANIC — no drag planes, no ray intersection math:
        //
        //   On grab: record index tip's projection onto the face world axis.
        //   Each frame: re-project current index tip onto same axis.
        //   axisDelta = currentProjection - grabStartProjection
        //   Call ApplyFaceScale(face, axisDelta, frozenPos, frozenScale)
        //
        //   This works because:
        //   - The face axis is a 1D line in world space.
        //   - Dot(tipPosition, faceAxis) gives signed position along that line.
        //   - The difference tells us exactly how much the hand moved along the axis.
        //   - Drag direction doesn't matter — only axial component is extracted.

        private void ProcessSingleHand(HandRuntime rt)
        {
            if (rt.Hand == null || !rt.Hand.IsTracked) return;

            bool justPinched = rt.IsPinching && !rt.WasPinching;
            bool justReleased = !rt.IsPinching && rt.WasPinching;

            if (justReleased && rt.GrabbedHandle != null)
            {
                rt.GrabbedHandle.SetState(HandleState.Idle);
                rt.GrabbedHandle = null;
            }

            if (justPinched && rt.GrabbedHandle == null && !_twoHandActive)
            {
                if (!GetSmoothedRay(rt, out Ray ray)) return;
                var hit = RayCastHandles(ray);
                if (hit == null || hit.Type == HandleType.Center) return;

                if (!TryGetIndexTipWorld(rt, out Vector3 tipAtGrab)) return;

                // Compute face world axis — frozen for lifetime of this grab
                Vector3 faceAxis = hit.Owner.transform.rotation *
                                   BoxManipulator.FaceLocalAxis(hit.Type);

                rt.GrabbedHandle = hit;
                rt.GrabStartPos = hit.Owner.transform.position;
                rt.GrabStartScale = hit.Owner.transform.localScale;
                rt.GrabWorldAxis = faceAxis;
                rt.GrabStartAxisPos = Vector3.Dot(tipAtGrab, faceAxis);

                hit.SetState(HandleState.Grabbed);
            }

            if (rt.IsPinching && rt.GrabbedHandle != null)
            {
                if (!TryGetIndexTipWorld(rt, out Vector3 tipNow)) return;

                float currentAxisPos = Vector3.Dot(tipNow, rt.GrabWorldAxis);
                float axisDelta = (currentAxisPos - rt.GrabStartAxisPos) * scaleSensitivity;

                rt.GrabbedHandle.Owner.ApplyFaceScale(
                    rt.GrabbedHandle.Type,
                    axisDelta,
                    rt.GrabStartPos,
                    rt.GrabStartScale
                );
            }
        }

        // ── Two-Hand Uniform Scale + Rotate ───────────────────────────────────

        private void ProcessTwoHand()
        {
            bool lP = _lRT.IsPinching && _lRT.Hand != null && _lRT.Hand.IsTracked;
            bool rP = _rRT.IsPinching && _rRT.Hand != null && _rRT.Hand.IsTracked;

            if (!lP || !rP) { if (_twoHandActive) ExitTwoHand(); return; }

            if (!TryGetIndexTipWorld(_lRT, out Vector3 lTip)) return;
            if (!TryGetIndexTipWorld(_rRT, out Vector3 rTip)) return;

            BoxManipulator nearBox = FindNearestBoxToBothTips(lTip, rTip);
            if (nearBox == null) { if (_twoHandActive) ExitTwoHand(); return; }

            if (!_twoHandActive)
            {
                _twoHandActive = true;
                _twoHandBox = nearBox;
                _twoHandStartSpan = Vector3.Distance(lTip, rTip);
                _twoHandStartScale = nearBox.transform.localScale;
                _twoHandStartRot = nearBox.transform.rotation;
                Vector3 sv = rTip - lTip; sv.y = 0f;
                _twoHandStartAngle = Mathf.Atan2(sv.z, sv.x) * Mathf.Rad2Deg;
                return;
            }

            float span = Vector3.Distance(lTip, rTip);
            if (_twoHandStartSpan > 0.001f)
            {
                float factor = Mathf.Clamp(span / _twoHandStartSpan, 0.1f, 5f);
                _twoHandBox.ApplyTwoHandScale(factor, _twoHandStartScale);
            }

            Vector3 cv = rTip - lTip; cv.y = 0f;
            if (cv.sqrMagnitude > 0.0001f)
            {
                float angle = Mathf.Atan2(cv.z, cv.x) * Mathf.Rad2Deg;
                _twoHandBox.ApplyTwoHandRotation(
                    Mathf.DeltaAngle(_twoHandStartAngle, angle),
                    _twoHandStartRot);
            }
        }

        private void ExitTwoHand()
        {
            _twoHandActive = false;
            _twoHandBox = null;
        }

        private BoxManipulator FindNearestBoxToBothTips(Vector3 lTip, Vector3 rTip)
        {
            Vector3 mid = (lTip + rTip) * 0.5f;
            float r2 = twoHandActivationRadius * twoHandActivationRadius;
            BoxManipulator best = null;
            float bestDist = float.MaxValue;

            foreach (var box in _boxes)
            {
                if (box == null) continue;
                Vector3 c = box.transform.position;
                if ((c - lTip).sqrMagnitude > r2) continue;
                if ((c - rTip).sqrMagnitude > r2) continue;
                float d = (c - mid).sqrMagnitude;
                if (d < bestDist) { bestDist = d; best = box; }
            }
            return best;
        }

        // ── Ray-Sphere Hit ────────────────────────────────────────────────────

        private BoxHandle RayCastHandles(Ray ray)
        {
            BoxHandle best = null;
            float bestDist = float.MaxValue;

            foreach (var box in _boxes)
            {
                if (box == null) continue;
                foreach (var handle in box.AllHandles())
                {
                    if (handle == null || handle.Type == HandleType.Center) continue;
                    if (RaySphere(ray, handle.transform.position, hoverRadius, out float d) && d < bestDist)
                    { bestDist = d; best = handle; }
                }
            }
            return best;
        }

        private static bool RaySphere(Ray ray, Vector3 center, float radius, out float t)
        {
            t = 0f;
            Vector3 oc = ray.origin - center;
            float b = Vector3.Dot(oc, ray.direction);
            float c = oc.sqrMagnitude - radius * radius;
            float disc = b * b - c;
            if (disc < 0f) return false;
            float sq = Mathf.Sqrt(disc);
            float t0 = -b - sq, t1 = -b + sq;
            if (t0 > 0f) { t = t0; return true; }
            if (t1 > 0f) { t = t1; return true; }
            return false;
        }

        // ── Index Tip World Position ──────────────────────────────────────────

        private static bool TryGetIndexTipWorld(HandRuntime rt, out Vector3 world)
        {
            world = Vector3.zero;
            if (rt.Skeleton == null) return false;
            Transform tip = FindBone(rt.Skeleton, OVRSkeleton.BoneId.Hand_ThumbTip);
            if (tip == null) return false;
            world = tip.position;
            return true;
        }

        // ── Ray Visuals ───────────────────────────────────────────────────────

        private void UpdateRayVisuals(HandRuntime rt)
        {
            if (_boxes.Count == 0 || rt.Hand == null || !rt.Hand.IsTracked || !rt.RayReady)
            { HideRay(rt); return; }

            if (rt.GrabbedHandle != null)
            {
                ShowRay(rt, rt.SmoothedOrigin, rt.GrabbedHandle.transform.position, rayGrabColor);
                PlaceReticle(rt, rt.GrabbedHandle.transform.position);
                return;
            }

            if (_twoHandActive) { HideRay(rt); return; }

            if (!GetSmoothedRay(rt, out Ray ray)) { HideRay(rt); return; }

            var hovered = RayCastHandles(ray);
            if (hovered != null)
            {
                ShowRay(rt, ray.origin, hovered.transform.position, rayHoverColor);
                PlaceReticle(rt, hovered.transform.position);
                hovered.SetState(HandleState.Hovered);
            }
            else
            {
                HideRay(rt);
                foreach (var box in _boxes)
                    if (box != null)
                        foreach (var h in box.AllHandles())
                            if (h != null && h.State == HandleState.Hovered)
                                h.SetState(HandleState.Idle);
            }
        }

        private void ShowRay(HandRuntime rt, Vector3 start, Vector3 end, Color c)
        {
            rt.RayLine.enabled = true;
            rt.RayLine.startColor = c;
            rt.RayLine.endColor = new Color(c.r, c.g, c.b, 0f);
            rt.RayLine.SetPosition(0, start);
            rt.RayLine.SetPosition(1, end);
        }

        private void HideRay(HandRuntime rt)
        {
            rt.RayLine.enabled = false;
            rt.Reticle.gameObject.SetActive(false);
        }

        private void PlaceReticle(HandRuntime rt, Vector3 pos)
        {
            rt.Reticle.position = pos;
            rt.Reticle.gameObject.SetActive(true);
        }

        // ── Auto-Locate ───────────────────────────────────────────────────────

        private void AutoLocateHands()
        {
            var hands = FindObjectsByType<OVRHand>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            foreach (var h in hands)
            {
                var t = (OVRHand.Hand)h.GetHand();
                if (t == OVRHand.Hand.HandLeft && leftHand == null) leftHand = h;
                if (t == OVRHand.Hand.HandRight && rightHand == null) rightHand = h;
            }
            var skels = FindObjectsByType<OVRSkeleton>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            foreach (var s in skels)
            {
                if (s.GetSkeletonType() == OVRSkeleton.SkeletonType.HandLeft && leftSkeleton == null) leftSkeleton = s;
                if (s.GetSkeletonType() == OVRSkeleton.SkeletonType.HandRight && rightSkeleton == null) rightSkeleton = s;
            }
            _lRT.Hand = leftHand; _lRT.Skeleton = leftSkeleton;
            _rRT.Hand = rightHand; _rRT.Skeleton = rightSkeleton;
        }

        // ── Visual Builder ────────────────────────────────────────────────────

        private HandRuntime BuildRuntime(string side)
        {
            var rt = new HandRuntime();
            var rayGo = new GameObject($"HandRay_{side}");
            rayGo.transform.SetParent(transform);
            var lr = rayGo.AddComponent<LineRenderer>();
            lr.positionCount = 2;
            lr.useWorldSpace = true;
            lr.startWidth = rayWidth;
            lr.endWidth = rayWidth * 0.3f;
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            lr.receiveShadows = false;
            lr.material = new Material(Shader.Find("Sprites/Default"));
            lr.enabled = false;
            rt.RayLine = lr;

            var rGo = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            rGo.name = $"Reticle_{side}";
            rGo.transform.SetParent(transform);
            rGo.transform.localScale = Vector3.one * reticleRadius * 2f;
            Destroy(rGo.GetComponent<Collider>());
            rGo.GetComponent<Renderer>().material = new Material(Shader.Find("Sprites/Default"));
            rGo.SetActive(false);
            rt.Reticle = rGo.transform;

            return rt;
        }
    }
}