// ═══════════════════════════════════════════════════════════════════════════
// OutlierRejector.cs  — unchanged, included here for completeness
// ═══════════════════════════════════════════════════════════════════════════

namespace PassthroughCameraSamples.MultiObjectDetection
{
    public static class OutlierRejector
    {
        public static UnityEngine.Vector3[] RemoveStatisticalOutliers(
            UnityEngine.Vector3[] points, int k = 8, float stdMul = 1.5f)
        {
            if (points.Length < k + 1) return points;
            float[] meanDists = new float[points.Length];
            for (int i = 0; i < points.Length; i++)
            {
                float[] dists = new float[points.Length - 1]; int di = 0;
                for (int j = 0; j < points.Length; j++)
                {
                    if (i == j) continue;
                    dists[di++] = UnityEngine.Vector3.Distance(points[i], points[j]);
                }
                System.Array.Sort(dists);
                float s = 0; for (int ki = 0; ki < k; ki++) s += dists[ki];
                meanDists[i] = s / k;
            }
            float mean = 0, var2 = 0;
            foreach (var d in meanDists) mean += d; mean /= meanDists.Length;
            foreach (var d in meanDists) var2 += (d - mean) * (d - mean); var2 /= meanDists.Length;
            float thr = mean + stdMul * UnityEngine.Mathf.Sqrt(var2);
            var clean = new System.Collections.Generic.List<UnityEngine.Vector3>(points.Length);
            for (int i = 0; i < points.Length; i++) if (meanDists[i] <= thr) clean.Add(points[i]);
            return clean.ToArray();
        }

        public static UnityEngine.Vector3[] VoxelDownsample(
            UnityEngine.Vector3[] points, float voxelSize)
        {
            var voxels = new System.Collections.Generic.Dictionary<
                UnityEngine.Vector3Int, (UnityEngine.Vector3 sum, int count)>();
            foreach (var p in points)
            {
                var key = new UnityEngine.Vector3Int(
                    UnityEngine.Mathf.FloorToInt(p.x / voxelSize),
                    UnityEngine.Mathf.FloorToInt(p.y / voxelSize),
                    UnityEngine.Mathf.FloorToInt(p.z / voxelSize));
                voxels[key] = voxels.TryGetValue(key, out var e)
                    ? (e.sum + p, e.count + 1) : (p, 1);
            }
            var result = new UnityEngine.Vector3[voxels.Count]; int idx = 0;
            foreach (var kv in voxels) result[idx++] = kv.Value.sum / kv.Value.count;
            return result;
        }
    }
}