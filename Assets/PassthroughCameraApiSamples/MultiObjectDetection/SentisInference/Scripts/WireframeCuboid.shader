// WireframeCuboid.shader
Shader "Quest3/WireframeCuboid"
{
    Properties
    {
        _Color     ("Line Color", Color)   = (0, 1, 0.5, 1)
        _LineWidth ("Line Width", Float)   = 0.005
    }
    SubShader
    {
        Tags { "Queue"="Overlay" "RenderType"="Transparent" }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        ZTest Always  // Always visible through passthrough
        Cull Off

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            float4 _Color;

            struct appdata { float4 vertex : POSITION; };
            struct v2f    { float4 pos    : SV_POSITION; };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                return o;
            }

            float4 frag(v2f i) : SV_Target { return _Color; }
            ENDCG
        }
    }
}