// Copyright (c) Meta Platforms, Inc. and affiliates.

using TMPro;
using UnityEngine;

namespace PassthroughCameraSamples.MultiObjectDetection
{
    /// <summary>
    /// Box3DVisualizer.cs
    ///
    /// Changes in this version:
    ///
    ///   5. REAL-WORLD DIMENSIONS  — when the box is locked via SetLocked(true),
    ///      the caller passes the frozen world-space size (metres). The visualizer
    ///      formats it as "W × H × D cm" and shows it on a second label line
    ///      (m_dimensionText) that sits just below the class-name label.
    ///      While the box is live (not locked) the dimension text is hidden so the
    ///      display is uncluttered during tracking.
    ///
    ///   Previous stability changes (1-4) are unchanged:
    ///   1. Pool snap fix  2. Live 6-DoF tracking  3. Label billboard
    ///   4. SetLocked() freeze + blue tint
    /// </summary>
    [RequireComponent(typeof(LineRenderer))]
    public class Box3DVisualizer : MonoBehaviour
    {
        [Header("Appearance")]
        [SerializeField] private Color m_color = new Color(0f, 1f, 0.45f, 1f);
        [SerializeField] private Color m_colorLocked = new Color(0f, 0.6f, 1f, 1f);
        [SerializeField] private float m_lineWidth = 0.003f;

        [Header("Labels")]
        [SerializeField] private TextMeshProUGUI m_labelText;

        /// <summary>
        /// Optional second TMP label for the dimension readout (e.g. "42 × 31 × 28 cm").
        /// Wire up a child Canvas/TMP object in the prefab.
        /// If left null, the dimension is appended as a second line on m_labelText instead.
        /// </summary>
        [SerializeField] private TextMeshProUGUI m_dimensionText;

        [Header("Smoothing")]
        [Tooltip("Visual lerp speed. Higher = snappier. 0 = instant snap.")]
        [SerializeField, Range(0f, 20f)] private float m_lerpSpeed = 8f;

        // ── Unit cube corners & edges ─────────────────────────────────────────
        private static readonly Vector3[] Corners =
        {
            new(-0.5f, -0.5f, -0.5f), // 0
            new( 0.5f, -0.5f, -0.5f), // 1
            new( 0.5f,  0.5f, -0.5f), // 2
            new(-0.5f,  0.5f, -0.5f), // 3
            new(-0.5f, -0.5f,  0.5f), // 4
            new( 0.5f, -0.5f,  0.5f), // 5
            new( 0.5f,  0.5f,  0.5f), // 6
            new(-0.5f,  0.5f,  0.5f), // 7
        };

        private static readonly int[] Edges =
        {
            0,1, 1,2, 2,3, 3,0,   // back  face
            4,5, 5,6, 6,7, 7,4,   // front face
            0,4, 1,5, 2,6, 3,7    // pillars
        };

        // ── State ─────────────────────────────────────────────────────────────
        private LineRenderer _lr;
        private Vector3 _targetCenter;
        private Quaternion _targetRotation;
        private Vector3 _targetSize;
        private bool _initialized;
        private bool _locked;
        private Camera _mainCam;

        // Cached class-name label (set by SetBox, displayed with or without dims)
        private string _currentLabel = string.Empty;

        // ── Unity Lifecycle ───────────────────────────────────────────────────

        private void Awake()
        {
            _lr = GetComponent<LineRenderer>();
            _lr.useWorldSpace = true;
            _lr.loop = false;
            _lr.positionCount = Edges.Length;
            _lr.widthMultiplier = m_lineWidth;
            _lr.material = new Material(Shader.Find("Sprites/Default"));
            _lr.startColor = _lr.endColor = m_color;

            _mainCam = Camera.main;

            // Hide dimension display until an object is locked
            if (m_dimensionText != null)
                m_dimensionText.gameObject.SetActive(false);
        }

        private void Update()
        {
            if (!_initialized) return;

            // ── Smooth tracking ───────────────────────────────────────────────
            if (m_lerpSpeed > 0f)
            {
                float t = Time.deltaTime * m_lerpSpeed;
                transform.position = Vector3.Lerp(transform.position, _targetCenter, t);
                transform.rotation = Quaternion.Slerp(transform.rotation, _targetRotation, t);
                transform.localScale = Vector3.Lerp(transform.localScale, _targetSize, t);
            }
            else
            {
                transform.position = _targetCenter;
                transform.rotation = _targetRotation;
                transform.localScale = _targetSize;
            }

            RebuildLines(transform.position, transform.rotation, transform.localScale);

            // ── Label billboard ───────────────────────────────────────────────
            BillboardLabel(m_labelText, offsetFactor: 0.5f, extraOffset: 0.06f);
            BillboardLabel(m_dimensionText, offsetFactor: 0.5f, extraOffset: -0.06f);
        }

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>
        /// Called every confirmed-detection frame with the EMA-smoothed pose from
        /// Box3DManager. Snaps on first call after (re)activation.
        /// </summary>
        public void SetBox(Vector3 center, Quaternion rotation, Vector3 size, string label)
        {
            if (_locked) return;

            if (!_initialized)
            {
                transform.position = center;
                transform.rotation = rotation;
                transform.localScale = size;
                _initialized = true;
            }

            _targetCenter = center;
            _targetRotation = rotation;
            _targetSize = size;

            _currentLabel = label;
            if (m_labelText != null)
                m_labelText.text = label;
        }

        /// <summary>
        /// Freeze / unfreeze.
        /// When locking, pass the EMA-smoothed world-space size (metres) so the
        /// visualizer can compute and display the real-world dimensions.
        /// Locked = stops updates, turns box blue, shows dimension readout.
        /// Unlocked = resumes live 6-DoF tracking, hides dimension readout.
        /// </summary>
        public void SetLocked(bool locked, Vector3 frozenSizeMetres = default)
        {
            _locked = locked;

            if (locked)
            {
                // Freeze targets at current rendered pose
                _targetCenter = transform.position;
                _targetRotation = transform.rotation;
                _targetSize = transform.localScale;

                // Show dimension readout
                ShowDimensions(frozenSizeMetres);
            }
            else
            {
                // Hide dimension readout and restore plain label
                HideDimensions();
            }

            Color c = locked ? m_colorLocked : m_color;
            _lr.startColor = _lr.endColor = c;
            if (m_labelText != null) m_labelText.color = c;
            if (m_dimensionText != null) m_dimensionText.color = c;
        }

        /// <summary>
        /// Called by Box3DManager when this visualizer is returned to the pool.
        /// </summary>
        public void ResetForReuse()
        {
            _initialized = false;
            _locked = false;
            _currentLabel = string.Empty;

            _lr.startColor = _lr.endColor = m_color;

            if (m_labelText != null)
            {
                m_labelText.text = string.Empty;
                m_labelText.color = m_color;
            }

            HideDimensions();
        }

        // ── Dimension display ─────────────────────────────────────────────────

        /// <summary>
        /// Formats size (metres) into a human-readable string and shows it.
        /// Uses cm when all dimensions are under 1 m, otherwise metres.
        /// Layout:
        ///   - If m_dimensionText is wired:  class label on m_labelText,
        ///                                   dimensions on m_dimensionText.
        ///   - Otherwise: append dimensions as a second line on m_labelText.
        /// </summary>
        private void ShowDimensions(Vector3 sizeM)
        {
            // Guard: zero / unset size means caller did not supply a frozen size.
            // Fall back gracefully using the transform's current scale.
            if (sizeM == default || sizeM == Vector3.zero)
                sizeM = transform.localScale;

            string dimString = FormatDimensions(sizeM);

            if (m_dimensionText != null)
            {
                m_dimensionText.gameObject.SetActive(true);
                m_dimensionText.text = dimString;

                // Restore class label to name-only on m_labelText
                if (m_labelText != null)
                    m_labelText.text = _currentLabel;
            }
            else if (m_labelText != null)
            {
                // No separate dimension label: second line on the class label
                m_labelText.text = $"{_currentLabel}\n<size=80%>{dimString}</size>";
            }
        }

        private void HideDimensions()
        {
            if (m_dimensionText != null)
                m_dimensionText.gameObject.SetActive(false);

            // Restore plain class label if it was overwritten
            if (m_labelText != null)
                m_labelText.text = _currentLabel;
        }

        /// <summary>
        /// Returns a compact "W × H × D cm" string (or metres if any axis ≥ 10 m).
        /// Axes are sorted Width (X), Height (Y), Depth (Z) in PCA-frame order,
        /// which after gravity-alignment maps to: widest horizontal, vertical, depth.
        /// </summary>
        private static string FormatDimensions(Vector3 sizeM)
        {
            // Convert to centimetres for typical furniture / object scale
            float w = sizeM.x * 100f;
            float h = sizeM.y * 100f;
            float d = sizeM.z * 100f;

            // If the object is huge (room-scale) stay in metres
            if (Mathf.Max(w, h, d) > 999f)
                return $"{sizeM.x:F2} × {sizeM.y:F2} × {sizeM.z:F2} m";

            // Round to nearest cm for a clean readout
            return $"{Mathf.RoundToInt(w)} × {Mathf.RoundToInt(h)} × {Mathf.RoundToInt(d)} cm";
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private void BillboardLabel(TextMeshProUGUI label, float offsetFactor, float extraOffset)
        {
            if (label == null || !label.gameObject.activeSelf || _mainCam == null) return;

            Vector3 topOffset = transform.rotation *
                (Vector3.up * (transform.localScale.y * offsetFactor + extraOffset));
            label.transform.position = transform.position + topOffset;

            Vector3 toCamera = _mainCam.transform.position - label.transform.position;
            if (toCamera.sqrMagnitude > 0.0001f)
                label.transform.rotation = Quaternion.LookRotation(-toCamera.normalized);
        }

        private void RebuildLines(Vector3 center, Quaternion rotation, Vector3 size)
        {
            for (int i = 0; i < Edges.Length; i++)
            {
                Vector3 local = Vector3.Scale(Corners[Edges[i]], size);
                _lr.SetPosition(i, center + rotation * local);
            }
        }
    }
}