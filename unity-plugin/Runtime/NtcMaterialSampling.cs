using System;
using UnityEngine;

namespace Ntc.Unity
{
    // The material interface owns semantic defaults. The neural decoder only returns channels.
    public static class NtcMaterialSampling
    {
        public static void ValidateUrpMaterial(Material source)
        {
            if (source == null || source.shader == null || source.shader.name != "Universal Render Pipeline/Lit")
                throw new NotSupportedException("Direct NTC currently requires a URP Lit material.");
            if ((source.HasProperty("_Surface") && source.GetFloat("_Surface") > 0.5f)
                || source.IsKeywordEnabled("_SPECULAR_SETUP"))
                throw new NotSupportedException("Direct NTC supports opaque/alpha-clip metallic URP Lit; transparent/specular materials stay original.");
        }

        public static void Bind(Material destination, Material template, NtcBuiltAssets assets,
            NtcPbrChannelLayout layout, NtcTextureSetAsset binding)
        {
            if (binding != null && !string.IsNullOrEmpty(binding.directUnsupportedReason))
                throw new NotSupportedException(binding.directUnsupportedReason);
            if (assets == null || assets.LatentTexture == null || assets.WeightBuffer == null)
                throw new ArgumentException("Direct NTC requires live latent and weight resources.");
            ValidateChannels(template, layout);
            bool needsAlpha = (template.HasProperty("_AlphaClip") && template.GetFloat("_AlphaClip") > 0.5f)
                || template.IsKeywordEnabled("_ALPHATEST_ON")
                || template.IsKeywordEnabled("_SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A")
                || (template.HasProperty("_SmoothnessTextureChannel") && template.GetFloat("_SmoothnessTextureChannel") > 0.5f);
            if (needsAlpha && !layout.HasAlpha) throw new NotSupportedException("Rebake this material with its BaseMap alpha before direct NTC.");
            CopyScalar(template, destination, "_Metallic", 0f);
            CopyScalar(template, destination, "_Smoothness", 0.5f);
            CopyScalar(template, destination, "_BumpScale", 1f);
            CopyScalar(template, destination, "_OcclusionStrength", 1f);
            CopyScalar(template, destination, "_Cutoff", 0.5f);
            destination.SetFloat("_AlphaClip", template.IsKeywordEnabled("_ALPHATEST_ON")
                || (template.HasProperty("_AlphaClip") && template.GetFloat("_AlphaClip") > 0.5f) ? 1f : 0f);
            CopyScalar(template, destination, "_Cull", 2f);
            destination.SetColor("_BaseColor", template.HasProperty("_BaseColor") ? template.GetColor("_BaseColor") : Color.white);
            destination.SetColor("_EmissionColor", template.IsKeywordEnabled("_EMISSION") && template.HasProperty("_EmissionColor") ? template.GetColor("_EmissionColor") : Color.black);
            destination.SetFloat("_NtcSmoothnessFromBaseAlpha", template.IsKeywordEnabled("_SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A")
                || (template.HasProperty("_SmoothnessTextureChannel") && template.GetFloat("_SmoothnessTextureChannel") > 0.5f) ? 1f : 0f);
            destination.SetFloat("_NtcBackend", 1f);
            foreach (string keyword in new[] { "_RECEIVE_SHADOWS_OFF", "_SPECULARHIGHLIGHTS_OFF", "_ENVIRONMENTREFLECTIONS_OFF" })
                if (template.IsKeywordEnabled(keyword)) destination.EnableKeyword(keyword); else destination.DisableKeyword(keyword);
            destination.renderQueue = template.renderQueue;
            destination.enableInstancing = template.enableInstancing;
            destination.SetTextureScale("_BaseMap", template.GetTextureScale("_BaseMap"));
            destination.SetTextureOffset("_BaseMap", template.GetTextureOffset("_BaseMap"));
            destination.SetTexture("_NtcLatents", assets.LatentTexture);
            destination.SetBuffer("_NtcWeights", assets.WeightBuffer);
            var c = assets.Constants;
            var mips = new Vector4[16];
            for (int i = 0; i < 16; i++)
                mips[i] = new Vector4(c.colorMips[i].x, Bits(c.colorMips[i].y), Bits(c.colorMips[i].z), 0);
            destination.SetVectorArray("_NtcColorMips", mips);
            destination.SetVector("_NtcWeightOffsets", Vector(c.networkWeightOffsets));
            destination.SetVector("_NtcScaleOffsets", Vector(c.networkScaleOffsets));
            destination.SetVector("_NtcBiasOffsets", Vector(c.networkBiasOffsets));
            destination.SetVector("_NtcImageInfo", new Vector4(c.imageWidth, c.imageHeight, c.imageMips, 0));
            destination.SetVector("_NtcColorChannels", new Vector4(layout.ColorR, layout.ColorG, layout.ColorB, layout.ColorIsSrgb ? 1 : 0));
            bool useNormal = template.IsKeywordEnabled("_NORMALMAP"), useMask = template.IsKeywordEnabled("_METALLICSPECGLOSSMAP");
            destination.SetVector("_NtcNormalChannels", useNormal ? new Vector4(layout.NormalX, layout.NormalY, layout.NormalZ, 0) : new Vector4(-1,-1,-1,0));
            destination.SetVector("_NtcMaskChannels", new Vector4(useMask ? layout.Metal : -1, useMask ? layout.Smooth : -1, layout.SmoothIsRoughness ? 1 : 0, layout.Alpha));
            destination.SetFloat("_NtcAOChannel", template.IsKeywordEnabled("_OCCLUSIONMAP") ? layout.AO : -1);
            string[] properties = { "_BaseMap", "_BumpMap", "_MetallicGlossMap", "_OcclusionMap" };
            var biases = Vector4.zero;
            for (int i = 0; i < properties.Length; i++)
            {
                var settings = binding != null ? binding.Sampler(properties[i]) : NtcSamplerSettings.Capture(template, properties[i]);
                destination.SetVector("_NtcSampler" + i, settings.ShaderValue);
                biases[i] = settings.mipMapBias;
            }
            destination.SetVector("_NtcMipBias", biases);
        }

        public static void ValidateChannels(Material source, NtcPbrChannelLayout layout, bool requiresColor = false)
        {
            if (requiresColor && !layout.HasColor)
                throw new NotSupportedException("Rebake this material with its BaseMap color before direct NTC.");
            if (source.IsKeywordEnabled("_NORMALMAP") && (!layout.HasNormal || layout.NormalY < 0))
                throw new NotSupportedException("Rebake this material with its enabled normal map before direct NTC.");
            bool baseAlpha = source.IsKeywordEnabled("_SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A")
                || (source.HasProperty("_SmoothnessTextureChannel") && source.GetFloat("_SmoothnessTextureChannel") > 0.5f);
            if (source.IsKeywordEnabled("_METALLICSPECGLOSSMAP") && (layout.Metal < 0 || (!baseAlpha && layout.Smooth < 0)))
                throw new NotSupportedException("Rebake this material with its enabled metallic/smoothness map before direct NTC.");
            if (source.IsKeywordEnabled("_OCCLUSIONMAP") && layout.AO < 0)
                throw new NotSupportedException("Rebake this material with its enabled occlusion map before direct NTC.");
        }

        static void CopyScalar(Material source, Material destination, string name, float fallback)
            => destination.SetFloat(name, source.HasProperty(name) ? source.GetFloat(name) : fallback);
        static float Bits(int value) => BitConverter.ToSingle(BitConverter.GetBytes(value), 0);
        static Vector4 Vector(Vector4Int value) => new Vector4(value.x, value.y, value.z, value.w);
    }
}
