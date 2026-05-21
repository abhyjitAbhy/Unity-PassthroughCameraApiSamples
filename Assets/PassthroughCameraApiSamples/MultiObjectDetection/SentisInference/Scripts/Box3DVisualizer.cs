// Copyright (c) Meta Platforms, Inc. and affiliates.

using TMPro;
using UnityEngine;

namespace PassthroughCameraSamples.MultiObjectDetection
{
    [RequireComponent(typeof(LineRenderer))]
    public class Box3DVisualizer : MonoBehaviour
    {
        [Header("Appearance")]
        // KEPT original field name m_color so existing prefab serialization is not broken
        [SerializeField] private Color m_color = new Color(0f, 1f, 0.45f, 1f);
        [SerializeField] private Color m_colorLocked = new Color(0f, 0.6f, 1f, 1f);
        [SerializeField] private float m_lineWidth = 0.003f;

        [Header("Label")]
        [SerializeField] private TextMeshProUGUI m_labelText;

        [Header("Smoothing")]
        [Tooltip("Higher = snappier tracking, lower = smoother. 0 = no smoothing.")]
        [SerializeField, Range(0f, 20f)] private float m_lerpSpeed = 10f;

        private static readonly Vector3[] Corners = {
            new(-0.5f, -0.5f, -0.5f), // 0
            new( 0.5f, -0.5f, -0.5f), // 1
            new( 0.5f,  0.5f, -0.5f), // 2
            new(-0.5f,  0.5f, -0.5f), // 3
            new(-0.5f, -0.5f,  0.5f), // 4
            new( 0.5f, -0.5f,  0.5f), // 5
            new( 0.5f,  0.5f,  0.5f), // 6
            new(-0.5f,  0.5f,  0.5f), // 7
        };

        private static readonly int[] Edges = {
            0,1, 1,2, 2,3, 3,0,   // back face
            4,5, 5,6, 6,7, 7,4,   // front face
            0,4, 1,5, 2,6, 3,7    // pillars
        };

        private LineRenderer _lr;
        private Vector3 _targetCenter;
        private Quaternion _targetRotation;
        private Vector3 _targetSize;
        private bool _initialized;
        private bool _locked;

        private void Awake()
        {
            _lr = GetComponent<LineRenderer>();
            _lr.useWorldSpace = true;
            _lr.loop = false;
            _lr.positionCount = Edges.Length;
            _lr.widthMultiplier = m_lineWidth;
            _lr.material = new Material(Shader.Find("Sprites/Default"));
            // Use m_color (original field) so prefab-saved alpha/color is respected
            _lr.startColor = _lr.endColor = m_color;
        }

        private void Update()
        {
            if (!_initialized) return;

            if (m_lerpSpeed > 0f)
            {
                transform.position = Vector3.Lerp(transform.position, _targetCenter, Time.deltaTime * m_lerpSpeed);
                transform.rotation = Quaternion.Slerp(transform.rotation, _targetRotation, Time.deltaTime * m_lerpSpeed);
                transform.localScale = Vector3.Lerp(transform.localScale, _targetSize, Time.deltaTime * m_lerpSpeed);
            }
            else
            {
                transform.position = _targetCenter;
                transform.rotation = _targetRotation;
                transform.localScale = _targetSize;
            }

            RebuildLines(transform.position, transform.rotation, transform.localScale);
        }

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

            if (m_labelText != null)
            {
                m_labelText.text = label;
                m_labelText.transform.position = center + rotation * (Vector3.up * (size.y * 0.5f + 0.06f));
                m_labelText.transform.rotation = rotation;
            }
        }

        public void SetLocked(bool locked)
        {
            _locked = locked;

            if (locked)
            {
                // Snap lerp targets to current transform so box stops moving immediately
                _targetCenter = transform.position;
                _targetRotation = transform.rotation;
                _targetSize = transform.localScale;
            }

            Color c = locked ? m_colorLocked : m_color;
            _lr.startColor = _lr.endColor = c;

            if (m_labelText != null)
                m_labelText.color = c;
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