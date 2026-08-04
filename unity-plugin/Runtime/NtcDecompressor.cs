// NtcDecompressor.cs
// Thin C# wrapper around NtcDecompress.compute. Binds a material's NTC GPU assets
// (latent Texture2DArray + INT8 weight ByteAddressBuffer + constants) and dispatches
// the decompression kernel into ordinary PBR RenderTextures.
//
// Two dispatch styles, both driven by the SAME compute kernel:
//   * DecompressFull   : whole image at one color mip -> used by mode2 (on-Load) at load time.
//   * DecompressRegion : an arbitrary pixel rectangle -> used by mode3 (on-Sample) tile streaming.
using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace Ntc.Unity
{
    /// <summary>
    /// The set of decompressed PBR RenderTextures for one material (BaseColor/Normal/Mask/AO).
    /// BaseColor is stored LINEAR (the shader linearizes sRGB channels on write), so all four
    /// use a plain linear UNORM format -- no sRGB read/write ambiguity on UAV stores.
    /// </summary>
    public class NtcDecompressTargets
    {
        public RenderTexture BaseColor;   // rgb = linear albedo
        public RenderTexture Normal;      // r = nx, g = ny (URP UnpackNormalmapRGorAG)
        public RenderTexture Mask;        // r = metallic, a = smoothness
        public RenderTexture AO;          // g = ambient occlusion
        public int Width;
        public int Height;
        public int ColorMip;              // which .ntc color mip this cache was decoded from

        public long VramBytes
        {
            get
            {
                long b = 0;
                b += RtBytes(BaseColor);
                b += RtBytes(Normal);
                b += RtBytes(Mask);
                b += RtBytes(AO);
                return b;
            }
        }

        static long RtBytes(RenderTexture rt)
        {
            if (rt == null) return 0;
            long baseB = (long)rt.width * rt.height * 4;
            return rt.useMipMap ? (long)(baseB * 1.3333) : baseB;
        }

        public void Release()
        {
            ReleaseRt(ref BaseColor);
            ReleaseRt(ref Normal);
            ReleaseRt(ref Mask);
            ReleaseRt(ref AO);
        }

        static void ReleaseRt(ref RenderTexture rt)
        {
            if (rt == null) return;
            rt.Release();
            UnityEngine.Object.Destroy(rt);
            rt = null;
        }
    }

    public class NtcDecompressor : IDisposable
    {
        /// <summary>
        /// Debug-only override for the BaseColor sRGB->linear conversion normally driven by the
        /// .ntc container's per-texture `rgbColorSpace` metadata (see NtcAssetBuilder.BuildPbrLayout).
        /// Lets you A/B-test whether a perceived brightness mismatch vs the "Original" material is
        /// actually caused by color-space handling: Auto = trust the container flag (default,
        /// verified correct against real baked assets); ForceSrgbDecode = always linearize the
        /// color channels as if they were sRGB-encoded; ForceRawLinear = never linearize, i.e. treat
        /// the raw MLP output as already-linear (this is what you'd see if the sRGB conversion were
        /// missing -- washed-out/too-bright midtones and lifted shadows compared to Original).
        /// Affects both on-Load (DecompressFull) and on-Sample tile streaming (DecompressRegion),
        /// since both go through this same compute kernel.
        /// </summary>
        public enum ColorSpaceMode { Auto = 0, ForceSrgbDecode = 1, ForceRawLinear = 2 }
        public static ColorSpaceMode DebugColorSpaceMode = ColorSpaceMode.Auto;

        readonly ComputeShader _cs;
        readonly int _kernel;
        RenderTexture[] _dummies; // one per output slot (Unity requires every UAV be bound)

        public bool IsValid { get { return _cs != null && _kernel >= 0; } }
        public static bool Dp4aHardwareSupported
        {
            get
            {
#if UNITY_2020_1_OR_NEWER
                return SystemInfo.supportsComputeShaders &&
                       (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Direct3D12 ||
                        SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Vulkan);
#else
                return false;
#endif
            }
        }

        public NtcDecompressor(ComputeShader cs)
        {
            _cs = cs;
            _kernel = (cs != null) ? cs.FindKernel("NtcDecompressMain") : -1;
            if (_cs != null && _kernel >= 0)
            {
                _dummies = new RenderTexture[4];
                for (int i = 0; i < 4; i++)
                {
                    var d = new RenderTexture(1, 1, 0, GraphicsFormat.R8G8B8A8_UNorm) { enableRandomWrite = true };
                    d.Create();
                    _dummies[i] = d;
                }
            }
        }

        /// <summary>Load the compute shader from Resources ("NtcDecompress") unless one is supplied.</summary>
        public static NtcDecompressor Create(ComputeShader supplied)
        {
            ComputeShader cs = supplied != null ? supplied : Resources.Load<ComputeShader>("NtcDecompress");
            if (cs == null)
            {
                Debug.LogError("[NTC] NtcDecompress.compute not found (expected under a Resources folder or wired in the Inspector).");
                return null;
            }
            return new NtcDecompressor(cs);
        }

        public static RenderTexture CreateTarget(int w, int h, bool mips, GraphicsFormat format = GraphicsFormat.R8G8B8A8_UNorm)
        {
            var rt = new RenderTexture(w, h, 0, format)
            {
                enableRandomWrite = true,
                useMipMap = mips,
                autoGenerateMips = false,
                wrapMode = TextureWrapMode.Repeat,
                filterMode = FilterMode.Bilinear,
            };
            rt.Create();
            return rt;
        }

        // Mask (metallic/smoothness) gets 16 bits per channel instead of the 8 bits used for
        // Color/Normal/AO. Rationale: 8-bit UNORM quantization error is only ~0.4% (1/255), which
        // is far smaller than the ~6-18% smoothness fidelity loss measured 2026-07-10 (see
        // PROGRESS.md) -- so 8-bit output is NOT the main cause of that loss (the shared-bpp-budget
        // / lossFunctionScale issue is). But smoothness feeds a perceptually-remapped, squared curve
        // (roughness = (1-smoothness)^2 in BRDF.hlsl) that amplifies low-end quantization steps into
        // visible specular banding, and today's source *_Smoothness.tif assets are 8-bit anyway --
        // this is cheap headroom for when higher-bit-depth source art is used, or once the MLP loss
        // weight fix (lossFunctionScale) closes most of the gap, so residual 8-bit banding does not
        // become the next visible artifact.
        static readonly GraphicsFormat kMaskFormat = GraphicsFormat.R16G16B16A16_UNorm;

        /// <summary>Allocate the PBR RenderTextures needed for a given channel layout.</summary>
        public NtcDecompressTargets AllocateTargets(int width, int height, NtcPbrChannelLayout layout, bool mips)
        {
            var t = new NtcDecompressTargets { Width = width, Height = height };
            if (layout.HasColor)  t.BaseColor = CreateTarget(width, height, mips);
            if (layout.HasNormal) t.Normal    = CreateTarget(width, height, mips);
            if (layout.HasMask)   t.Mask      = CreateTarget(width, height, mips,
                SystemInfo.IsFormatSupported(kMaskFormat, FormatUsage.LoadStore) ? kMaskFormat : GraphicsFormat.R8G8B8A8_UNorm);
            if (layout.HasAO)     t.AO        = CreateTarget(width, height, mips);
            return t;
        }

        /// <summary>Decompress the whole image at one color mip into freshly matching-size targets.</summary>
        public void DecompressFull(NtcBuiltAssets assets, NtcPbrChannelLayout layout,
                                   NtcDecompressTargets targets, int colorMip)
        {
            int mipW = Mathf.Max(assets.Constants.imageWidth >> colorMip, 1);
            int mipH = Mathf.Max(assets.Constants.imageHeight >> colorMip, 1);
            DecompressRegion(assets, layout, targets, colorMip,
                new RectInt(0, 0, mipW, mipH), Vector2Int.zero);
        }

        /// <summary>
        /// Decompress a pixel rectangle (in the color mip's coordinate space) into the targets,
        /// writing to dstOffset. Only this rectangle is touched -- the rest of the cache is left
        /// as-is, which is what makes tile streaming possible.
        /// </summary>
        public void DecompressRegion(NtcBuiltAssets assets, NtcPbrChannelLayout layout,
                                     NtcDecompressTargets targets, int colorMip,
                                     RectInt srcRect, Vector2Int dstTopLeft)
        {
            if (!IsValid || assets == null || assets.LatentTexture == null || assets.WeightBuffer == null)
                return;

            int mipW = Mathf.Max(assets.Constants.imageWidth >> colorMip, 1);
            int mipH = Mathf.Max(assets.Constants.imageHeight >> colorMip, 1);

            Vector4Int cm = assets.Constants.colorMips[Mathf.Clamp(colorMip, 0, 15)];
            _cs.SetInts("_NtcColorMip", cm.x, cm.y, cm.z, 0);
            _cs.SetInts("_NtcWeightOffsets", V(assets.Constants.networkWeightOffsets));
            _cs.SetInts("_NtcBiasOffsets",   V(assets.Constants.networkBiasOffsets));
            _cs.SetInts("_NtcScaleOffsets",  V(assets.Constants.networkScaleOffsets));
            _cs.SetInts("_MipImageSize", mipW, mipH, 0, 0);
            _cs.SetInts("_SrcRect", srcRect.xMin, srcRect.yMin, srcRect.xMax, srcRect.yMax);
            _cs.SetInts("_DstOffset", dstTopLeft.x - srcRect.xMin, dstTopLeft.y - srcRect.yMin, 0, 0);

            bool colorIsSrgb = layout.ColorIsSrgb;
            if (DebugColorSpaceMode == ColorSpaceMode.ForceSrgbDecode) colorIsSrgb = true;
            else if (DebugColorSpaceMode == ColorSpaceMode.ForceRawLinear) colorIsSrgb = false;
            _cs.SetInts("_ColorChannels", layout.ColorR, layout.ColorG, layout.ColorB, colorIsSrgb ? 1 : 0);
            _cs.SetInts("_NormalChannels", layout.NormalX, layout.NormalY, layout.NormalZ, 0);
            _cs.SetInts("_MaskChannels", layout.Metal, layout.Smooth, layout.SmoothIsRoughness ? 1 : 0, 0);
            _cs.SetInt("_AOChannel", layout.AO);
            _cs.SetInt("_AlphaChannel", layout.HasAlpha ? layout.Alpha : -1);
            _cs.SetInts("_OutputEnable",
                (targets.BaseColor != null && layout.HasColor) ? 1 : 0,
                (targets.Normal != null && layout.HasNormal) ? 1 : 0,
                (targets.Mask != null && layout.HasMask) ? 1 : 0,
                (targets.AO != null && layout.HasAO) ? 1 : 0);

            _cs.SetTexture(_kernel, "_Latents", assets.LatentTexture);
            _cs.SetBuffer(_kernel, "_Weights", assets.WeightBuffer);
            _cs.SetTexture(_kernel, "_OutBaseColor", targets.BaseColor != null ? targets.BaseColor : _dummies[0]);
            _cs.SetTexture(_kernel, "_OutNormal",    targets.Normal    != null ? targets.Normal    : _dummies[1]);
            _cs.SetTexture(_kernel, "_OutMask",      targets.Mask      != null ? targets.Mask      : _dummies[2]);
            _cs.SetTexture(_kernel, "_OutAO",        targets.AO        != null ? targets.AO        : _dummies[3]);

            int gx = Mathf.Max((srcRect.width + 15) / 16, 1);
            int gy = Mathf.Max((srcRect.height + 7) / 8, 1);
            _cs.Dispatch(_kernel, gx, gy, 1);
        }

        public static void GenerateMips(NtcDecompressTargets t)
        {
            if (t == null) return;
            if (t.BaseColor != null && t.BaseColor.useMipMap) t.BaseColor.GenerateMips();
            if (t.Normal != null && t.Normal.useMipMap) t.Normal.GenerateMips();
            if (t.Mask != null && t.Mask.useMipMap) t.Mask.GenerateMips();
            if (t.AO != null && t.AO.useMipMap) t.AO.GenerateMips();
        }

        static int[] V(Vector4Int v) { return new[] { v.x, v.y, v.z, v.w }; }

        public void Dispose()
        {
            if (_dummies != null)
            {
                for (int i = 0; i < _dummies.Length; i++)
                {
                    if (_dummies[i] == null) continue;
                    _dummies[i].Release();
                    UnityEngine.Object.Destroy(_dummies[i]);
                    _dummies[i] = null;
                }
                _dummies = null;
            }
        }
    }
}
