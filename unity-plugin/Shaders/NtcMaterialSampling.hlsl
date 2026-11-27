// A material-texture-set interface shared by the ordinary and neural backends.
// Neural fetch decodes one tuple, then semantic defaults are applied per material.
#ifndef NTC_MATERIAL_SAMPLING_INCLUDED
#define NTC_MATERIAL_SAMPLING_INCLUDED
#include "NtcInference.hlsl"

Texture2DArray _NtcLatents;
SamplerState sampler_NtcLatents;
ByteAddressBuffer _NtcWeights;
TEXTURE2D(_BaseMap); SAMPLER(sampler_BaseMap);
TEXTURE2D(_BumpMap); SAMPLER(sampler_BumpMap);
TEXTURE2D(_MetallicGlossMap); SAMPLER(sampler_MetallicGlossMap);
TEXTURE2D(_OcclusionMap); SAMPLER(sampler_OcclusionMap);

CBUFFER_START(UnityPerMaterial)
float4 _BaseMap_ST, _BaseColor, _EmissionColor;
float _Metallic, _Smoothness, _BumpScale, _OcclusionStrength;
float _Cutoff, _AlphaClip, _Cull, _Surface;
float _NtcBackend, _NtcDebugView, _NtcSmoothnessFromBaseAlpha, _NtcHasOriginalMask;
float _NtcHasOriginalNormal, _NtcHasOriginalAO, _NtcAOChannel;
float4 _NtcColorMips[16];
float4 _NtcWeightOffsets, _NtcScaleOffsets, _NtcBiasOffsets, _NtcImageInfo;
float4 _NtcColorChannels, _NtcNormalChannels, _NtcMaskChannels;
float4 _NtcSampler0, _NtcSampler1, _NtcSampler2, _NtcSampler3;
float4 _NtcMipBias;
CBUFFER_END

struct NtcMaterialSamples
{
    float4 baseColor;
    float3 normalTS;
    float metallic, smoothness, occlusion;
};

NtcTextureSetConstants NtcMaterialDescriptor()
{
    NtcTextureSetConstants d = (NtcTextureSetConstants)0;
    [unroll] for (int i = 0; i < 16; i++)
        d.colorMips[i] = int4((int)_NtcColorMips[i].x, asint(_NtcColorMips[i].y), asint(_NtcColorMips[i].z), 0);
    d.imageWidth = (int)_NtcImageInfo.x; d.imageHeight = (int)_NtcImageInfo.y; d.imageMips = (int)_NtcImageInfo.z;
    d.networkWeightOffsets = (int4)_NtcWeightOffsets;
    d.networkScaleOffsets = (int4)_NtcScaleOffsets;
    d.networkBiasOffsets = (int4)_NtcBiasOffsets;
    return d;
}

int NtcWrapTexel(int x, int size, int mode)
{
    if (mode == 1) return clamp(x, 0, size - 1);
    if (mode == 3) return clamp(x < 0 ? -x - 1 : x, 0, size - 1);
    if (mode == 2) { int r = (x % (size * 2) + size * 2) % (size * 2); return min(r, size * 2 - 1 - r); }
    return (x % size + size) % size;
}

// Like Texture.Load: one unfiltered texel, explicit mip and addressing.
void LoadMaterialTexel(NtcTextureSetConstants d, int2 texel, int mip, int2 wrap, out float values[16])
{
    int2 size = max(int2(d.imageWidth, d.imageHeight) >> mip, 1);
    texel = int2(NtcWrapTexel(texel.x, size.x, wrap.x), NtcWrapTexel(texel.y, size.y, wrap.y));
    texel.y = size.y - 1 - texel.y; // Unity UV -> raw raster convention of the trained model.
    NtcSampleTextureSet(d, _NtcLatents, sampler_NtcLatents, _NtcWeights, 0, texel, mip, false, values);
    [unroll] for (int c = 0; c < 16; c++) values[c] = saturate(values[c]);
    #if !defined(UNITY_COLORSPACE_GAMMA)
    if (_NtcColorChannels.x >= 0 && _NtcColorChannels.w != 0)
    {
        // Grayscale/two-channel colors can alias an output channel. Read all source
        // values before writing, so the same scalar is never linearized twice.
        float3 encoded = float3(values[(int)_NtcColorChannels.x],values[(int)_NtcColorChannels.y],values[(int)_NtcColorChannels.z]);
        [unroll] for (int c = 0; c < 3; c++)
        {
            int channel = (int)_NtcColorChannels[c];
            float v = encoded[c];
            values[channel] = v <= 0.04045 ? v / 12.92 : pow((v + 0.055) / 1.055, 2.4);
        }
    }
    #endif
}

// Deterministic filtering reference. Unlike a hardware Sample, each requested texel is an MLP.
// Matching map samplers reuse this complete tuple; differing map samplers decode separately.
void NtcSampleFiltered(NtcTextureSetConstants d, float2 uv, float2 dx, float2 dy, float4 settings, float mipBias, out float values[16])
{
    float2 size = float2(d.imageWidth, d.imageHeight);
    float lx = length(dx * size), ly = length(dy * size);
    float major = max(lx, ly), minor = min(lx, ly);
    int taps = settings.z < 0.5 ? 1 : (int)ceil(clamp(major / max(minor, 1e-8), 1.0, clamp(settings.w, 1.0, 16.0)));
    float lod = clamp(log2(max(max(major / taps, minor), 1e-8)) + mipBias, 0.0, float(d.imageMips - 1));
    int mip = settings.z > 1.5 ? int(floor(lod)) : int(floor(lod + 0.5));
    int levels = settings.z > 1.5 && mip + 1 < d.imageMips ? 2 : 1;
    int corners = settings.z < 0.5 ? 1 : 4;
    [unroll] for (int i = 0; i < 16; i++) values[i] = 0;
    [loop] for (int tap = 0; tap < taps; tap++)
    {
        float2 tapUV = uv + (lx >= ly ? dx : dy) * ((tap + 0.5) / taps - 0.5);
        [loop] for (int level = 0; level < levels; level++)
        {
            float mipWeight = levels == 1 ? 1.0 : (level == 0 ? 1.0 - frac(lod) : frac(lod));
            int2 dimensions = max(int2(d.imageWidth,d.imageHeight) >> (mip + level),1);
            float2 p = tapUV * dimensions - (corners == 1 ? 0.0 : 0.5);
            int2 lo = int2(floor(p)); float2 f = frac(p);
            [loop] for (int corner = 0; corner < corners; corner++)
            {
                int2 offset = int2(corner & 1,corner >> 1);
                float weight = mipWeight / taps;
                if (corners > 1) weight *= (offset.x == 0 ? 1-f.x : f.x) * (offset.y == 0 ? 1-f.y : f.y);
                if (weight <= 0) continue;
                // Keep ONE decoder call site: repeated inlining of the MLP made driver JIT
                // and live temporary storage grow with every filter/map combination.
                float sampleValues[16];
                LoadMaterialTexel(d,lo+offset,mip+level,(int2)settings.xy,sampleValues);
                [unroll] for (int i = 0; i < 16; i++) values[i] += sampleValues[i] * weight;
            }
        }
    }
}

float NtcSemantic(float values[16], int channel, float fallback) { if (channel < 0) return fallback; return values[channel]; }

NtcMaterialSamples SampleMaterialTextures(float2 uv, float2 dx, float2 dy)
{
    NtcMaterialSamples s;
    if (_NtcBackend < 0.5)
    {
        s.baseColor = SAMPLE_TEXTURE2D_GRAD(_BaseMap, sampler_BaseMap, uv, dx, dy);
        s.normalTS = _NtcHasOriginalNormal > 0.5 ? UnpackNormalScale(SAMPLE_TEXTURE2D_GRAD(_BumpMap, sampler_BumpMap, uv, dx, dy), _BumpScale) : float3(0,0,1);
        float4 mask = SAMPLE_TEXTURE2D_GRAD(_MetallicGlossMap, sampler_MetallicGlossMap, uv, dx, dy);
        s.metallic = _NtcHasOriginalMask > 0.5 ? mask.r : _Metallic;
        s.smoothness = (_NtcSmoothnessFromBaseAlpha > 0.5 ? s.baseColor.a : (_NtcHasOriginalMask > 0.5 ? mask.a : 1.0)) * _Smoothness;
        s.occlusion = _NtcHasOriginalAO > 0.5 ? lerp(1.0, SAMPLE_TEXTURE2D_GRAD(_OcclusionMap, sampler_OcclusionMap, uv, dx, dy).g, _OcclusionStrength) : 1.0;
    }
    else
    {
        NtcTextureSetConstants d = NtcMaterialDescriptor();
        s.baseColor = 1; s.normalTS = float3(0,0,1); s.metallic = _Metallic; s.smoothness = _Smoothness; s.occlusion = 1;
        float common[16]; [unroll] for (int i = 0; i < 16; i++) common[i] = 0;
        [loop] for (int role = 0; role < 4; role++)
        {
            if (role == 1 && _NtcNormalChannels.x < 0) continue;
            if (role == 2 && _NtcMaskChannels.x < 0 && _NtcMaskChannels.y < 0) continue;
            if (role == 3 && _NtcAOChannel < 0) continue;
            float4 settings = role == 0 ? _NtcSampler0 : (role == 1 ? _NtcSampler1 : (role == 2 ? _NtcSampler2 : _NtcSampler3));
            float value[16];
            if (role > 0 && all(settings == _NtcSampler0) && _NtcMipBias[role] == _NtcMipBias.x) { [unroll] for (int i = 0; i < 16; i++) value[i] = common[i]; }
            else NtcSampleFiltered(d,uv,dx,dy,settings,_NtcMipBias[role],value);
            if (role == 0)
            {
                [unroll] for (int i = 0; i < 16; i++) common[i] = value[i];
                s.baseColor = float4(NtcSemantic(value,(int)_NtcColorChannels.x,1),NtcSemantic(value,(int)_NtcColorChannels.y,1),
                    NtcSemantic(value,(int)_NtcColorChannels.z,1),NtcSemantic(value,(int)_NtcMaskChannels.w,1));
            }
            else if (role == 1) s.normalTS = UnpackNormalmapRGorAG(float4(value[(int)_NtcNormalChannels.x],NtcSemantic(value,(int)_NtcNormalChannels.y,0.5),1,1),_BumpScale);
            else if (role == 2)
            {
                s.metallic = NtcSemantic(value,(int)_NtcMaskChannels.x,_Metallic);
                float smooth = NtcSemantic(value,(int)_NtcMaskChannels.y,1);
                if (_NtcMaskChannels.y >= 0 && _NtcMaskChannels.z != 0) smooth = 1-smooth;
                s.smoothness = smooth * _Smoothness;
            }
            else s.occlusion = lerp(1.0,NtcSemantic(value,(int)_NtcAOChannel,1),_OcclusionStrength);
        }
    }
    // URP reserves texture alpha for gloss in this workflow; opacity comes from the tint.
    if (_NtcSmoothnessFromBaseAlpha > 0.5) { s.smoothness = s.baseColor.a * _Smoothness; s.baseColor.a = 1; }
    s.baseColor *= _BaseColor;
    return s;
}
#endif
