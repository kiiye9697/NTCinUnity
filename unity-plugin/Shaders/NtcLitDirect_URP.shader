Shader "NTC/LitDirect_URP"
{
    Properties
    {
        _BaseMap("Base Map (ordinary backend)", 2D) = "white" {}
        _BumpMap("Normal Map (ordinary backend)", 2D) = "bump" {}
        _MetallicGlossMap("Mask (ordinary backend)", 2D) = "white" {}
        _OcclusionMap("Occlusion (ordinary backend)", 2D) = "white" {}
        _BaseColor("Base Color", Color) = (1,1,1,1)
        [HDR] _EmissionColor("Emission", Color) = (0,0,0,1)
        _Metallic("Metallic", Range(0,1)) = 0
        _Smoothness("Smoothness", Range(0,1)) = 0.5
        _BumpScale("Normal Strength", Float) = 1
        _OcclusionStrength("Occlusion Strength", Range(0,1)) = 1
        _AlphaClip("Alpha Clip", Float) = 0
        _Cutoff("Cutoff", Range(0,1)) = 0.5
        [Enum(UnityEngine.Rendering.CullMode)] _Cull("Cull", Float) = 2
        [HideInInspector] _Surface("Surface", Float) = 0
        [HideInInspector] _NtcBackend("NTC backend", Float) = 1
        [HideInInspector] _NtcDebugView("Debug View", Float) = 0
        [HideInInspector] _NtcSmoothnessFromBaseAlpha("Smoothness source", Float) = 0
        [HideInInspector] _NtcHasOriginalMask("Original Mask", Float) = 0
        [HideInInspector] _NtcHasOriginalNormal("Original Normal", Float) = 0
        [HideInInspector] _NtcHasOriginalAO("Original AO", Float) = 0
    }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "RenderType"="Opaque" "Queue"="Geometry" }
        Cull [_Cull]
        HLSLINCLUDE
        #pragma target 5.0
        #pragma use_dxc
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
        #define _NORMALMAP 1
        #define REQUIRES_WORLD_SPACE_POS_INTERPOLATOR 1
        #include "NtcMaterialSampling.hlsl"

        void InitializeStandardLitSurfaceData(float2 uv, out SurfaceData data)
        {
            NtcMaterialSamples s = SampleMaterialTextures(uv, ddx(uv), ddy(uv));
            if (_AlphaClip > 0.5) clip(s.baseColor.a - _Cutoff);
            data = (SurfaceData)0;
            data.albedo = s.baseColor.rgb;
            data.alpha = s.baseColor.a;
            data.normalTS = s.normalTS;
            data.metallic = s.metallic;
            data.smoothness = s.smoothness;
            data.occlusion = s.occlusion;
            data.emission = _EmissionColor.rgb;
        }
        #include "Packages/com.unity.render-pipelines.universal/Shaders/LitForwardPass.hlsl"

        half4 NtcForward(Varyings input) : SV_Target
        {
            UNITY_SETUP_INSTANCE_ID(input);
            UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
            SurfaceData surface;
            InitializeStandardLitSurfaceData(input.uv, surface);
            if (_NtcDebugView == 1) return half4(surface.albedo, 1);
            if (_NtcDebugView == 2) return half4(normalize(surface.normalTS) * 0.5 + 0.5, 1);
            if (_NtcDebugView == 3) return half4(surface.metallic.xxx, 1);
            if (_NtcDebugView == 4) return half4((1.0 - surface.smoothness).xxx, 1);
            if (_NtcDebugView == 5) return half4(surface.occlusion.xxx, 1);
            InputData lighting;
            InitializeInputData(input, surface.normalTS, lighting);
            #if defined(_DBUFFER)
                ApplyDecalToSurfaceData(input.positionCS, surface, lighting);
            #endif
            half4 color = UniversalFragmentPBR(lighting, surface);
            color.rgb = MixFog(color.rgb, lighting.fogCoord);
            return half4(color.rgb, 1);
        }

        half4 NtcDepth(Varyings input) : SV_Target
        {
            if (_AlphaClip > 0.5)
            {
                NtcMaterialSamples s = SampleMaterialTextures(input.uv, ddx(input.uv), ddy(input.uv));
                clip(s.baseColor.a - _Cutoff);
            }
            return 0;
        }

        half4 NtcDepthNormals(Varyings input) : SV_Target
        {
            SurfaceData surface;
            InitializeStandardLitSurfaceData(input.uv, surface);
            float3 n = normalize(input.normalWS);
            float3 tangent = normalize(input.tangentWS.xyz);
            float3 bitangent = input.tangentWS.w * cross(n, tangent);
            n = normalize(TransformTangentToWorld(surface.normalTS, half3x3(tangent, bitangent, n)));
            #if defined(_GBUFFER_NORMALS_OCT)
                float2 oct = PackNormalOctQuadEncode(n);
                return half4(PackFloat2To888(saturate(oct * 0.5 + 0.5)), 0);
            #else
                return half4(n, 0);
            #endif
        }

        float3 _LightDirection, _LightPosition;
        Varyings NtcShadowVertex(Attributes input)
        {
            Varyings o = LitPassVertex(input);
            float3 direction = _LightDirection;
            #if defined(_CASTING_PUNCTUAL_LIGHT_SHADOW)
                direction = normalize(_LightPosition - o.positionWS);
            #endif
            o.positionCS = TransformWorldToHClip(ApplyShadowBias(o.positionWS, o.normalWS, direction));
            #if UNITY_REVERSED_Z
                o.positionCS.z = min(o.positionCS.z, UNITY_NEAR_CLIP_VALUE * o.positionCS.w);
            #else
                o.positionCS.z = max(o.positionCS.z, UNITY_NEAR_CLIP_VALUE * o.positionCS.w);
            #endif
            return o;
        }
        ENDHLSL

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode"="UniversalForwardOnly" }
            HLSLPROGRAM
            #pragma vertex LitPassVertex
            #pragma fragment NtcForward
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BLENDING
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BOX_PROJECTION
            #pragma multi_compile_fragment _ _LIGHT_LAYERS
            #pragma multi_compile_fragment _ _LIGHT_COOKIES
            #pragma multi_compile _ _CLUSTERED_RENDERING
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #pragma multi_compile_fragment _ _DBUFFER_MRT1 _DBUFFER_MRT2 _DBUFFER_MRT3
            #pragma shader_feature_local _RECEIVE_SHADOWS_OFF
            #pragma shader_feature_local_fragment _SPECULARHIGHLIGHTS_OFF
            #pragma shader_feature_local_fragment _ENVIRONMENTREFLECTIONS_OFF
            #pragma multi_compile _ LIGHTMAP_ON
            #pragma multi_compile _ DIRLIGHTMAP_COMBINED
            #pragma multi_compile _ DYNAMICLIGHTMAP_ON
            #pragma multi_compile _ LIGHTMAP_SHADOW_MIXING
            #pragma multi_compile _ SHADOWS_SHADOWMASK
            #pragma multi_compile_fog
            #pragma multi_compile_instancing
            #pragma instancing_options renderinglayer
            ENDHLSL
        }
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode"="ShadowCaster" }
            ZWrite On ZTest LEqual ColorMask 0
            HLSLPROGRAM
            #pragma vertex NtcShadowVertex
            #pragma fragment NtcDepth
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW
            #pragma multi_compile_instancing
            ENDHLSL
        }
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode"="DepthOnly" }
            ZWrite On ColorMask 0
            HLSLPROGRAM
            #pragma vertex LitPassVertex
            #pragma fragment NtcDepth
            #pragma multi_compile_instancing
            ENDHLSL
        }
        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode"="DepthNormals" }
            ZWrite On
            HLSLPROGRAM
            #pragma vertex LitPassVertex
            #pragma fragment NtcDepthNormals
            #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT
            #pragma multi_compile_instancing
            ENDHLSL
        }
    }
    FallBack Off
}
