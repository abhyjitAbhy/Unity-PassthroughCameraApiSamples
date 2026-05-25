// ═══════════════════════════════════════════════════════════════════════════
// TemporalSmoother.cs  — FIXED
//
// BUG in original:
//   relChange computed as (smoothExtents - newExtents).magnitude / smoothExtents.magnitude
//   but smoothExtents could be near-zero on first frames → NaN division → reset loop.
//   FIX: guard against zero denominator.
// ═══════════════════════════════════════════════════════════════════════════

namespace PassthroughCameraSamples.MultiObjectDetection
{
    public class TemporalSmoother
    {
        private readonly int m_histLen;
        private readonly float m_posAlpha;
        private readonly float m_sizeAlpha;

        private UnityEngine.Vector3 m_center;
        private UnityEngine.Quaternion m_orient;
        private UnityEngine.Vector3 m_extents;
        private bool m_init;

        public TemporalSmoother(int histLen = 5, float posAlpha = 0.3f, float sizeAlpha = 0.15f)
        {
            m_histLen = histLen;
            m_posAlpha = posAlpha;
            m_sizeAlpha = sizeAlpha;
        }

        public PCAOBBFitter.OBBResult Smooth(PCAOBBFitter.OBBResult n)
        {
            if (!m_init)
            {
                m_center = n.Center; m_orient = n.Orientation; m_extents = n.Extents;
                m_init = true; return n;
            }

            m_center = UnityEngine.Vector3.Lerp(m_center, n.Center, m_posAlpha);
            m_extents = UnityEngine.Vector3.Lerp(m_extents, n.Extents, m_sizeAlpha);
            m_orient = UnityEngine.Quaternion.Slerp(m_orient, n.Orientation, m_posAlpha);

            // FIX: guard zero denominator
            float denom = m_extents.magnitude;
            if (denom > 0.001f)
            {
                float rel = (m_extents - n.Extents).magnitude / denom;
                if (rel > 0.5f) { m_extents = n.Extents; m_center = n.Center; }
            }

            return new PCAOBBFitter.OBBResult
            {
                Center = m_center,
                Extents = m_extents,
                Orientation = m_orient,
                Axis0 = n.Axis0,
                Axis1 = n.Axis1,
                Axis2 = n.Axis2,
                PointCount = n.PointCount
            };
        }

        public void Reset() { m_init = false; }
    }
}
