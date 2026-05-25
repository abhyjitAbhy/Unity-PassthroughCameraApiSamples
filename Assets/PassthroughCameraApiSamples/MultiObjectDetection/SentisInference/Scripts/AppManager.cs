//// AppManager.cs
//using System.Collections.Generic;
//using UnityEngine;

//public class AppManager : MonoBehaviour
//{
//    [Header("Core Components")]
//    public DepthTextureProvider DepthProvider;
//    public PassthroughCapture PassthroughCapture;
//    public SegmentationManager SegmentationManager;
//    public PointCloudBuilder PointCloudBuilder;

//    [Header("Prefabs")]
//    public GameObject WireframePrefab;
//    public GameObject LabelPrefab;

//    [Header("OBB Settings")]
//    public int MinPointsForOBB = 50;
//    public float OutlierKNeighbours = 8;
//    public float VoxelSize = 0.02f;  // 2 cm downsampling

//    private Camera _eyeCamera;

//    // Per-detected-object state
//    private class ObjectState
//    {
//        public WireframeCuboidRenderer Cuboid;
//        public DimensionLabelRenderer Label;
//        public TemporalSmoother Smoother;
//        public float LastSeen;
//        public string ClassName;
//    }

//    private readonly List<ObjectState> _objects = new();
//    private const float ObjectTimeout = 2.0f;  // Remove if not seen for N seconds

//    void Start()
//    {
//        _eyeCamera = Camera.main;
//        SegmentationManager.OnSegmentationComplete += OnSegmentationResult;
//    }

//    private void OnSegmentationResult(List<SegmentationManager.SegmentationResult> results)
//    {
//        // Expire old objects
//        for (int i = _objects.Count - 1; i >= 0; i--)
//        {
//            if (Time.time - _objects[i].LastSeen > ObjectTimeout)
//            {
//                Destroy(_objects[i].Cuboid.gameObject);
//                Destroy(_objects[i].Label.gameObject);
//                _objects.RemoveAt(i);
//            }
//        }

//        // Ensure we have enough object slots
//        while (_objects.Count < results.Count)
//        {
//            _objects.Add(new ObjectState
//            {
//                Cuboid = Instantiate(WireframePrefab).GetComponent<WireframeCuboidRenderer>(),
//                Label = Instantiate(LabelPrefab).GetComponent<DimensionLabelRenderer>(),
//                Smoother = new TemporalSmoother(historyLen: 5, posAlpha: 0.25f, sizeAlpha: 0.12f)
//            });
//        }

//        for (int i = 0; i < results.Count; i++)
//        {
//            var seg = results[i];
//            var obj = _objects[i];
//            obj.ClassName = seg.ClassName;
//            obj.LastSeen = Time.time;

//            // Build point cloud from depth inside mask
//            Vector3[] rawPts = PointCloudBuilder.BuildPointCloud(
//                DepthProvider.DepthRT,
//                seg.Mask,
//                _eyeCamera,
//                DepthProvider.ReprojectionMatrix,
//                DepthProvider.ZBufferParams);

//            if (rawPts.Length < MinPointsForOBB) continue;

//            // Outlier removal → voxel downsample
//            var cleanPts = OutlierRejector.RemoveStatisticalOutliers(rawPts, 8, 1.5f);
//            var sparseRts = OutlierRejector.VoxelDownsample(cleanPts, VoxelSize);

//            if (sparseRts.Length < MinPointsForOBB / 2) continue;

//            // Fit OBB
//            var obb = PCAOBBFitter.Fit(sparseRts);

//            // Prevent camera-facing orientation
//            obb = PCAOBBFitter.CorrectCameraAlignment(obb, _eyeCamera);

//            // Temporal smoothing
//            obb = obj.Smoother.Smooth(obb);

//            // Update visuals
//            obj.Cuboid.gameObject.SetActive(true);
//            obj.Label.gameObject.SetActive(true);
//            obj.Cuboid.UpdateFromOBB(obb);
//            obj.Label.UpdateFromOBB(obb, seg.ClassName);
//        }

//        // Hide unused slots
//        for (int i = results.Count; i < _objects.Count; i++)
//        {
//            _objects[i].Cuboid.gameObject.SetActive(false);
//            _objects[i].Label.gameObject.SetActive(false);
//        }
//    }
//}