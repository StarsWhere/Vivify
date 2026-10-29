Shader "Vivify/Checks/Refract"
{
    Properties { _Prefix("Prefix",Color)=(0,0,0,0) }
    SubShader
    {
        Tags { "Queue"="Transparent" }
        Pass
        {
            Name "PREFIX"
            Tags { "LightMode"="SRPDefaultUnlit" }
            Cull Off ZWrite Off Blend SrcAlpha OneMinusSrcAlpha
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            float4 _Prefix;
            float4 vert(float4 position:POSITION):SV_POSITION{return UnityObjectToClipPos(position);}
            float4 frag():SV_Target{return _Prefix;}
            ENDCG
        }
        Pass
        {
            Name "REFRACT"
            Tags { "LightMode"="SRPDefaultUnlit" }
            Cull Off ZWrite Off
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _EffectSmokeGrab;
            struct v2f {float4 pos:SV_POSITION;float4 grab:TEXCOORD0;};
            v2f vert(float4 position:POSITION){v2f o;o.pos=UnityObjectToClipPos(position);o.grab=ComputeGrabScreenPos(o.pos);return o;}
            float4 frag(v2f i):SV_Target{return tex2Dproj(_EffectSmokeGrab,i.grab);}
            ENDCG
        }
    }
}
