Shader "Vivify/Checks/Eye"
{
    SubShader
    {
        Pass
        {
            Cull Off ZWrite Off ZTest Always
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"
            int _StereoActiveEye;
            float4 frag(v2f_img i):SV_Target
            {
                if(_StereoActiveEye==0)return float4(1,0,0,1);
                if(_StereoActiveEye==1)return float4(0,0,1,1);
                return float4(0,1,0,1);
            }
            ENDCG
        }
    }
}
