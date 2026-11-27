using System;
using System.IO;
using UnityEngine;

namespace Ntc.Unity
{
    [Serializable]
    public struct NtcSamplerSettings
    {
        public string property;
        public TextureWrapMode wrapU, wrapV;
        public FilterMode filter;
        public float mipMapBias;
        [Range(1, 16)] public int anisotropy;
        public Vector2 scale, offset;

        public Vector4 ShaderValue => new Vector4((int)wrapU, (int)wrapV, (int)filter, Mathf.Clamp(anisotropy, 1, 16));
        public static NtcSamplerSettings Capture(Material material, string property)
        {
            Texture texture = material != null && material.HasProperty(property) ? material.GetTexture(property) : null;
            return new NtcSamplerSettings
            {
                property = property, wrapU = texture != null ? texture.wrapModeU : TextureWrapMode.Repeat,
                wrapV = texture != null ? texture.wrapModeV : TextureWrapMode.Repeat,
                filter = texture != null ? texture.filterMode : FilterMode.Bilinear,
                anisotropy = texture != null ? Mathf.Max(texture.anisoLevel, 1) : 1,
                mipMapBias = texture != null ? texture.mipMapBias : 0f,
                scale = material != null && material.HasProperty(property) ? material.GetTextureScale(property) : Vector2.one,
                offset = material != null && material.HasProperty(property) ? material.GetTextureOffset(property) : Vector2.zero
            };
        }
    }

    [CreateAssetMenu(menuName = "NTC/Texture Set Binding")]
    public sealed class NtcTextureSetAsset : ScriptableObject
    {
        public string stableId;
        public string directUnsupportedReason;
#if UNITY_EDITOR
        // Editor-only association: keeping this in a Player would pin every original texture.
        public Material editorSourceMaterial;
        public string editorDataPath;
#endif
        public string streamingPath;
        public string originalResourcePath;
        public Material texturelessTemplate;
        public Material directTemplate; // Persistent material also retains required local shader variants in builds.
        public NtcSamplerSettings[] samplers = Array.Empty<NtcSamplerSettings>();

        public string ResolveDataPath()
        {
#if UNITY_EDITOR
            if (!string.IsNullOrEmpty(editorDataPath)) return Path.GetFullPath(editorDataPath);
#endif
            return Path.IsPathRooted(streamingPath) ? streamingPath : Path.Combine(Application.streamingAssetsPath, streamingPath ?? "");
        }

        public NtcSamplerSettings Sampler(string property)
        {
            foreach (var settings in samplers) if (settings.property == property) return settings;
            return new NtcSamplerSettings { property = property, scale = Vector2.one, filter = FilterMode.Bilinear, anisotropy = 1 };
        }
    }

    [Serializable]
    public struct NtcRendererSlot
    {
        public Renderer renderer;
        public int materialIndex;
    }
}
