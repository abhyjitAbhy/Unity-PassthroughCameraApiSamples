using System.Collections.Generic;
using UnityEngine;

namespace PassthroughCameraSamples.MultiObjectDetection
{
    /// <summary>
    /// Attach to every detected/locked bounding box.
    /// Call Lock() to spawn handles and enable manipulation.
    /// Call Unlock() to destroy handles and return to tracking mode.
    ///
    /// BoxInteractionManager drives all ray-cast input.
    /// This class only owns: handle lifetime, world-position sync, transform application.
    /// </summary>
    public class BoxManipulator : MonoBehaviour
    {
        // ── Inspector ────────────────────────────────────────────────────────
        [Header("Handle Visuals")]
        [Tooltip("Leave null — auto-generates a sphere primitive.")]
        [SerializeField] private GameObject handlePrefab;
        [SerializeField] private float handleRadius = 0.035f;
        public float HandleRadius { get => handleRadius; set => handleRadius = value; }

        [Header("Constraints")]
        [SerializeField] private bool lockY = true;
        [SerializeField] private float minSize = 0.05f;
        [SerializeField] private float maxSize = 4f;

        // ── State ────────────────────────────────────────────────────────────
        public bool IsLocked { get; private set; }

        // Handle map — only populated while locked
        private readonly Dictionary<HandleType, BoxHandle> _handles = new();

        // ── Public API ───────────────────────────────────────────────────────

        public void Lock()
        {
            if (IsLocked) return;
            IsLocked = true;
            SpawnHandles();
            BoxInteractionManager.Register(this);
        }

        public void Unlock()
        {
            if (!IsLocked) return;
            IsLocked = false;
            DestroyHandles();
            BoxInteractionManager.Unregister(this);
        }

        // Called every frame by BoxInteractionManager while locked
        public void SyncHandlePositions()
        {
            Vector3 wc = transform.position;
            Quaternion wr = transform.rotation;
            Vector3 ws = transform.lossyScale;

            Vector3 r = wr * Vector3.right * ws.x * 0.5f;
            Vector3 u = wr * Vector3.up * ws.y * 0.5f;
            Vector3 f = wr * Vector3.forward * ws.z * 0.5f;

            SetPos(HandleType.Center, wc);
            SetPos(HandleType.Top, wc + u);
            SetPos(HandleType.Bottom, wc - u);
            SetPos(HandleType.Front, wc + f);
            SetPos(HandleType.Back, wc - f);
            SetPos(HandleType.Right, wc + r);
            SetPos(HandleType.Left, wc - r);
        }

        public IEnumerable<BoxHandle> AllHandles() => _handles.Values;

        public BoxHandle GetHandle(HandleType t) =>
            _handles.TryGetValue(t, out var h) ? h : null;

        // ── Manipulation — called by BoxInteractionManager ───────────────────

        public void ApplyTranslation(Vector3 delta)
        {
            if (lockY) delta.y = 0f;
            transform.position += delta;
        }

        /// <param name="face">Which face handle is being dragged.</param>
        /// <param name="axisDelta">Signed world-space movement along the face normal.</param>
        /// <param name="anchorPos">Box center at grab start (so opposite face stays fixed).</param>
        /// <param name="anchorScale">Box scale at grab start.</param>
        public void ApplyFaceScale(HandleType face,
                                   float axisDelta,
                                   Vector3 anchorPos,
                                   Vector3 anchorScale)
        {
            int idx = AxisIndex(face);
            float current = GetAxis(anchorScale, idx);
            float newSize = Mathf.Clamp(current + axisDelta, minSize, maxSize);
            float diff = newSize - current;

            transform.localScale = SetAxis(anchorScale, idx, newSize);

            // Shift center so opposite face stays world-fixed
            Vector3 worldAxis = transform.rotation * FaceLocalAxis(face);
            transform.position = anchorPos + worldAxis * (diff * 0.5f);
        }

        public void ApplyTwoHandScale(float scaleFactor, Vector3 anchorScale)
        {
            transform.localScale = anchorScale * scaleFactor;
        }

        public void ApplyTwoHandRotation(float angleDelta, Quaternion anchorRot)
        {
            transform.rotation = Quaternion.AngleAxis(-angleDelta, Vector3.up) * anchorRot;
        }

        // ── Lifetime ─────────────────────────────────────────────────────────

        private void OnDestroy() => Unlock();

        // ── Handle Spawning ──────────────────────────────────────────────────

        private void SpawnHandles()
        {
            if (handlePrefab == null)
                handlePrefab = BuildDefaultPrefab();

            SpawnHandle(HandleType.Center);
            SpawnHandle(HandleType.Top);
            SpawnHandle(HandleType.Bottom);
            SpawnHandle(HandleType.Front);
            SpawnHandle(HandleType.Back);
            SpawnHandle(HandleType.Right);
            SpawnHandle(HandleType.Left);

            SyncHandlePositions(); // place immediately so no 1-frame gap
        }

        private void SpawnHandle(HandleType type)
        {
            var go = Instantiate(handlePrefab);
            go.name = $"Handle_{type}_{name}";
            go.transform.localScale = Vector3.one * handleRadius * 2f;
            go.SetActive(true);

            // SphereCollider for ray-sphere intersection in manager
            var col = go.GetComponent<SphereCollider>();
            if (col == null) col = go.AddComponent<SphereCollider>();
            col.radius = 0.5f; // local space — world radius = handleRadius after scale

            var handle = go.GetComponent<BoxHandle>();
            if (handle == null) handle = go.AddComponent<BoxHandle>();
            handle.Type = type;
            handle.Owner = this;

            _handles[type] = handle;
        }

        private void DestroyHandles()
        {
            foreach (var h in _handles.Values)
                if (h != null) Destroy(h.gameObject);
            _handles.Clear();
        }

        private void SetPos(HandleType t, Vector3 pos)
        {
            if (_handles.TryGetValue(t, out var h) && h != null)
                h.transform.position = pos;
        }

        // ── Axis Helpers ─────────────────────────────────────────────────────

        public static Vector3 FaceLocalAxis(HandleType t) => t switch
        {
            HandleType.Top => Vector3.up,
            HandleType.Bottom => -Vector3.up,
            HandleType.Front => Vector3.forward,
            HandleType.Back => -Vector3.forward,
            HandleType.Right => Vector3.right,
            HandleType.Left => -Vector3.right,
            _ => Vector3.zero
        };

        public static int AxisIndex(HandleType t) => t switch
        {
            HandleType.Right or HandleType.Left => 0,
            HandleType.Top or HandleType.Bottom => 1,
            HandleType.Front or HandleType.Back => 2,
            _ => -1
        };

        private static float GetAxis(Vector3 v, int i) => i switch
        {
            0 => v.x,
            1 => v.y,
            2 => v.z,
            _ => 0f
        };

        private static Vector3 SetAxis(Vector3 v, int i, float val)
        {
            switch (i) { case 0: v.x = val; break; case 1: v.y = val; break; case 2: v.z = val; break; }
            return v;
        }

        // ── Default Prefab ───────────────────────────────────────────────────

        private static GameObject BuildDefaultPrefab()
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.SetActive(false);
            DestroyImmediate(go.GetComponent<Collider>()); // we add SphereCollider manually
            go.GetComponent<Renderer>().material =
                new Material(Shader.Find("Sprites/Default"));
            return go;
        }
    }
}