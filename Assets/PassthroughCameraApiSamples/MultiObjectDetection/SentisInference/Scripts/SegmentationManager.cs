// SegmentationManager.cs
using System.Collections.Generic;
using Unity.InferenceEngine;
using UnityEngine;

public class SegmentationManager : MonoBehaviour
{
    [Header("Model")]
    public ModelAsset YOLOModel;          // YOLOv8n-seg.onnx via Unity Sentis
    public float ConfidenceThreshold = 0.45f;
    public float NMSThreshold = 0.45f;
    public int MaxDetections = 8;

    private Worker _worker;
    private Model _model;
    private PassthroughCapture _capture;

    public System.Action<List<SegmentationResult>> OnSegmentationComplete;

    public struct SegmentationResult
    {
        public Rect BBox2D;       // normalized [0,1]
        public Texture2D Mask;         // same size as input frame, 0/1 float
        public int ClassID;
        public float Confidence;
        public string ClassName;
    }

    void Start()
    {
        _model = ModelLoader.Load(YOLOModel);
        _worker = new Worker(_model, BackendType.GPUCompute);  // Run on Quest GPU

        _capture = FindObjectOfType<PassthroughCapture>();
        _capture.OnFrameCaptured += RunInference;
    }

    private void RunInference(Texture2D frame)
    {
        // Resize frame to 640x640 for YOLOv8
        var resized = ResizeTexture(frame, 640, 640);

        using var inputTensor = TextureConverter.ToTensor(resized, 640, 640, 3);
        _worker.Schedule(inputTensor);

        // YOLOv8-seg outputs: output0 [1,116,8400] detections + output1 [1,32,160,160] proto masks
        var detectionOutput = _worker.PeekOutput("output0") as Tensor<float>;
        var protoOutput = _worker.PeekOutput("output1") as Tensor<float>;

        var results = DecodeYOLOSeg(detectionOutput, protoOutput, frame.width, frame.height);
        OnSegmentationComplete?.Invoke(results);

        UnityEngine.Object.Destroy(resized);
    }

    private List<SegmentationResult> DecodeYOLOSeg(
        Tensor<float> det, Tensor<float> proto, int origW, int origH)
    {
        var results = new List<SegmentationResult>();
        var detData = det.DownloadToArray();
        var protoData = proto.DownloadToArray();

        // det shape: [1, 116, 8400]  → 4 bbox + 1 conf + 80 classes + 32 mask coeffs
        int numDet = 8400;
        int channels = 116;

        // Collect valid detections above threshold
        var candidates = new List<(int idx, float conf, int cls, Rect box)>();
        for (int i = 0; i < numDet; i++)
        {
            float objConf = detData[4 * numDet + i]; // objectness
            if (objConf < ConfidenceThreshold) continue;

            // Find best class
            float bestCls = 0; int bestClsID = 0;
            for (int c = 0; c < 80; c++)
            {
                float score = detData[(5 + c) * numDet + i];
                if (score > bestCls) { bestCls = score; bestClsID = c; }
            }
            float finalConf = objConf * bestCls;
            if (finalConf < ConfidenceThreshold) continue;

            float cx = detData[0 * numDet + i] / 640f;
            float cy = detData[1 * numDet + i] / 640f;
            float bw = detData[2 * numDet + i] / 640f;
            float bh = detData[3 * numDet + i] / 640f;
            var box = new Rect(cx - bw / 2, cy - bh / 2, bw, bh);

            candidates.Add((i, finalConf, bestClsID, box));
        }

        // Simple NMS
        candidates.Sort((a, b) => b.conf.CompareTo(a.conf));
        var kept = NMS(candidates, NMSThreshold);

        foreach (var (idx, conf, cls, box) in kept)
        {
            if (results.Count >= MaxDetections) break;

            // Compute instance mask from proto + coefficients
            float[] coeffs = new float[32];
            for (int k = 0; k < 32; k++)
                coeffs[k] = detData[(85 + k) * numDet + idx];

            var mask = BuildInstanceMask(coeffs, protoData, origW, origH, box);

            results.Add(new SegmentationResult
            {
                BBox2D = box,
                Mask = mask,
                ClassID = cls,
                Confidence = conf,
                ClassName = YOLOClassNames.Names[cls]
            });
        }
        return results;
    }

    private Texture2D BuildInstanceMask(
        float[] coeffs, float[] proto, int outW, int outH, Rect box)
    {
        // proto shape: [1,32,160,160]
        int protoH = 160, protoW = 160;
        float[] maskData = new float[protoH * protoW];

        for (int y = 0; y < protoH; y++)
            for (int x = 0; x < protoW; x++)
            {
                float v = 0;
                for (int k = 0; k < 32; k++)
                    v += coeffs[k] * proto[k * protoH * protoW + y * protoW + x];
                maskData[y * protoW + x] = 1f / (1f + Mathf.Exp(-v)); // sigmoid
            }

        // Crop to bounding box region and threshold
        var tex = new Texture2D(outW, outH, TextureFormat.RFloat, false);
        var pixels = new float[outW * outH];
        for (int y = 0; y < outH; y++)
            for (int x = 0; x < outW; x++)
            {
                // Map output pixel → proto pixel
                float nx = (float)x / outW;
                float ny = (float)y / outH;

                // Only fill inside bounding box
                bool inBox = (nx >= box.xMin && nx <= box.xMax &&
                              ny >= box.yMin && ny <= box.yMax);
                if (!inBox) { pixels[y * outW + x] = 0; continue; }

                int px = Mathf.Clamp((int)(nx * protoW), 0, protoW - 1);
                int py = Mathf.Clamp((int)(ny * protoH), 0, protoH - 1);
                pixels[y * outW + x] = maskData[py * protoW + px] > 0.5f ? 1f : 0f;
            }
        tex.SetPixelData(pixels, 0);
        tex.Apply();
        return tex;
    }

    private List<(int, float, int, Rect)> NMS(
        List<(int idx, float conf, int cls, Rect box)> dets, float iouThresh)
    {
        var kept = new List<(int, float, int, Rect)>();
        bool[] suppressed = new bool[dets.Count];
        for (int i = 0; i < dets.Count; i++)
        {
            if (suppressed[i]) continue;
            kept.Add(dets[i]);
            for (int j = i + 1; j < dets.Count; j++)
            {
                if (suppressed[j]) continue;
                if (IoU(dets[i].box, dets[j].box) > iouThresh)
                    suppressed[j] = true;
            }
        }
        return kept;
    }

    private float IoU(Rect a, Rect b)
    {
        float ix = Mathf.Max(0, Mathf.Min(a.xMax, b.xMax) - Mathf.Max(a.xMin, b.xMin));
        float iy = Mathf.Max(0, Mathf.Min(a.yMax, b.yMax) - Mathf.Max(a.yMin, b.yMin));
        float inter = ix * iy;
        return inter / (a.width * a.height + b.width * b.height - inter);
    }

    private Texture2D ResizeTexture(Texture2D src, int w, int h)
    {
        var rt = RenderTexture.GetTemporary(w, h, 0);
        Graphics.Blit(src, rt);
        var prev = RenderTexture.active;
        RenderTexture.active = rt;
        var dst = new Texture2D(w, h, TextureFormat.RGB24, false);
        dst.ReadPixels(new Rect(0, 0, w, h), 0, 0);
        dst.Apply();
        RenderTexture.active = prev;
        RenderTexture.ReleaseTemporary(rt);
        return dst;
    }

    void OnDestroy() { _worker?.Dispose(); }
}