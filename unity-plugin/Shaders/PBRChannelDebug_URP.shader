// PBRChannelDebug_URP.shader
// Unlit URP debug shader used by NtcSceneController View Mode.
Shader "NTC/PBRChannelDebug_URP"
{
    Properties
    {
        _BaseMap ("Base Map", 2D) = "white" {}
        _BumpMap ("Normal Map", 2D) = "bump" {}
        _MetallicGlossMap ("Metallic Smoothness", 2D) = "white" {}
        _OcclusionMap ("Occlusion", 2D) = "white" {}
        _Metallic ("Metallic", Range(0, 1)) = 0
        _Smoothness ("Smoothness", Range(0, 1)) = 0.5
        _BumpScale ("Normal Scale", Float) = 1
        _NtcDebugMode ("Debug Mode", Float) = 1
        // Mirrors whether the source material has _METALLICSPECGLOSSMAP enabled (set from C#,
        // see NtcSceneController.BindDebugViewMaterial). Needed so this debug view branches
        // EXACTLY like URP's real LitInput.hlsl SampleMetallicSpecGloss(): when the map is
        // present, metallic/smoothness come from the map (metallic is NEVER multiplied by the
        // _Metallic scalar in the real shader); when absent, both are pure scalars and the
        // texture must not be sampled at all. Getting this wrong is what caused the "metal
        // overcompensation" debug-view-only bug found 2026-07-10.
        _NtcHasGlossMap ("Has MetallicGlossMap (debug)", Float) = 1
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" "Queue"="Geometry" }
        LOD 100

        Pass
        {
            Name "PBRChannelDebug"
            Tags { "LightMode"="SRPDefaultUnlit" }
            Cull Off
            ZWrite On
            ZTest LEqual

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_BaseMap);            SAMPLER(sampler_BaseMap);
            TEXTURE2D(_BumpMap);            SAMPLER(sampler_BumpMap);
            TEXTURE2D(_MetallicGlossMap);   SAMPLER(sampler_MetallicGlossMap);
            TEXTURE2D(_OcclusionMap);       SAMPLER(sampler_OcclusionMap);

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                float4 _BumpMap_ST;
                float4 _MetallicGlossMap_ST;
                float4 _OcclusionMap_ST;
                float _Metallic;
                float _Smoothness;
                float _BumpScale;
                float _NtcDebugMode;
                float _NtcHasGlossMap;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv         : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
            };

            Varyings vert(Attributes v)
            {
                Varyings o;
                o.positionCS = TransformObjectToHClip(v.positionOS.xyz);
                o.uv = v.uv;
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                int mode = (int)round(_NtcDebugMode);

                if (mode == 1) // BaseColor
                {
                    float2 uv = i.uv * _BaseMap_ST.xy + _BaseMap_ST.zw;
                    return half4(SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, uv).rgb, 1);
                }

                if (mode == 2) // Normal, shown as tangent-space XYZ mapped to 0..1
                {
                    float2 uv = i.uv * _BumpMap_ST.xy + _BumpMap_ST.zw;
                    float4 raw = SAMPLE_TEXTURE2D(_BumpMap, sampler_BumpMap, uv);
                    float3 n = float3(raw.r * 2.0 - 1.0, raw.g * 2.0 - 1.0, raw.b * 2.0 - 1.0);
                    if (abs(n.z + 1.0) < 1e-4 || raw.b < 1e-4)
                        n.z = sqrt(saturate(1.0 - dot(n.xy, n.xy)));
                    n.xy *= _BumpScale;
                    n = normalize(n);
                    return half4(n * 0.5 + 0.5, 1);
                }

                if (mode == 3) // Metallic -- must match LitInput.hlsl SampleMetallicSpecGloss()
                                // EXACTLY: when a gloss map is bound, metallic = map.r with NO
                                // _Metallic scalar multiply (the real shader ignores the scalar
                                // in this case); when no map, metallic IS the pure scalar. Doing
                                // map.r * _Metallic unconditionally (the old bug) made every
                                // material with _Metallic=0 (the common case in this scene, e.g.
                                // Ceiling/Bricks/Floor/Column_A/B/Details/Ivy/Lion_*/Vase_*/Roof)
                                // show 0 metal in Original but the real texture value once
                                // on-Load/on-Sample forces the scalar to 1 -- a debug-view-only
                                // artifact that looked like "metal overcompensation" but never
                                // existed in the actual lit render.
                {
                    float m;
                    if (_NtcHasGlossMap > 0.5)
                    {
                        float2 uv = i.uv * _MetallicGlossMap_ST.xy + _MetallicGlossMap_ST.zw;
                        m = SAMPLE_TEXTURE2D(_MetallicGlossMap, sampler_MetallicGlossMap, uv).r;
                    }
                    else
                    {
                        m = _Metallic;
                    }
                    return half4(m, m, m, 1);
                }

                if (mode == 4) // Roughness = 1 - smoothness, same map-vs-scalar branch as above
                {
                    float smoothVal;
                    if (_NtcHasGlossMap > 0.5)
                    {
                        float2 uv = i.uv * _MetallicGlossMap_ST.xy + _MetallicGlossMap_ST.zw;
                        smoothVal = SAMPLE_TEXTURE2D(_MetallicGlossMap, sampler_MetallicGlossMap, uv).a * _Smoothness;
                    }
                    else
                    {
                        smoothVal = _Smoothness;
                    }
                    float rough = 1.0 - smoothVal;
                    return half4(rough, rough, rough, 1);
                }

                float2 uv = i.uv * _BaseMap_ST.xy + _BaseMap_ST.zw;
                return half4(SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, uv).rgb, 1);
            }
            ENDHLSL
        }
    }
}
