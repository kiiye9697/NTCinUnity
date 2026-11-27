using System;
using System.IO;
using NUnit.Framework;
using Ntc.Packing;
using Ntc.Unity;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Ntc.Tests
{
    public class NtcInferenceParityTests
    {
        [TestCase("Bricks", 4)]
        [TestCase("Bricks", 7)]
        [TestCase("Flagpoles", 4)]
        [TestCase("Flagpoles", 7)]
        public void PortableInference_MatchesComputeOnRealModel(string name, int mip)
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null || !SystemInfo.supportsComputeShaders)
                Assert.Ignore("Run Unity with a graphics device (without -nographics) for GPU parity.");
            string directory = Environment.GetEnvironmentVariable("NTC_TEST_DATA_DIR");
            if (string.IsNullOrEmpty(directory) || !File.Exists(Path.Combine(directory, name + ".ntc")))
                Assert.Ignore("Set NTC_TEST_DATA_DIR to the real NTC corpus.");
            var shader = AssetDatabase.LoadAssetAtPath<ComputeShader>("Packages/com.kiiye.ntc-unity/Tests/Editor/NtcInferenceParity.compute");
            if (shader == null)
            {
                string[] guids = AssetDatabase.FindAssets("NtcInferenceParity t:ComputeShader");
                Assert.That(guids, Is.Not.Empty);
                shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(AssetDatabase.GUIDToAssetPath(guids[0]));
            }
            NtcBuiltAssets assets = null;
            NtcDecompressor decompressor = null;
            NtcDecompressTargets targets = null;
            RenderTexture portable = null;
            var previousColorSpaceMode = NtcDecompressor.DebugColorSpaceMode;
            try
            {
                var container = NtcContainerReader.Load(Path.Combine(directory, name + ".ntc"));
                assets = NtcAssetBuilder.Build(container);
                var layout = NtcAssetBuilder.BuildPbrLayout(container);
                int width = Math.Max(container.Document.Width >> mip, 1);
                int height = Math.Max(container.Document.Height >> mip, 1);
                decompressor = NtcDecompressor.Create(null);
                Assert.That(decompressor, Is.Not.Null);
                Assert.That(decompressor.IsValid, Is.True, "Packed inference kernel must compile and be supported.");
                NtcDecompressor.DebugColorSpaceMode = NtcDecompressor.ColorSpaceMode.Auto;
                targets = decompressor.AllocateTargets(width, height, layout, false);
                decompressor.DecompressFull(assets, layout, targets, mip);

                portable = NtcDecompressor.CreateTarget(width, height, false);
                int kernel = shader.FindKernel("CompareInference");
                Assert.That(shader.IsSupported(kernel), Is.True, "Portable inference kernel must compile and be supported.");
                var c = assets.Constants;
                var cm = c.colorMips[mip];
                shader.SetTexture(kernel, "_Latents", assets.LatentTexture);
                shader.SetBuffer(kernel, "_Weights", assets.WeightBuffer);
                shader.SetTexture(kernel, "_Output", portable);
                shader.SetInts("_ImageInfo", c.imageWidth, c.imageHeight, c.imageMips, 0);
                shader.SetInts("_ColorMip", cm.x, cm.y, cm.z, 0);
                shader.SetInts("_WeightOffsets", Values(c.networkWeightOffsets));
                shader.SetInts("_BiasOffsets", Values(c.networkBiasOffsets));
                shader.SetInts("_ScaleOffsets", Values(c.networkScaleOffsets));
                shader.SetInts("_Channels", layout.ColorR, layout.ColorG, layout.ColorB, layout.ColorIsSrgb && QualitySettings.activeColorSpace == ColorSpace.Linear ? 1 : 0);
                shader.SetInt("_Mip", mip);
                shader.Dispatch(kernel, (width + 7) / 8, (height + 7) / 8, 1);
                Color32[] reference = Read(targets.BaseColor);
                Color32[] actual = Read(portable);
                TestContext.WriteLine("First reference=" + reference[0] + " portable=" + actual[0]
                    + " middle reference=" + reference[reference.Length / 2] + " portable=" + actual[actual.Length / 2]);
                int maxError = 0;
                for (int i = 0; i < actual.Length; i++)
                {
                    maxError = Math.Max(maxError, Math.Abs(reference[i].r - actual[i].r));
                    maxError = Math.Max(maxError, Math.Abs(reference[i].g - actual[i].g));
                    maxError = Math.Max(maxError, Math.Abs(reference[i].b - actual[i].b));
                }
                TestContext.WriteLine(name + " mip=" + mip + " max RGB byte error=" + maxError);
                Assert.That(maxError, Is.LessThanOrEqualTo(2), "Portable and packed INT8 inference diverged.");
            }
            finally
            {
                NtcDecompressor.DebugColorSpaceMode = previousColorSpaceMode;
                if (portable != null) { portable.Release(); UnityEngine.Object.DestroyImmediate(portable); }
                if (targets != null) targets.Release();
                if (decompressor != null) decompressor.Dispose();
                if (assets != null) assets.Release();
            }
        }

        static int[] Values(Ntc.Unity.Vector4Int v) => new[] { v.x, v.y, v.z, v.w };

        static Color32[] Read(RenderTexture target)
        {
            RenderTexture previous = RenderTexture.active;
            var texture = new Texture2D(target.width, target.height, TextureFormat.RGBA32, false, true);
            try
            {
                RenderTexture.active = target;
                texture.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0);
                texture.Apply();
                return texture.GetPixels32();
            }
            finally
            {
                RenderTexture.active = previous;
                UnityEngine.Object.DestroyImmediate(texture);
            }
        }
    }
}
