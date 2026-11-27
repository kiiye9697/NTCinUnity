using System;
using System.IO;
using Ntc.Packing;
using Ntc.Unity;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Ntc.Tests
{
    public class NtcDirectMaterialTests
    {
        [TestCase(0, FilterMode.Point, "default")]
        [TestCase(1, FilterMode.Point, "default")]
        [TestCase(2, FilterMode.Point, "default")]
        [TestCase(3, FilterMode.Point, "default")]
        [TestCase(4, FilterMode.Point, "default")]
        [TestCase(5, FilterMode.Point, "default")]
        [TestCase(1, FilterMode.Bilinear, "default")]
        [TestCase(1, FilterMode.Trilinear, "default")]
        [TestCase(1, FilterMode.Trilinear, "mipBias")]
        [TestCase(1, FilterMode.Trilinear, "mipBiasMagnification")]
        [TestCase(1, FilterMode.Point, "grayscaleColor")]
        [TestCase(0, FilterMode.Point, "albedoAlphaSmoothness")]
        [TestCase(0, FilterMode.Point, "disabledTextureFeatures")]
        [TestCase(0, FilterMode.Point, "disabledLightingFeatures")]
        public void RealFragmentMaterial_MatchesDecodedTextureBackend(int view, FilterMode filter, string workflow)
        {
            if (!SystemInfo.supportsComputeShaders || SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                Assert.Ignore("A real GPU is required.");
            string corpus = Environment.GetEnvironmentVariable("NTC_TEST_DATA_DIR");
            if (string.IsNullOrEmpty(corpus)) Assert.Ignore("Set NTC_TEST_DATA_DIR.");
            bool previousAsync = ShaderUtil.allowAsyncCompilation;
            string previousPipelineTag = Shader.globalRenderPipeline;
            Shader.globalRenderPipeline = "UniversalPipeline";
            ShaderUtil.allowAsyncCompilation = false;
            var c = NtcContainerReader.Load(Path.Combine(corpus, "Bricks.ntc"));
            var assets = NtcAssetBuilder.Build(c);
            var layout = NtcAssetBuilder.BuildPbrLayout(c);
            if (workflow == "grayscaleColor") layout.ColorG = layout.ColorB = layout.ColorR;
            var settings = ScriptableObject.CreateInstance<NtcTextureSetAsset>();
            var template = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            var direct = new Material(Shader.Find("NTC/LitDirect_URP"));
            Material reference = null;
            Texture2D[] textures = new Texture2D[4];
            NtcDecompressor decompressor = null;
            try
            {
                template.SetFloat("_Metallic", 0.4f);
                template.SetFloat("_Smoothness", 0.73f);
                template.SetFloat("_BumpScale", 0.8f);
                template.SetFloat("_OcclusionStrength", 0.6f);
                template.EnableKeyword("_NORMALMAP"); template.EnableKeyword("_METALLICSPECGLOSSMAP"); template.EnableKeyword("_OCCLUSIONMAP");
                if (workflow == "albedoAlphaSmoothness")
                {
                    layout.Alpha = layout.Smooth;
                    template.EnableKeyword("_SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A"); template.EnableKeyword("_ALPHATEST_ON");
                    template.SetFloat("_SmoothnessTextureChannel",1); template.SetFloat("_AlphaClip",1); template.SetFloat("_Cutoff",0.99f);
                }
                if (workflow == "disabledTextureFeatures") { template.DisableKeyword("_NORMALMAP"); template.DisableKeyword("_METALLICSPECGLOSSMAP"); template.DisableKeyword("_OCCLUSIONMAP"); }
                if (workflow == "disabledLightingFeatures")
                    foreach (string keyword in new[] { "_RECEIVE_SHADOWS_OFF", "_SPECULARHIGHLIGHTS_OFF", "_ENVIRONMENTREFLECTIONS_OFF" }) template.EnableKeyword(keyword);
                settings.samplers = new NtcSamplerSettings[4];
                string[] properties = { "_BaseMap", "_BumpMap", "_MetallicGlossMap", "_OcclusionMap" };
                for (int i = 0; i < 4; i++) settings.samplers[i] = new NtcSamplerSettings
                { property = properties[i], filter = filter, anisotropy = 1, wrapU = TextureWrapMode.Mirror, wrapV = TextureWrapMode.Clamp, scale = Vector2.one,
                    mipMapBias = workflow.StartsWith("mipBias") ? 0.75f : 0f };
                NtcMaterialSampling.Bind(direct, template, assets, layout, settings);
                direct.SetFloat("_NtcDebugView", view);
                direct.SetFloat("_Cull", 0); // The offscreen test projection may flip winding.
                direct.SetColor("_EmissionColor", new Color(0.02f, 0.02f, 0.02f));
                reference = new Material(direct);
                reference.SetFloat("_NtcBackend", 0);
                reference.SetFloat("_NtcHasOriginalNormal", template.IsKeywordEnabled("_NORMALMAP") ? 1 : 0);
                reference.SetFloat("_NtcHasOriginalMask", template.IsKeywordEnabled("_METALLICSPECGLOSSMAP") ? 1 : 0);
                reference.SetFloat("_NtcHasOriginalAO", template.IsKeywordEnabled("_OCCLUSIONMAP") ? 1 : 0);
                const int baseMip = 7;
                int width = Mathf.Max(c.Document.Width >> baseMip, 1), height = Mathf.Max(c.Document.Height >> baseMip, 1);
                decompressor = NtcDecompressor.Create(null);
                for (int i = 0; i < 4; i++) textures[i] = new Texture2D(width, height, TextureFormat.RGBAFloat, true, true)
                { filterMode = filter, wrapModeU = TextureWrapMode.Mirror, wrapModeV = TextureWrapMode.Clamp, anisoLevel = 1 };
                for (int mip = 0; mip < textures[0].mipmapCount; mip++)
                {
                    int sourceMip = baseMip + mip;
                    var targets = decompressor.AllocateTargets(Mathf.Max(width >> mip, 1), Mathf.Max(height >> mip, 1), layout, false);
                    try
                    {
                        decompressor.DecompressFull(assets, layout, targets, sourceMip);
                        var maps = new[] { targets.BaseColor, targets.Normal, targets.Mask, targets.AO };
                        for (int i = 0; i < 4; i++)
                            if (maps[i] != null) textures[i].SetPixels(Read(maps[i]), mip);
                    }
                    finally { targets.Release(); }
                }
                for (int i = 0; i < 4; i++) { textures[i].mipMapBias = workflow.StartsWith("mipBias") ? 0.75f : 0f; textures[i].Apply(false, true); reference.SetTexture(properties[i], textures[i]); }
                float range = workflow == "mipBiasMagnification" ? 0.5f : (filter == FilterMode.Trilinear ? Mathf.Sqrt(2) : 1);
                // Noninteger UV offset exercises bilinear taps and negative-U mirror addressing.
                var mesh = Quad(-0.19f, 0.03f, range);
                Color[] actual = null, expected = null;
                try
                {
                    actual = Draw(mesh, direct, width, height);
                    expected = Draw(mesh, reference, width, height);
                }
                finally { UnityEngine.Object.DestroyImmediate(mesh); }
                double sum = 0; float max = 0, signal = 0;
                for (int i = 0; i < actual.Length; i++)
                {
                    foreach (float error in new[] { Mathf.Abs(actual[i].r - expected[i].r), Mathf.Abs(actual[i].g - expected[i].g), Mathf.Abs(actual[i].b - expected[i].b) })
                    { max = Mathf.Max(max, error); sum += error; }
                    signal = Mathf.Max(signal, actual[i].maxColorComponent);
                }
                double mean = sum / (actual.Length * 3);
                TestContext.WriteLine("Fragment view=" + view + " filter=" + filter + " max=" + max + " mean=" + mean);
                Assert.That(Array.Exists(actual, pixel => pixel.a > 0.5f), Is.True,
                    "The mesh must actually render; an all-black metallic channel is valid.");
                Assert.That(ShaderUtil.ShaderHasError(direct.shader), Is.False);
                Assert.That(mean, Is.LessThan(0.015), "Actual fragment material must match its decoded-texture reference.");
                Assert.That(max, Is.LessThan(0.08));
                if (view == 0)
                {
                    var stock = new Material(template);
                    var stockMesh = Quad(-0.19f, 0.03f, range);
                    try
                    {
                        for (int i = 0; i < properties.Length; i++) stock.SetTexture(properties[i], textures[i]);
                        stock.SetColor("_EmissionColor",new Color(0.02f,0.02f,0.02f));
                        stock.SetFloat("_SrcBlend",1); stock.SetFloat("_DstBlend",0); stock.SetFloat("_ZWrite",1);
                        stock.SetFloat("_Cull",0);
                        stock.EnableKeyword("_EMISSION");
                        foreach (string keyword in new[] { "_NORMALMAP", "_METALLICSPECGLOSSMAP", "_OCCLUSIONMAP", "_RECEIVE_SHADOWS_OFF", "_SPECULARHIGHLIGHTS_OFF", "_ENVIRONMENTREFLECTIONS_OFF", "_ALPHATEST_ON", "_SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A" })
                            if (template.IsKeywordEnabled(keyword)) stock.EnableKeyword(keyword); else stock.DisableKeyword(keyword);
                        Color[] stockPixels = Draw(stockMesh, stock, width, height);
                        TestContext.WriteLine("Stock first=" + stockPixels[0] + " direct first=" + actual[0]);
                        Assert.That(Array.Exists(stockPixels,pixel=>pixel.a>0.5f),Is.True,"Stock URP reference must draw.");
                        double stockError = 0;
                        for (int i = 0; i < stockPixels.Length; i++)
                            stockError += Math.Abs(actual[i].r-stockPixels[i].r) + Math.Abs(actual[i].g-stockPixels[i].g) + Math.Abs(actual[i].b-stockPixels[i].b);
                        stockError /= stockPixels.Length * 3;
                        TestContext.WriteLine("Stock URP Lit mean RGB error=" + stockError);
                        Assert.That(stockError, Is.LessThan(0.015));
                    }
                    finally { UnityEngine.Object.DestroyImmediate(stock); UnityEngine.Object.DestroyImmediate(stockMesh); }
                }
                string output = Environment.GetEnvironmentVariable("NTC_TEST_CAPTURE_DIR");
                if (!string.IsNullOrEmpty(output))
                {
                    Directory.CreateDirectory(output);
                    Save(actual, width, height, Path.Combine(output, "direct-view" + view + "-" + filter + ".png"));
                    Save(expected, width, height, Path.Combine(output, "decoded-view" + view + "-" + filter + ".png"));
                }
            }
            finally
            {
                ShaderUtil.allowAsyncCompilation = previousAsync;
                Shader.globalRenderPipeline = previousPipelineTag;
                assets.Release();
                if (decompressor != null) decompressor.Dispose();
                foreach (var texture in textures) if (texture != null) UnityEngine.Object.DestroyImmediate(texture);
                UnityEngine.Object.DestroyImmediate(reference); UnityEngine.Object.DestroyImmediate(direct);
                UnityEngine.Object.DestroyImmediate(template); UnityEngine.Object.DestroyImmediate(settings);
            }
        }

        static Mesh Quad(float u, float v, float range)
        {
            var mesh = new Mesh();
            mesh.vertices = new[] { new Vector3(-1,-1,0), new Vector3(1,-1,0), new Vector3(-1,1,0), new Vector3(1,1,0) };
            mesh.normals = new[] { Vector3.back, Vector3.back, Vector3.back, Vector3.back };
            mesh.tangents = new[] { new Vector4(1,0,0,-1), new Vector4(1,0,0,-1), new Vector4(1,0,0,-1), new Vector4(1,0,0,-1) };
            mesh.uv = new[] { new Vector2(u,v), new Vector2(u+range,v), new Vector2(u,v+range), new Vector2(u+range,v+range) };
            mesh.triangles = new[] { 0,2,1, 2,3,1 }; return mesh;
        }

        static Color[] Draw(Mesh mesh, Material material, int width, int height)
        {
            var target = new RenderTexture(width, height, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
            target.Create();
            var command = new CommandBuffer();
            string[] globals = { "_WorldSpaceCameraPos", "_MainLightPosition", "_MainLightColor", "unity_LightData" };
            var saved = new Vector4[globals.Length];
            for (int i = 0; i < globals.Length; i++) saved[i] = Shader.GetGlobalVector(globals[i]);
            try
            {
                command.SetRenderTarget(target);
                command.ClearRenderTarget(true, true, Color.clear);
                command.SetViewProjectionMatrices(Matrix4x4.identity, GL.GetGPUProjectionMatrix(Matrix4x4.Ortho(-1,1,-1,1,-1,1), true));
                command.SetGlobalVector("_WorldSpaceCameraPos", new Vector4(0,0,-2,1));
                command.SetGlobalVector("_MainLightPosition", new Vector4(0,0,-1,0));
                command.SetGlobalVector("_MainLightColor", Vector4.one);
                command.SetGlobalVector("unity_LightData", new Vector4(0,0,1,0));
                int pass = material.FindPass("ForwardLit");
                Assert.That(pass, Is.GreaterThanOrEqualTo(0),material.shader.name + " forward pass must be selected.");
                command.DrawMesh(mesh, Matrix4x4.identity, material, 0, pass);
                Graphics.ExecuteCommandBuffer(command);
                return Read(target);
            }
            finally
            {
                for (int i = 0; i < globals.Length; i++) Shader.SetGlobalVector(globals[i],saved[i]);
                command.Release(); target.Release(); UnityEngine.Object.DestroyImmediate(target);
            }
        }

        static Color[] Read(RenderTexture target)
        {
            RenderTexture previous = RenderTexture.active;
            var copy = new Texture2D(target.width, target.height, TextureFormat.RGBAFloat, false, true);
            try { RenderTexture.active = target; copy.ReadPixels(new Rect(0,0,target.width,target.height),0,0); copy.Apply(); return copy.GetPixels(); }
            finally { RenderTexture.active = previous; UnityEngine.Object.DestroyImmediate(copy); }
        }

        static void Save(Color[] pixels, int width, int height, string path)
        {
            var copy = new Texture2D(width,height,TextureFormat.RGBA32,false);
            for (int i = 0; i < pixels.Length; i++) pixels[i] = pixels[i].gamma;
            copy.SetPixels(pixels); copy.Apply(); File.WriteAllBytes(path, copy.EncodeToPNG()); UnityEngine.Object.DestroyImmediate(copy);
        }
    }
}
