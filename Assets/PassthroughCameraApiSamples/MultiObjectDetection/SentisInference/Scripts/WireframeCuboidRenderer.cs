// WireframeCuboidRenderer.cs — RUNTIME SAFE
//
// FIXES applied on top of previous version:
//  - Removed 'using UnityEditor' and ShaderUtil.CreateShaderAsset (Editor-only API,
//    causes CS0103 in player/Quest builds).
//  - Shader is now created via new Material(shader) using Shader.Find with a
//    guaranteed built-in fallback ("Hidden/Internal-Colored") that is always
//    included in Unity builds. This renders unlit vertex-colored lines correctly.
//  - LineColor is applied via material.color which Internal-Colored respects.
//  - All other logic (billboard perpendicular fix, rebuild throttle) preserved.

using UnityEngine;

namespace PassthroughCameraSamples.MultiObjectDetection
{
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public class WireframeCuboidRenderer : MonoBehaviour
    {
        public Color LineColor = new Color(0f, 1f, 0.5f, 0.9f);
        public float LineWidth = 0.006f;

        private Mesh m_mesh;
        private MeshFilter m_filter;
        private MeshRenderer m_renderer;
        private Material m_mat;
        private Vector3 m_lastExtents = Vector3.negativeInfinity;

        void Awake()
        {
            m_filter = GetComponent<MeshFilter>();
            m_renderer = GetComponent<MeshRenderer>();
            m_mesh = new Mesh { name = "WireframeOBB" };
            m_filter.sharedMesh = m_mesh;

            // "Hidden/Internal-Colored" is a Unity built-in shader that is always
            // present in builds. It supports transparency and vertex color tinting
            // via _Color. This replaces the Editor-only ShaderUtil.CreateShaderAsset.
            var shader = Shader.Find("Hidden/Internal-Colored");
            if (shader == null)
            {
                // Absolute fallback — shouldn't happen in any Unity version
                shader = Shader.Find("Unlit/Color");
                Debug.LogWarning("[WireframeCuboidRenderer] Hidden/Internal-Colored not found, using Unlit/Color.");
            }

            m_mat = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            m_mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            m_mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            m_mat.SetInt("_ZWrite", 0);
            m_mat.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
            m_mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;

            m_renderer.sharedMaterial = m_mat;
            m_renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            m_renderer.receiveShadows = false;
        }

        public void UpdateFromOBB(PCAOBBFitter.OBBResult obb)
        {
            transform.SetPositionAndRotation(obb.Center, obb.Orientation);
            transform.localScale = Vector3.one;
            m_mat.color = LineColor;

            Vector3 fullSize = obb.Extents * 2f;
            // Only rebuild mesh when extents changed meaningfully (saves ~0.2ms/frame)
            if ((fullSize - m_lastExtents).sqrMagnitude > 0.0001f)
            {
                BuildMesh(fullSize);
                m_lastExtents = fullSize;
            }
        }

        private void BuildMesh(Vector3 size)
        {
            float hx = size.x * 0.5f;
            float hy = size.y * 0.5f;
            float hz = size.z * 0.5f;

            Vector3[] corners =
            {
                new(-hx,-hy,-hz), new(hx,-hy,-hz), new(hx,hy,-hz), new(-hx,hy,-hz),
                new(-hx,-hy, hz), new(hx,-hy, hz), new(hx,hy, hz), new(-hx,hy, hz),
            };

            int[] edges =
            {
                0,1, 1,2, 2,3, 3,0,
                4,5, 5,6, 6,7, 7,4,
                0,4, 1,5, 2,6, 3,7
            };

            int edgeCount = edges.Length / 2;
            var verts = new Vector3[edgeCount * 4];
            var tris = new int[edgeCount * 6];

            // Compute perpendicular in LOCAL space of the cuboid so lines face camera.
            Camera cam = Camera.main;
            Vector3 camRightWorld = cam != null ? cam.transform.right : Vector3.right;
            Vector3 camRightLocal = transform.InverseTransformDirection(camRightWorld);

            int vi = 0, ti = 0;
            for (int e = 0; e < edges.Length; e += 2)
            {
                Vector3 a = corners[edges[e]];
                Vector3 b = corners[edges[e + 1]];
                Vector3 dir = (b - a).normalized;

                Vector3 perp = Vector3.Cross(dir, camRightLocal).normalized * (LineWidth * 0.5f);
                if (perp.sqrMagnitude < 0.00001f)
                    perp = Vector3.Cross(dir, Vector3.up).normalized * (LineWidth * 0.5f);

                verts[vi] = a - perp;
                verts[vi + 1] = a + perp;
                verts[vi + 2] = b + perp;
                verts[vi + 3] = b - perp;

                tris[ti] = vi; tris[ti + 1] = vi + 1; tris[ti + 2] = vi + 2;
                tris[ti + 3] = vi; tris[ti + 4] = vi + 2; tris[ti + 5] = vi + 3;
                vi += 4; ti += 6;
            }

            m_mesh.Clear();
            m_mesh.vertices = verts;
            m_mesh.triangles = tris;
            m_mesh.RecalculateNormals();
        }

        void OnDestroy()
        {
            if (m_mat != null) Destroy(m_mat);
            if (m_mesh != null) Destroy(m_mesh);
        }
    }
}