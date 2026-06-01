// CustomImageSegmentationAgent.cs
//
// Standalone copy of Meta's ImageSegmentationAgent — fully git-trackable,
// outside the package folder, free to edit without SDK updates wiping changes.
//
// Namespace : YourProject.AI   (rename to match your project)
// Replaces  : Meta.XR.BuildingBlocks.AIBlocks.ImageSegmentationAgent
//
// Wire-up: swap every reference to ImageSegmentationAgent in your scene
// prefabs / scripts to CustomImageSegmentationAgent. The public API is
// identical — same fields, same event, same CallInference() method.

using System;
using System.Threading.Tasks;
using Meta.XR;
// Keep using Meta types that live in the package and are not being copied.
using Meta.XR.BuildingBlocks.AIBlocks;   // AIProviderBase, IImageSegmentationTask,
using UnityEngine;
using UnityEngine.Events;
// UnityInferenceEngineProvider, SegmentationResult
// PassthroughCameraAccess, DepthTextureAccess

namespace SmartMove
{
    [Serializable]
    public class OnSegmentationResponseReceived : UnityEvent<SegmentationResult> { }

    public sealed class CustomImageSegmentationAgent : MonoBehaviour
    {
        [Header("Provider")]
        [Tooltip("Provider asset that implements IImageSegmentationTask.")]
        [SerializeField] internal AIProviderBase providerAsset;

        [Tooltip("Run segmentation every N frames. 0 = manual only.")]
        [SerializeField, Range(0, 120)] internal int segmentEveryNFrames = 1;

        [Tooltip("Max resolution (width or height) before sending to inference. 0 = no downscale.")]
        [SerializeField] internal int captureMaxResolution = 640;

        public int CaptureMaxResolution
        {
            get => captureMaxResolution;
            set => captureMaxResolution = value;
        }

#if MRUK_INSTALLED
        /// <summary>
        /// Fired after segmentation is finalized for the current frame.
        /// </summary>
        public event Action<SegmentationResult> OnSegmentationUpdated;

        private PassthroughCameraAccess _cam;
        private DepthTextureAccess _depth;
#endif

        [SerializeField] private OnSegmentationResponseReceived onSegmentationResponseReceived = new();
        public OnSegmentationResponseReceived OnSegmentationResponseReceived => onSegmentationResponseReceived;

        private UnityInferenceEngineProvider _unityProvider;
        private IImageSegmentationTask _segmentTask;
        private RenderTexture _captureRT;
        private bool _busy;
        private bool _mrukWarningShown;

        private async void Awake()
        {
            await Task.CompletedTask;
#if MRUK_INSTALLED
            _cam = FindAnyObjectByType<PassthroughCameraAccess>();
            _depth = GetComponent<DepthTextureAccess>();
            _segmentTask = providerAsset as IImageSegmentationTask;
            _unityProvider = providerAsset as UnityInferenceEngineProvider;

            if (_segmentTask == null)
                Debug.LogError("[CustomImageSegmentationAgent] providerAsset must implement IImageSegmentationTask.");

            if (_unityProvider != null)
            {
#if UNITY_INFERENCE_INSTALLED
                await _unityProvider.WarmUp();
#else
                Debug.LogError("[CustomImageSegmentationAgent] Unity Inference Engine package is not installed.");
#endif
            }
#endif
        }

        private void Update()
        {
#if MRUK_INSTALLED
            if (!_cam.IsPlaying || _busy) return;
#endif
            if (segmentEveryNFrames > 0 && (Time.frameCount % segmentEveryNFrames == 0))
                CallInference();
        }

        /// <summary>
        /// Triggers an asynchronous segmentation pass if not already running.
        /// </summary>
        public void CallInference() => _ = RunSegmentation();

        private Task RunSegmentation()
        {
#if MRUK_INSTALLED
            return RunSegmentationImpl();
#else
            if (!_mrukWarningShown)
            {
                Debug.LogWarning("[CustomImageSegmentationAgent] MRUK not installed — inference unavailable.");
                _mrukWarningShown = true;
            }
            return Task.CompletedTask;
#endif
        }

#if MRUK_INSTALLED
        private async Task RunSegmentationImpl()
        {
            if (_busy) return;
            _busy = true;
            try
            {
                if (_segmentTask == null) return;

                _depth?.RequestDepthSample();

                var src = _cam.GetTexture();

                if (!_captureRT || _captureRT.width != src.width || _captureRT.height != src.height)
                {
                    if (_captureRT) _captureRT.Release();
                    _captureRT = new RenderTexture(src.width, src.height, 0, RenderTextureFormat.ARGB32);
                }

                Graphics.Blit(src, _captureRT);

                var result = await _segmentTask.SegmentAsync(_captureRT);
                if (result == null || result.numObjects == 0) return;

                OnSegmentationUpdated?.Invoke(result);
                onSegmentationResponseReceived.Invoke(result);
            }
            finally
            {
                _busy = false;
            }
        }
#endif
    }
}