using TMPro;
using UnityEngine;

namespace PassthroughCameraSamples.MultiObjectDetection
{
    /// <summary>
    /// Attach to any bounding box GameObject.
    /// Reads world-space localScale each frame and displays
    /// real-world dimensions as "W × H × D cm" on a TMP label.
    /// No dependency on Box3DVisualizer.
    /// </summary>
    public class Box3DDimensionDisplay : MonoBehaviour
    {
        [Header("Label")]
        [SerializeField] private TextMeshProUGUI dimensionLabel;

        [Header("Display")]
        [SerializeField, Range(0f, 1f)] private float updateInterval = 0.1f;
        [SerializeField] private bool billboard = true;
        [SerializeField, Range(-1f, 1f)] private float verticalOffsetFactor = -0.6f;

        private Camera _mainCam;
        private float _timer;
        private bool _frozen;

        private void Awake()
        {
            _mainCam = Camera.main;

            if (dimensionLabel == null)
            {
                Debug.LogError("[Box3DDimensionDisplay] dimensionLabel not assigned.");
                enabled = false;
                return;
            }

            dimensionLabel.gameObject.SetActive(true);
        }

        private void Update()
        {
            if (_frozen) return;

            _timer += Time.deltaTime;
            if (updateInterval > 0f && _timer < updateInterval)
                return;

            _timer = 0f;

            UpdateLabel();

            if (billboard)
                BillboardLabel();
        }

        public void SetFrozen(bool frozen)
        {
            _frozen = frozen;
            if (dimensionLabel != null)
                dimensionLabel.gameObject.SetActive(true);
        }

        private void UpdateLabel()
        {
            dimensionLabel.text = FormatDimensions(transform.localScale);
        }

        private void BillboardLabel()
        {
            if (_mainCam == null) return;

            float halfH = transform.localScale.y * 0.5f;
            Vector3 offset = transform.rotation *
                (Vector3.up * (halfH * verticalOffsetFactor));

            dimensionLabel.transform.position = transform.position + offset;

            Vector3 toCamera = _mainCam.transform.position
                - dimensionLabel.transform.position;

            if (toCamera.sqrMagnitude > 0.0001f)
                dimensionLabel.transform.rotation =
                    Quaternion.LookRotation(-toCamera.normalized);
        }

        private static string FormatDimensions(Vector3 sizeM)
        {
            float w = sizeM.x * 100f;
            float h = sizeM.y * 100f;
            float d = sizeM.z * 100f;

            if (Mathf.Max(w, h, d) > 999f)
                return $"{sizeM.x:F2} × {sizeM.y:F2} × {sizeM.z:F2} m";

            return $"{Mathf.RoundToInt(w)} × {Mathf.RoundToInt(h)} × {Mathf.RoundToInt(d)} cm";
        }
    }
}