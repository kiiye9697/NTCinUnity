// NtcInference.hlsl
// Pure-HLSL NTC (Neural Texture Compression) "Inference on Sample" math library.
//
// ---------------------------------------------------------------------------------------
// ATTRIBUTION AND LICENSING -- READ BEFORE REDISTRIBUTING
//
// This software contains source code provided by NVIDIA Corporation.
//
// The inference math in this file is a port of Inference.hlsli from the NVIDIA RTXNTC SDK
// (https://github.com/NVIDIA-RTX/RTXNTC). NVIDIA distributes that file under the NVIDIA
// RTX SDKs License Agreement and marks it:
//     SPDX-License-Identifier: LicenseRef-NvidiaProprietary
//     Copyright (c) 2024, NVIDIA CORPORATION & AFFILIATES. All rights reserved.
//
// NVIDIA's license, not this repository's LICENSE, governs the NVIDIA-derived portions
// below. Obtain the SDK from NVIDIA and accept their terms before using or redistributing
// this file. See THIRD-PARTY-NOTICES.md at the repository root.
// ---------------------------------------------------------------------------------------
//
// This is plain HLSL -- no CG legacy (no CGPROGRAM/cginc conventions, no auto-included
// UnityCG). It is meant to be #include'd from inside an HLSLPROGRAM/HLSLINCLUDE block
// (Built-in RP or URP). The caller declares the resource bindings (Texture2DArray,
// SamplerState, ByteAddressBuffer) before including this file; everything here is pure
// arithmetic over those inputs.
//
// Network topology (fixed by the .ntc training): 48 -> 64 -> 48 -> 32 -> 16, Int8 weights,
// HGELUClamp activation, RowMajor layout (the DP4A / MLP version 0 path).

#ifndef NTC_INFERENCE_INCLUDED
#define NTC_INFERENCE_INCLUDED

// === Network constants ===
#ifndef NTC_MLP_LAYERS
#define NTC_MLP_LAYERS 4
#define NTC_MLP_FEATURES 16
#define NTC_FEATURES_PER_LAYER 4
#define NTC_MLP_POS_ENC_WAVES 3
#define NTC_MLP_INPUT_CHANNELS 48
#define NTC_MLP_HIDDEN0_CHANNELS 64
#define NTC_MLP_HIDDEN1_CHANNELS 48
#define NTC_MLP_HIDDEN2_CHANNELS 32
#define NTC_MLP_OUTPUT_CHANNELS 16
// Largest channel count across ALL layer transitions (48/64/48/32/16 -> max is 64).
// Every intermediate array (networkInputs/hidden0/hidden1/hidden2/networkOutputs and the
// macro's inputArray/outputArray parameters) MUST be declared with this single size so that
// HLSL's exact-array-size-match rule for function parameters holds at every call site.
// (Layer0's output needs 64 elements; sizing everything to 64 avoids out-of-bounds writes.)
#define NTC_MLP_MAX_CHANNELS 64
#define NTC_MAX_MIPS 16
#define NTC_HGELU_MAXVALUE 3
#define NTC_HGELU_INVSTEP 80
#define NTC_HGELU_BIAS -113
#define NTC_COLORSPACE_LINEAR 0
#define NTC_COLORSPACE_SRGB 1
#endif

static const float c_InputScale = 127.5;

// === Constant description of one texture set ===
struct NtcTextureSetConstants
{
    int4 colorMips[NTC_MAX_MIPS];   // .x = neuralMip, .y = asint(positionLod), .z = asint(positionScale)
    int4 networkWeightOffsets;      // per-layer byte offset into the weight buffer
    int4 networkBiasOffsets;
    int4 networkScaleOffsets;
    int  imageWidth;
    int  imageHeight;
    int  imageMips;
    uint validChannelMask;
    uint channelColorSpaces;
};

// === Helper functions ===
float4 UnpackInt8x4ToFloat4(uint packed)
{
    int i0 = (int)(packed & 0xFF);
    int i1 = (int)((packed >> 8) & 0xFF);
    int i2 = (int)((packed >> 16) & 0xFF);
    int i3 = (int)((packed >> 24) & 0xFF);
    i0 = (i0 > 127) ? (i0 - 256) : i0;
    i1 = (i1 > 127) ? (i1 - 256) : i1;
    i2 = (i2 > 127) ? (i2 - 256) : i2;
    i3 = (i3 > 127) ? (i3 - 256) : i3;
    return float4((float)i0, (float)i1, (float)i2, (float)i3);
}

float4 NtcHGELUClamp_ForwardFloat(float4 x)
{
    return min(x, float(NTC_HGELU_MAXVALUE)) * saturate(mad(x, 0.333333, 0.5));
}

float4 NtcEvaluatePositionalEncoding(float2 posf)
{
    float4 result;
    result.x = frac(posf.x) * 2 - 1;
    result.y = frac(posf.y) * 2 - 1;
    result.z = frac(posf.x + 0.25) * 2 - 1;
    result.w = frac(posf.y + 0.25) * 2 - 1;
    return result;
}

// === Latent grid sampling ===
void NtcSampleLatentGrid(
    Texture2DArray latentTexture,
    SamplerState latentSampler,
    float2 uv,
    int neuralLod,
    int featureOffset,
    inout float networkInputs[NTC_MLP_MAX_CHANNELS])
{
    int width, height, arraySize;
    latentTexture.GetDimensions(width, height, arraySize);

    [unroll]
    for (int layerIndex = 0; layerIndex < NTC_MLP_FEATURES / NTC_FEATURES_PER_LAYER; ++layerIndex)
    {
        const bool mask = (layerIndex == 0) || (layerIndex < arraySize);
        float4 sampledValue = latentTexture.SampleLevel(latentSampler, float3(uv, layerIndex), neuralLod);
        // NOTE: no .bgra unswizzle -- see the matching comment in NtcDecompress.compute. Our latent
        // Texture2DArray is a plain RGBA32 built by NtcAssetBuilder with nibbles already in physical
        // (r=nibble0, g=nibble1, b=nibble2, a=nibble3) order, so re-applying .bgra (needed only when
        // sampling a real hardware BGRA4_UNORM texture) would incorrectly swap nibble0<->nibble2.
        sampledValue = mask ? floor(sampledValue * (2.0 * c_InputScale) - c_InputScale - 0.5) : 0.0;

        networkInputs[featureOffset + layerIndex * 4 + 0] = sampledValue.x;
        networkInputs[featureOffset + layerIndex * 4 + 1] = sampledValue.y;
        networkInputs[featureOffset + layerIndex * 4 + 2] = sampledValue.z;
        networkInputs[featureOffset + layerIndex * 4 + 3] = sampledValue.w;
    }
}

void NtcEncodeSamplePosition(
    float2 posf, float lod, int featureOffset,
    inout float networkInputs[NTC_MLP_MAX_CHANNELS])
{
    int idx = featureOffset;
    [unroll]
    for (int wave = 0; wave < NTC_MLP_POS_ENC_WAVES; ++wave)
    {
        float4 enc = NtcEvaluatePositionalEncoding(posf);
        int4 quantized = int4(enc * c_InputScale);
        networkInputs[idx + 0] = quantized.x;
        networkInputs[idx + 1] = quantized.y;
        networkInputs[idx + 2] = quantized.z;
        networkInputs[idx + 3] = quantized.w;
        idx += 4;
        posf *= 2.0;
    }
    networkInputs[idx + 0] = int(lod * c_InputScale);
    networkInputs[idx + 1] = networkInputs[idx + 0];
    networkInputs[idx + 2] = 0.0;
    networkInputs[idx + 3] = 0.0;
}

// === Evaluate one MLP layer (Float version) ===
// One specialized function is generated per layer transition via this macro so that
// IN_CH / OUT_CH are genuine preprocessor literals (not runtime parameters). fxc cannot
// unroll a loop whose trip count is a runtime function argument, so the inner fixed-array
// loop must have a compile-time-constant bound. The outer `col` loop stays a plain dynamic
// [loop] (ByteAddressBuffer.Load supports runtime addressing natively).
#define NTC_DEFINE_LAYER_EVAL(FuncName, IN_CH, OUT_CH)                                   \
void FuncName(                                                                            \
    ByteAddressBuffer weightBuffer,                                                       \
    int weightOffset,                                                                     \
    int biasOffset,                                                                       \
    int scaleOffset,                                                                      \
    bool isOutputLayer,                                                                   \
    float inputArray[NTC_MLP_MAX_CHANNELS],                                               \
    out float outputArray[NTC_MLP_MAX_CHANNELS])                                          \
{                                                                                         \
    [loop]                                                                                \
    for (int col = 0; col < (OUT_CH); col += 4)                                           \
    {                                                                                     \
        uint4 biasRaw = weightBuffer.Load4(biasOffset + col * 4);                         \
        int4 bias = asint(biasRaw);                                                       \
        float4 acc = float4((float)bias.x, (float)bias.y, (float)bias.z, (float)bias.w);  \
        [unroll]                                                                          \
        for (int row4 = 0; row4 < (IN_CH) / 4; row4++)                                    \
        {                                                                                 \
            uint w0 = weightBuffer.Load(weightOffset + (col + 0) * (IN_CH) + row4 * 4);   \
            uint w1 = weightBuffer.Load(weightOffset + (col + 1) * (IN_CH) + row4 * 4);   \
            uint w2 = weightBuffer.Load(weightOffset + (col + 2) * (IN_CH) + row4 * 4);   \
            uint w3 = weightBuffer.Load(weightOffset + (col + 3) * (IN_CH) + row4 * 4);   \
            float4 inVec = float4(                                                        \
                inputArray[row4 * 4 + 0],                                                 \
                inputArray[row4 * 4 + 1],                                                 \
                inputArray[row4 * 4 + 2],                                                 \
                inputArray[row4 * 4 + 3]);                                                \
            float4 w0f = UnpackInt8x4ToFloat4(w0);                                        \
            float4 w1f = UnpackInt8x4ToFloat4(w1);                                        \
            float4 w2f = UnpackInt8x4ToFloat4(w2);                                        \
            float4 w3f = UnpackInt8x4ToFloat4(w3);                                        \
            acc.x += dot(inVec, w0f);                                                     \
            acc.y += dot(inVec, w1f);                                                     \
            acc.z += dot(inVec, w2f);                                                     \
            acc.w += dot(inVec, w3f);                                                     \
        }                                                                                 \
        uint4 scalesRaw = weightBuffer.Load4(scaleOffset + col * 4);                      \
        float4 scales = asfloat(scalesRaw);                                               \
        float4 results = acc * scales;                                                    \
        if (isOutputLayer)                                                                \
        {                                                                                 \
            outputArray[col + 0] = results.x;                                             \
            outputArray[col + 1] = results.y;                                             \
            outputArray[col + 2] = results.z;                                             \
            outputArray[col + 3] = results.w;                                             \
        }                                                                                 \
        else                                                                              \
        {                                                                                 \
            results = round(NtcHGELUClamp_ForwardFloat(results) * float(NTC_HGELU_INVSTEP)) \
                + float(NTC_HGELU_BIAS);                                                 \
            outputArray[col + 0] = results.x;                                             \
            outputArray[col + 1] = results.y;                                             \
            outputArray[col + 2] = results.z;                                             \
            outputArray[col + 3] = results.w;                                             \
        }                                                                                 \
    }                                                                                     \
}

// One specialized function per MLP layer transition (48 -> 64 -> 48 -> 32 -> 16).
NTC_DEFINE_LAYER_EVAL(NtcEvaluateLayer0, NTC_MLP_INPUT_CHANNELS, NTC_MLP_HIDDEN0_CHANNELS)
NTC_DEFINE_LAYER_EVAL(NtcEvaluateLayer1, NTC_MLP_HIDDEN0_CHANNELS, NTC_MLP_HIDDEN1_CHANNELS)
NTC_DEFINE_LAYER_EVAL(NtcEvaluateLayer2, NTC_MLP_HIDDEN1_CHANNELS, NTC_MLP_HIDDEN2_CHANNELS)
NTC_DEFINE_LAYER_EVAL(NtcEvaluateLayer3, NTC_MLP_HIDDEN2_CHANNELS, NTC_MLP_OUTPUT_CHANNELS)

// === Color space conversion ===
float NtcConvertChannelToLinearColorSpace(NtcTextureSetConstants desc, int channel, float storedValue)
{
    int colorSpace = (desc.channelColorSpaces >> (channel * 2)) & 3;
    if (colorSpace == NTC_COLORSPACE_SRGB)
    {
        return storedValue <= 0.04045 ? storedValue / 12.92 : pow((storedValue + 0.055) / 1.055, 2.4);
    }
    return storedValue;
}

// === Prepare network inputs ===
bool NtcPrepareNetworkInputs(
    NtcTextureSetConstants desc,
    Texture2DArray latentTexture,
    SamplerState latentSampler,
    int2 texel,
    int mipLevel,
    out float networkInputs[NTC_MLP_MAX_CHANNELS])
{
    [unroll]
    for (int i = 0; i < NTC_MLP_MAX_CHANNELS; ++i)
        networkInputs[i] = 0.0;

    int4 cm = desc.colorMips[mipLevel];
    int neuralMip = cm.x;
    float positionLod = asfloat(cm.y);
    float positionScale = asfloat(cm.z);

    if (neuralMip < 0)
        return false;

    int2 imageSize = max(int2(desc.imageWidth, desc.imageHeight) >> mipLevel, 1);
    float2 uv = (float2(texel) + 0.5) / imageSize;

    NtcSampleLatentGrid(latentTexture, latentSampler, uv, neuralMip, 0, networkInputs);
    NtcSampleLatentGrid(latentTexture, latentSampler, uv, neuralMip + 1, NTC_MLP_FEATURES, networkInputs);

    float2 posf = float2(texel) * positionScale;
    NtcEncodeSamplePosition(posf, positionLod, NTC_MLP_FEATURES * 2, networkInputs);

    return true;
}

// === Main entry point ===
bool NtcSampleTextureSet(
    NtcTextureSetConstants desc,
    Texture2DArray latentTexture,
    SamplerState latentSampler,
    ByteAddressBuffer weightsBuffer,
    uint weightsOffset,
    int2 texel,
    int mipLevel,
    bool convertToLinearColorSpace,
    out float outputs[NTC_MLP_OUTPUT_CHANNELS])
{
    float networkInputs[NTC_MLP_MAX_CHANNELS];
    if (!NtcPrepareNetworkInputs(desc, latentTexture, latentSampler, texel, mipLevel, networkInputs))
        return false;

    float hidden0[NTC_MLP_MAX_CHANNELS];
    NtcEvaluateLayer0(weightsBuffer,
        weightsOffset + desc.networkWeightOffsets.x,
        weightsOffset + desc.networkBiasOffsets.x,
        weightsOffset + desc.networkScaleOffsets.x,
        false,
        networkInputs, hidden0);

    float hidden1[NTC_MLP_MAX_CHANNELS];
    NtcEvaluateLayer1(weightsBuffer,
        weightsOffset + desc.networkWeightOffsets.y,
        weightsOffset + desc.networkBiasOffsets.y,
        weightsOffset + desc.networkScaleOffsets.y,
        false,
        hidden0, hidden1);

#if NTC_MLP_LAYERS >= 4
    float hidden2[NTC_MLP_MAX_CHANNELS];
    NtcEvaluateLayer2(weightsBuffer,
        weightsOffset + desc.networkWeightOffsets.z,
        weightsOffset + desc.networkBiasOffsets.z,
        weightsOffset + desc.networkScaleOffsets.z,
        false,
        hidden1, hidden2);

    float networkOutputs[NTC_MLP_MAX_CHANNELS];
    NtcEvaluateLayer3(weightsBuffer,
        weightsOffset + desc.networkWeightOffsets.w,
        weightsOffset + desc.networkBiasOffsets.w,
        weightsOffset + desc.networkScaleOffsets.w,
        true,
        hidden2, networkOutputs);
#else
    float networkOutputs[NTC_MLP_MAX_CHANNELS];
    NtcEvaluateLayer2(weightsBuffer,
        weightsOffset + desc.networkWeightOffsets.z,
        weightsOffset + desc.networkBiasOffsets.z,
        weightsOffset + desc.networkScaleOffsets.z,
        true,
        hidden1, networkOutputs);
#endif

    [unroll]
    for (int ch = 0; ch < NTC_MLP_OUTPUT_CHANNELS; ++ch)
    {
        outputs[ch] = networkOutputs[ch];
        if (convertToLinearColorSpace)
            outputs[ch] = NtcConvertChannelToLinearColorSpace(desc, ch, outputs[ch]);
    }

    return true;
}

#endif // NTC_INFERENCE_INCLUDED
