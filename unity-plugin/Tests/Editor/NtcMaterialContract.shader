Shader "Hidden/NTC/MaterialContractTest"
{
    Properties
    {
        _BaseMap ("Color", 2D) = "white" {}
        _BumpMap ("Normal", 2D) = "bump" {}
        _OcclusionMap ("AO", 2D) = "white" {}
        _MetallicGlossMap ("Mask", 2D) = "white" {}
        _BumpScale ("Normal scale", Float) = 1
        _Smoothness ("Smoothness", Float) = 0.5
        _Metallic ("Metallic", Float) = 0
        _Surface ("Surface", Float) = 0
        _SmoothnessTextureChannel ("Smoothness source", Float) = 0
    }
    SubShader
    {
        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma shader_feature_local _METALLICSPECGLOSSMAP
            #pragma shader_feature_local _SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A
            float4 vert(float4 vertex : POSITION) : SV_POSITION { return vertex; }
            float4 frag() : SV_Target { return 1; }
            ENDHLSL
        }
    }
}
