// PassthroughCapture.cs
using UnityEngine;
using UnityEngine.Rendering;

public class PassthroughCapture : MonoBehaviour
{
    [Header("Capture Settings")]
    public int CaptureWidth = 640;
    public int CaptureHeight = 480;
    public int SegmentationFPS = 5;  // Run segmentation at reduced rate

    private RenderTexture _captureRT;
    private Texture2D _cpuTexture;
    private Camera _passthroughCam;
    private float _lastCaptureTime;

    public System.Action<Texture2D> OnFrameCaptured;

    void Start()
    {
        _captureRT = new RenderTexture(CaptureWidth, CaptureHeight, 0, RenderTextureFormat.ARGB32);
        _cpuTexture = new Texture2D(CaptureWidth, CaptureHeight, TextureFormat.RGB24, false);

        // Find or create passthrough camera
        _passthroughCam = GetComponent<Camera>();
    }

    void Update()
    {
        if (Time.time - _lastCaptureTime < 1f / SegmentationFPS) return;
        _lastCaptureTime = Time.time;

        AsyncGPUReadback.Request(_captureRT, 0, TextureFormat.RGB24, OnReadbackComplete);
    }

    private void OnReadbackComplete(AsyncGPUReadbackRequest req)
    {
        if (req.hasError) return;
        _cpuTexture.LoadRawTextureData(req.GetData<byte>());
        _cpuTexture.Apply();
        OnFrameCaptured?.Invoke(_cpuTexture);
    }
}