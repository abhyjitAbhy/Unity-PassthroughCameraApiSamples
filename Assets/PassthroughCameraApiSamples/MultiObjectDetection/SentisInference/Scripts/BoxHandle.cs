using UnityEngine;

namespace PassthroughCameraSamples.MultiObjectDetection
{
    public enum HandleType { Center, Top, Bottom, Front, Back, Right, Left }
    public enum HandleState { Idle, Hovered, Grabbed }

    /// <summary>
    /// Plain sphere handle. No Rigidbody, no Grabbable, no DistanceGrabInteractable.
    /// BoxInteractionManager owns all hit detection and state changes.
    /// </summary>
    public class BoxHandle : MonoBehaviour
    {
        public HandleType Type { get; set; }
        public BoxManipulator Owner { get; set; }

        // Colours
        private static readonly Color ColIdle    = new(1f,  1f,    1f,    0.45f);
        private static readonly Color ColHovered = new(1f,  0.92f, 0f,    1f);
        private static readonly Color ColGrabbed = new(0f,  0.85f, 1f,    1f);

        private Renderer   _rend;
        private MaterialPropertyBlock _mpb;
        private static readonly int _colorID = Shader.PropertyToID("_Color");

        private HandleState _state = HandleState.Idle;
        public  HandleState State => _state;

        private void Awake()
        {
            _rend = GetComponent<Renderer>();
            _mpb  = new MaterialPropertyBlock();
            ApplyColor(ColIdle);
        }

        public void SetState(HandleState s)
        {
            if (_state == s) return;
            _state = s;
            switch (s)
            {
                case HandleState.Idle:    ApplyColor(ColIdle);    break;
                case HandleState.Hovered: ApplyColor(ColHovered); break;
                case HandleState.Grabbed: ApplyColor(ColGrabbed); break;
            }
        }

        private void ApplyColor(Color c)
        {
            if (_rend == null) return;
            _rend.GetPropertyBlock(_mpb);
            _mpb.SetColor(_colorID, c);
            _rend.SetPropertyBlock(_mpb);
        }
    }
}
