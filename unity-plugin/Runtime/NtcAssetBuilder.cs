// NtcAssetBuilder.cs
// Runtime-side script that reads a .ntc file via NtcContainerReader and uploads
// latent data to a Texture2DArray + weights to a ComputeBuffer.
// This validates the "pure C# asset ingestion" path (no ntc-cli dependency at runtime).
// It also serves as the data source for a future Inference on Sample shader.
using System;
using System.IO;
using UnityEngine;
using Ntc.Packing;

namespace Ntc.Unity
{
    /// <summary>
    /// Built Unity assets from a .ntc container, ready for shader binding.
    /// </summary>
    public class NtcBuiltAssets
    {
        public Texture2DArray LatentTexture;
        public ComputeBuffer WeightBuffer;
        public NtcTextureSetConstants Constants;
        public string Report;
    }

    /// <summary>
    /// Mirrors the NtcTextureSetConstants struct from InferenceConstants.h (shader side).
    /// Packed as int4 arrays for constant buffer compatibility.
    /// </summary>
    public struct NtcTextureSetConstants
    {
        public Vector4Int[] colorMips;   // 16 entries (only first NumColorMips are used)
        public Vector4Int networkWeightOffsets;
        public Vector4Int networkBiasOffsets;
        public Vector4Int networkScaleOffsets;
        public int imageWidth;
        public int imageHeight;
        public int imageMips;
        public uint validChannelMask;
        public uint channelColorSpaces;
    }

    public struct Vector4Int
    {
        public int x, y, z, w;
        public Vector4Int(int x, int y, int z, int w) { this.x = x; this.y = y; this.z = z; this.w = w; }
    }

    /// <summary>
    /// Maps the flat NTC output channels (0..15) onto the concrete PBR maps that the
    /// compute decompressor writes (BaseColor / Normal / MetallicGloss / AO). Derived from
    /// the container's texture list by name, so it works for any .ntc baked from the
    /// Color/Normal/Metalness/Smoothness(or Roughness)/AO manifest convention.
    /// A channel index of -1 means "not present".
    /// </summary>
    public struct NtcPbrChannelLayout
    {
        public int ColorR, ColorG, ColorB;
        public bool ColorIsSrgb;
        public int NormalX, NormalY, NormalZ;
        public int Metal;
        public int Smooth;
        public bool SmoothIsRoughness;
        public int AO;
        // Alpha-test cutout mask channel (source: BaseMap's own alpha channel, baked as a
        // separate scalar NTC texture named "Alpha" / semantic "AlphaMask" -- see
        // NtcBakeWindow.AddAlphaCutoutFromBaseMap). -1 means the material has no cutout mask
        // baked (e.g. opaque materials), in which case the decompressor writes BaseColor.a=1
        // exactly like before this fix.
        public int Alpha;

        public bool HasColor  { get { return ColorR >= 0; } }
        public bool HasNormal { get { return NormalX >= 0; } }
        public bool HasMask   { get { return Metal >= 0 || Smooth >= 0; } }
        public bool HasAO     { get { return AO >= 0; } }
        public bool HasAlpha  { get { return Alpha >= 0; } }
    }

    public static class NtcAssetBuilder
    {
        // NTC network constants (from InferenceConstants.h, compile-time fixed)
        public const int NTC_MLP_FEATURES = 16;
        public const int NTC_FEATURES_PER_LAYER = 4;
        public const int NTC_MLP_LAYERS = 4;
        public const int NTC_MAX_NEURAL_MIPS = 8;

        /// <summary>
        /// Build Unity GPU assets from a parsed NtcContainer.
        /// Uses MLP version 0 (Int8 weights, the DP4A path).
        /// </summary>
        public static NtcBuiltAssets Build(NtcContainer container, int mlpVersion = 0)
        {
            NtcBuiltAssets result = new NtcBuiltAssets();
            try
            {
                NtcTextureSetDocument doc = container.Document;

                if (mlpVersion >= doc.MlpVersions.Count)
                {
                    Debug.LogError("[NTC] MLP version " + mlpVersion + " out of range (have " + doc.MlpVersions.Count + ")");
                    return result;
                }

                NtcMlp mlp = doc.MlpVersions[mlpVersion];

                // 1. Build the weight buffer: concatenate all layer weight/scale/bias bytes
                result.WeightBuffer = BuildWeightBuffer(container, mlp);

                // 2. Build latent Texture2DArray
                result.LatentTexture = BuildLatentTexture(container);

                // 3. Build the constants struct
                result.Constants = BuildConstants(container, mlp);

                // 4. Report
                using (StringWriter sw = new StringWriter())
                {
                    sw.WriteLine("=== NtcAssetBuilder Report ===");
                    sw.WriteLine("Image: {0}x{1}, {2} mips, {3} channels", doc.Width, doc.Height, doc.NumColorMips, doc.NumChannels);
                    sw.WriteLine("MLP version {0}: {1} layers, {2}, {3}", mlpVersion, mlp.Layers.Count, mlp.Activation, mlp.WeightLayout);
                    for (int i = 0; i < mlp.Layers.Count; i++)
                    {
                        NtcMlpLayer layer = mlp.Layers[i];
                        sw.WriteLine("  layer {0}: {1}->{2} weightType={3} (wv={4}, sv={5}, bv={6})",
                            i, layer.InputChannels, layer.OutputChannels, layer.WeightType,
                            layer.WeightView, layer.ScaleView, layer.BiasView);
                    }
                    if (result.LatentTexture != null)
                        sw.WriteLine("Latent texture: {0}x{1}, {2} layers (RGBA32 expanded from BGRA4)",
                            result.LatentTexture.width, result.LatentTexture.height, result.LatentTexture.depth);
                    if (result.WeightBuffer != null)
                        sw.WriteLine("Weight buffer: {0} bytes ({1} uints)", result.WeightBuffer.count * 4, result.WeightBuffer.count);
                    sw.WriteLine("Network weight offsets: ({0}, {1}, {2}, {3})",
                        result.Constants.networkWeightOffsets.x, result.Constants.networkWeightOffsets.y,
                        result.Constants.networkWeightOffsets.z, result.Constants.networkWeightOffsets.w);
                    result.Report = sw.ToString();
                }

                return result;
            }
            catch
            {
                if (result.WeightBuffer != null)
                {
                    result.WeightBuffer.Release();
                    result.WeightBuffer = null;
                }
                throw;
            }
        }

        private static ComputeBuffer BuildWeightBuffer(NtcContainer container, NtcMlp mlp)
        {
            // Concatenate all weight/scale/bias views into one byte array
            // Then upload as a Raw ComputeBuffer (ByteAddressBuffer on shader side)
            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter bw = new BinaryWriter(ms))
            {
                foreach (NtcMlpLayer layer in mlp.Layers)
                {
                    if (layer.WeightView >= 0)
                        bw.Write(container.GetViewBytes(layer.WeightView));
                    if (layer.ScaleView >= 0)
                        bw.Write(container.GetViewBytes(layer.ScaleView));
                    if (layer.BiasView >= 0)
                        bw.Write(container.GetViewBytes(layer.BiasView));
                }

                byte[] allBytes = ms.ToArray();
                // Pad to multiple of 4 (uint alignment for ByteAddressBuffer)
                int uintCount = (allBytes.Length + 3) / 4;
                uint[] uintData = new uint[uintCount];
                Buffer.BlockCopy(allBytes, 0, uintData, 0, allBytes.Length);

                ComputeBuffer cb = new ComputeBuffer(uintCount, sizeof(uint), ComputeBufferType.Raw);
                cb.SetData(uintData);
                return cb;
            }
        }

        private static Texture2DArray BuildLatentTexture(NtcContainer container)
        {
            NtcTextureSetDocument doc = container.Document;
            if (doc.Latents.Count == 0)
            {
                Debug.LogError("[NTC] No latent images in container");
                return null;
            }

            // NTC stores one latent image per neural mip. The shader samples neuralMip and
            // neuralMip + 1 with SampleLevel(), so the Unity Texture2DArray must contain the
            // real mip chain. Also, latent data is numeric data, not color: force linear reads.
            NtcLatentImage firstLatent = doc.Latents[0];
            int width = firstLatent.Width;
            int height = firstLatent.Height;
            int numLayers = firstLatent.LayerViews.Count;
            int mipCount = doc.Latents.Count;

            // NTC latent format is BGRA4 (4 bits per channel, 16 bits per texel).
            // Unity doesn't have a native BGRA4 format, so we use linear RGBA32 and
            // expand each 4-bit value to 8-bit (shader scales it back to INT8 inputs).
            Texture2DArray texArray = new Texture2DArray(width, height, numLayers,
                TextureFormat.RGBA32, mipCount > 1, true);
            texArray.wrapMode = TextureWrapMode.Repeat;
            texArray.filterMode = FilterMode.Bilinear;

            for (int mip = 0; mip < mipCount; mip++)
            {
                NtcLatentImage latent = doc.Latents[mip];
                int mipLayers = Math.Min(numLayers, latent.LayerViews.Count);
                int texelCount = latent.Width * latent.Height;

                for (int layer = 0; layer < mipLayers; layer++)
                {
                    int viewIndex = latent.LayerViews[layer];
                    byte[] rawBytes = container.GetViewBytes(viewIndex);
                    byte[] rgba32 = new byte[texelCount * 4];

                    for (int i = 0; i < texelCount && i * 2 + 1 < rawBytes.Length; i++)
                    {
                        byte b0 = rawBytes[i * 2];
                        byte b1 = rawBytes[i * 2 + 1];
                        int r4 = b0 & 0x0F;
                        int g4 = (b0 >> 4) & 0x0F;
                        int b4 = b1 & 0x0F;
                        int a4 = (b1 >> 4) & 0x0F;
                        rgba32[i * 4 + 0] = (byte)(r4 | (r4 << 4));
                        rgba32[i * 4 + 1] = (byte)(g4 | (g4 << 4));
                        rgba32[i * 4 + 2] = (byte)(b4 | (b4 << 4));
                        rgba32[i * 4 + 3] = (byte)(a4 | (a4 << 4));
                    }

                    texArray.SetPixelData(rgba32, mip, layer);
                }
            }
            texArray.Apply(false, true);

            return texArray;
        }

        private static NtcTextureSetConstants BuildConstants(NtcContainer container, NtcMlp mlp)
        {
            NtcTextureSetConstants c = new NtcTextureSetConstants();
            NtcTextureSetDocument doc = container.Document;

            c.colorMips = new Vector4Int[16];
            for (int i = 0; i < doc.ColorMips.Count && i < 16; i++)
            {
                NtcColorMip cm = doc.ColorMips[i];
                c.colorMips[i] = new Vector4Int(cm.LatentMip, FloatToInt(cm.PositionLod), FloatToInt(cm.PositionScale), 0);
            }
            for (int i = doc.ColorMips.Count; i < 16; i++)
                c.colorMips[i] = new Vector4Int(0, 0, 0, 0);

            // Compute byte offsets for each layer's weight/bias/scale in the concatenated buffer
            int offset = 0;
            int[] wOffsets = new int[4];
            int[] bOffsets = new int[4];
            int[] sOffsets = new int[4];
            for (int i = 0; i < mlp.Layers.Count && i < 4; i++)
            {
                NtcMlpLayer layer = mlp.Layers[i];
                wOffsets[i] = offset;
                if (layer.WeightView >= 0) offset += (int)doc.Views[layer.WeightView].StoredSize;
                sOffsets[i] = offset;
                if (layer.ScaleView >= 0) offset += (int)doc.Views[layer.ScaleView].StoredSize;
                bOffsets[i] = offset;
                if (layer.BiasView >= 0) offset += (int)doc.Views[layer.BiasView].StoredSize;
            }

            c.networkWeightOffsets = new Vector4Int(wOffsets[0], wOffsets[1], wOffsets[2], wOffsets[3]);
            c.networkBiasOffsets = new Vector4Int(bOffsets[0], bOffsets[1], bOffsets[2], bOffsets[3]);
            c.networkScaleOffsets = new Vector4Int(sOffsets[0], sOffsets[1], sOffsets[2], sOffsets[3]);

            c.imageWidth = doc.Width;
            c.imageHeight = doc.Height;
            c.imageMips = doc.NumColorMips;

            // Build validChannelMask and channelColorSpaces
            uint channelMask = 0;
            uint colorSpaces = 0;
            for (int i = 0; i < doc.Textures.Count; i++)
            {
                NtcTexture t = doc.Textures[i];
                for (int ch = t.FirstChannel; ch < t.FirstChannel + t.NumChannels; ch++)
                {
                    channelMask |= (1u << ch);
                    int cs = 0; // Linear
                    if (t.RgbColorSpace == "sRGB") cs = 1;
                    else if (t.RgbColorSpace == "HLG") cs = 2;
                    colorSpaces |= ((uint)cs << (ch * 2));
                }
            }
            c.validChannelMask = channelMask;
            c.channelColorSpaces = colorSpaces;

            return c;
        }

        private static int FloatToInt(float f)
        {
            byte[] b = BitConverter.GetBytes(f);
            return BitConverter.ToInt32(b, 0);
        }

        /// <summary>
        /// Derive the PBR channel layout from the container's texture list (matched by name).
        /// Recognizes Color/BaseColor/Albedo/Diffuse, Normal, Metal(ness), Smooth(ness)/Gloss,
        /// Rough(ness) (converted to smoothness in the shader), and AO/Occlusion.
        /// </summary>
        public static NtcPbrChannelLayout BuildPbrLayout(NtcContainer container)
        {
            var layout = new NtcPbrChannelLayout
            {
                ColorR = -1, ColorG = -1, ColorB = -1,
                NormalX = -1, NormalY = -1, NormalZ = -1,
                Metal = -1, Smooth = -1, AO = -1, Alpha = -1,
            };

            NtcTextureSetDocument doc = container.Document;
            foreach (NtcTexture t in doc.Textures)
            {
                string n = (t.Name ?? "").ToLowerInvariant();
                int c0 = t.FirstChannel;
                bool srgb = string.Equals(t.RgbColorSpace, "sRGB", StringComparison.OrdinalIgnoreCase);

                if (n.Contains("normal"))
                {
                    layout.NormalX = c0;
                    layout.NormalY = t.NumChannels > 1 ? c0 + 1 : -1;
                    layout.NormalZ = t.NumChannels > 2 ? c0 + 2 : -1;
                }
                else if (n.Contains("metal"))
                {
                    layout.Metal = c0;
                }
                else if (n.Contains("rough"))
                {
                    layout.Smooth = c0;
                    layout.SmoothIsRoughness = true;
                }
                else if (n.Contains("smooth") || n.Contains("gloss"))
                {
                    layout.Smooth = c0;
                    layout.SmoothIsRoughness = false;
                }
                else if (n.Contains("occl") || n == "ao" || n.Contains("ambientocclusion") || n.EndsWith("ao"))
                {
                    layout.AO = c0;
                }
                else if (n.Contains("alpha") || n.Contains("cutout") || n.Contains("opacity"))
                {
                    layout.Alpha = c0;
                }
                else if (n.Contains("color") || n.Contains("albedo") || n.Contains("diffuse") || n.Contains("basecolor"))
                {
                    layout.ColorR = c0;
                    layout.ColorG = t.NumChannels > 1 ? c0 + 1 : c0;
                    layout.ColorB = t.NumChannels > 2 ? c0 + 2 : c0;
                    layout.ColorIsSrgb = srgb;
                }
            }

            // Fallback: if no texture was named "color", assume the first 3 channels are color.
            if (layout.ColorR < 0 && doc.NumChannels >= 3)
            {
                layout.ColorR = 0; layout.ColorG = 1; layout.ColorB = 2;
                layout.ColorIsSrgb = true;
            }

            return layout;
        }
    }
}
