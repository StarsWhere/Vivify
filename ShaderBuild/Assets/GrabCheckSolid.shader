Shader "Vivify/Checks/Solid"
{
    Properties { _Color("Color",Color)=(1,1,1,1) }
    SubShader
    {
        Tags { "Queue"="Transparent" }
        Pass
        {
            Tags { "LightMode"="SRPDefaultUnlit" }
            Cull Off ZWrite Off Blend SrcAlpha OneMinusSrcAlpha
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            float4 _Color;
            float4 vert(float4 position:POSITION):SV_POSITION{return UnityObjectToClipPos(position);}
            float4 frag():SV_Target{return _Color;}
            ENDCG
        }
    }
}
