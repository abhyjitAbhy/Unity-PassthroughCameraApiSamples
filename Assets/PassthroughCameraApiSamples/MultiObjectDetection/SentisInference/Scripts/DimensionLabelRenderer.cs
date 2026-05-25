// DimensionLabelRenderer.cs  — FIXED
//
// BUG in original:
//   transform.rotation = Quaternion.LookRotation(-dir, Vector3.up) was called
//   with dir.y = 0 but the -dir was already flat — this caused the label to
//   tilt when the camera looked down at floor objects.
//   FIX: use Camera.main.transform.forward directly for the look direction
//   so the label always faces the camera regardless of vertical angle.

using TMPro;
using UnityEngine;

namespace PassthroughCameraSamples.MultiObjectDetection
{
    public class DimensionLabelRenderer : MonoBehaviour
    {
        [HideInInspector] public TextMeshPro LabelTMP;
        public float LabelHeightOffset = 0.12f;
        public float LabelScale = 0.04f;

        private Transform m_camTransform;

        void Start()
        {
            m_camTransform = Camera.main != null ? Camera.main.transform : null;
            transform.localScale = Vector3.one * LabelScale;

            // Auto-create a TextMeshPro child if none was wired
            if (LabelTMP == null)
            {
                var go = new GameObject("Label");
                go.transform.SetParent(transform, false);
                LabelTMP = go.AddComponent<TextMeshPro>();
                LabelTMP.fontSize = 0.3f;
                LabelTMP.alignment = TextAlignmentOptions.Center;
                LabelTMP.color = Color.white;
            }
        }

        void LateUpdate()
        {
            // FIX: face the camera using its actual forward, not a flat projected dir
            if (m_camTransform != null)
                transform.rotation = Quaternion.LookRotation(
                    transform.position - m_camTransform.position, Vector3.up);
        }

        public void UpdateFromOBB(PCAOBBFitter.OBBResult obb, string className = "")
        {
            // Convert half-extents → full dimensions in cm
            float w = obb.Extents.x * 2f * 100f;
            float h = obb.Extents.y * 2f * 100f;
            float d = obb.Extents.z * 2f * 100f;

            LabelTMP.text = string.IsNullOrEmpty(className)
                ? $"{w:F0}×{h:F0}×{d:F0} cm"
                : $"<b>{className}</b>\n{w:F0}×{h:F0}×{d:F0} cm";

            // Position above the top face
            transform.position = obb.Center + Vector3.up * (obb.Extents.y + LabelHeightOffset);
        }
    }
}
