// NtcInferenceOnSample_URP.shader
// URP (Universal Render Pipeline 12.x) port of the NTC "Inference on Sample" path.
//
// Runs the full 4-layer NTC MLP (48->64->48->32->16) per fragment, decoding the
// BaseColor straight out of the latent grid + weight buffer -- NO pre-decompressed
// texture is ever created. The heavy math lives in NtcInference.hlsl (pure HLSL,
// no CG legacy), #include'd inside this URP HLSLPROGRAM block.
//
// Data bound from C# (see NtcSceneController.cs):
//   _NtcLatents        Texture2DArray  (RGBA32, BGRA4 expanded)
//   _NtcWeights        ByteAddressBuffer (concatenated INT8 weights/scale/bias)
//   _NtcColorMips[16]  float4 : (neuralMip, positionLod, positionScale, 0)
//   _NtcWeightOffsets / _NtcBiasOffsets / _NtcScaleOffsets : float4 byte offsets per layer
//   _NtcImageInfo      float4 : (width, height, mips, asfloat(validChannelMask))
Shader "NTC/InferenceOnSample_URP"
{
    Properties
    {
        _ChannelOffset ("First output channel", Float) = 0
        // Debug-only override for the BaseColor sRGB->linear conversion (see NtcDecompressor.
        // ColorSpaceMode in NtcSceneController.cs): 0 = Auto (use _NtcChannelColorSpaces from the
        // .ntc container, default/correct), 1 = ForceSrgbDecode, 2 = ForceRawLinear.
        _NtcColorSpaceOverride ("Color space debug override", Float) = 0
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" "Queue"="Geometry" }
        LOD 100

        Pass
        {
            Name "NtcInferenceOnSample"
            Tags { "LightMode"="UniversalForward" }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 5.0

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            // ---- NTC data bindings ----
            // Texture2DArray + ByteAddressBuffer must be declared before the include,
            // because NtcInference.hlsl references the types (it takes them as params).
            Texture2DArray _NtcLatents;
            SamplerState sampler_NtcLatents;
            ByteAddressBuffer _NtcWeights;

            float4 _NtcColorMips[16];
            float4 _NtcWeightOffsets;
            float4 _NtcBiasOffsets;
            float4 _NtcScaleOffsets;
            float4 _NtcImageInfo;
            float  _NtcChannelColorSpaces;
            float  _ChannelOffset;
            float  _NtcColorSpaceOverride;

            // Pure-HLSL inference library (shared, no CG legacy).
            #include "NtcInference.hlsl"

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

            Varyings vert (Attributes v)
            {
                Varyings o;
                o.positionCS = TransformObjectToHClip(v.positionOS.xyz);
                o.uv = v.uv;
                return o;
            }

            // Assemble the NtcTextureSetConstants struct from the bound material params.
            // NtcInference.hlsl expects colorMips as int4 where .y/.z are the *bit pattern*
            // of the float positionLod/positionScale (it re-reads them via asfloat()).
            NtcTextureSetConstants BuildDesc()
            {
                NtcTextureSetConstants d;
                [unroll]
                for (int i = 0; i < NTC_MAX_MIPS; ++i)
                {
                    d.colorMips[i] = int4(
                        (int)_NtcColorMips[i].x,
                        asint(_NtcColorMips[i].y),
                        asint(_NtcColorMips[i].z),
                        0);
                }
                d.networkWeightOffsets = int4((int)_NtcWeightOffsets.x, (int)_NtcWeightOffsets.y, (int)_NtcWeightOffsets.z, (int)_NtcWeightOffsets.w);
                d.networkBiasOffsets   = int4((int)_NtcBiasOffsets.x,   (int)_NtcBiasOffsets.y,   (int)_NtcBiasOffsets.z,   (int)_NtcBiasOffsets.w);
                d.networkScaleOffsets  = int4((int)_NtcScaleOffsets.x,  (int)_NtcScaleOffsets.y,  (int)_NtcScaleOffsets.z,  (int)_NtcScaleOffsets.w);
                d.imageWidth  = (int)_NtcImageInfo.x;
                d.imageHeight = (int)_NtcImageInfo.y;
                d.imageMips   = (int)_NtcImageInfo.z;
                d.validChannelMask   = asuint(_NtcImageInfo.w);
                d.channelColorSpaces = asuint(_NtcChannelColorSpaces);
                return d;
            }

            half4 frag (Varyings i) : SV_Target
            {
                NtcTextureSetConstants desc = BuildDesc();

                int2 imageSize = int2(max(desc.imageWidth, 1), max(desc.imageHeight, 1));
                int2 texel = int2(frac(i.uv) * imageSize);
                // Vertical-flip correction: mesh UV follows Unity's standard convention
                // (V=0 = bottom of the visual image, same as any properly-imported texture),
                // but the latent grid / positional encoding inside NtcSampleTextureSet were
                // trained/packed against the RAW raster convention (row 0 = top of the
                // source PNG, same as ntc-cli's own pixel indexing). Flip here so `texel`
                // addresses the same physical row the network was trained on -- without
                // this, the decoded content is vertically mismatched against every other
                // mode (Original / on-Load), which reads as a shifted/misaligned texture.
                texel.y = imageSize.y - 1 - texel.y;

                float outputs[NTC_MLP_OUTPUT_CHANNELS];
                bool ok = NtcSampleTextureSet(
                    desc,
                    _NtcLatents,
                    sampler_NtcLatents,
                    _NtcWeights,
                    0u,
                    texel,
                    0,               // mip level 0
                    false,           // keep stored values, linearize manually below
                    outputs);

                if (!ok)
                    return half4(1, 0, 1, 1); // magenta = decode failed (bad constants)

                int c = (int)_ChannelOffset;
                // NTC stores BaseColor in its texture color space (sRGB for color channels).
                // URP shades in linear space, so linearize the 3 color channels using the
                // same conversion that NtcInference.hlsl exposes, avoiding URP-version-specific helpers.
                // Debug override (see NtcDecompressor.ColorSpaceMode): 0=Auto (per-channel metadata,
                // the correct/default path), 1=force sRGB decode regardless of metadata, 2=force raw
                // (skip linearization entirely, as if the network output were already linear).
                int csOverride = (int)_NtcColorSpaceOverride;
                float3 rgb;
                if (csOverride == 1)
                {
                    // Force sRGB decode on all 3 color channels regardless of container metadata,
                    // using the exact same piecewise formula as NtcConvertChannelToLinearColorSpace.
                    float3 s = saturate(float3(outputs[c + 0], outputs[c + 1], outputs[c + 2]));
                    rgb = float3(
                        s.x <= 0.04045 ? s.x / 12.92 : pow((s.x + 0.055) / 1.055, 2.4),
                        s.y <= 0.04045 ? s.y / 12.92 : pow((s.y + 0.055) / 1.055, 2.4),
                        s.z <= 0.04045 ? s.z / 12.92 : pow((s.z + 0.055) / 1.055, 2.4));
                }
                else if (csOverride == 2)
                {
                    // Force raw/no conversion: use the network's stored output directly as linear.
                    rgb = float3(saturate(outputs[c + 0]), saturate(outputs[c + 1]), saturate(outputs[c + 2]));
                }
                else
                {
                    rgb = float3(
                        NtcConvertChannelToLinearColorSpace(desc, c + 0, saturate(outputs[c + 0])),
                        NtcConvertChannelToLinearColorSpace(desc, c + 1, saturate(outputs[c + 1])),
                        NtcConvertChannelToLinearColorSpace(desc, c + 2, saturate(outputs[c + 2])));
                }
                return half4(rgb, 1);
            }
            ENDHLSL
        }
    }

    FallBack "Universal Render Pipeline/Unlit"
}
