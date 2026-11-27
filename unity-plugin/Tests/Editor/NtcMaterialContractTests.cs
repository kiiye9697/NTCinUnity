using System;
using System.Collections;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using Ntc.Packing;
using Ntc.Unity;
using Ntc.UnityEditorTools;
using UnityEditor;
using UnityEngine;

namespace Ntc.Tests
{
    public class NtcMaterialContractTests
    {
        [TestCase("_NORMALMAP")]
        [TestCase("_METALLICSPECGLOSSMAP")]
        [TestCase("_OCCLUSIONMAP")]
        [TestCase("BaseMap")]
        public void DirectIncompleteChannels_RejectEnabledSourceFeatures(string feature)
        {
            var source = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            try
            {
                if (feature != "BaseMap") source.EnableKeyword(feature);
                var missing = new NtcPbrChannelLayout { ColorR = -1, NormalX = -1, Metal = -1, Smooth = -1, AO = -1 };
                Assert.Throws<NotSupportedException>(() => NtcMaterialSampling.ValidateChannels(source, missing, feature == "BaseMap"));
                source.DisableKeyword(feature);
                Assert.DoesNotThrow(() => NtcMaterialSampling.ValidateChannels(source, missing));
            }
            finally { UnityEngine.Object.DestroyImmediate(source); }
        }

        [Test]
        public void NormalOnlyContainer_DoesNotReplaceBaseColor()
        {
            var container = new NtcContainer { Document = new NtcTextureSetDocument { NumChannels = 3 } };
            container.Document.Textures.Add(new NtcTexture { Name = "Normal", FirstChannel = 0, NumChannels = 3 });
            NtcPbrChannelLayout layout = NtcAssetBuilder.BuildPbrLayout(container);
            Assert.That(layout.HasNormal, Is.True);
            Assert.That(layout.HasColor, Is.False, "Normal channels must not also be interpreted as sRGB albedo.");
        }

        [Test]
        public void ComputeBinding_PreservesSourceNormalAndSmoothnessMultipliers()
        {
            var host = new GameObject("NTC binding test");
            host.SetActive(false);
            var source = new Material(Shader.Find("Hidden/NTC/MaterialContractTest"));
            source.SetFloat("_BumpScale", 1.7f);
            source.SetFloat("_Smoothness", 0.23f);
            var destination = new Material(source);
            var targets = new NtcDecompressTargets
            {
                Normal = new RenderTexture(1, 1, 0),
                Mask = new RenderTexture(1, 1, 0)
            };
            try
            {
                var controller = host.AddComponent<NtcSceneController>();
                var layout = new NtcPbrChannelLayout { Metal = 0, Smooth = 1 };
                typeof(NtcSceneController).GetMethod("BindPbrTargetsToMaterial", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(controller, new object[] { destination, source, targets, layout });
                Assert.That(destination.GetFloat("_BumpScale"), Is.EqualTo(1.7f));
                Assert.That(destination.GetFloat("_Smoothness"), Is.EqualTo(0.23f));
            }
            finally
            {
                targets.Release();
                UnityEngine.Object.DestroyImmediate(destination);
                UnityEngine.Object.DestroyImmediate(source);
                UnityEngine.Object.DestroyImmediate(host);
            }
        }

        [Test]
        public void BakeOcclusionProperty_UsesGreenChannel()
        {
            string path = "Assets/NtcAoContract_" + Guid.NewGuid().ToString("N") + ".png";
            var pixels = new Texture2D(1, 1);
            pixels.SetPixel(0, 0, new Color(0.1f, 0.8f, 0.3f, 1f));
            File.WriteAllBytes(path, pixels.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(pixels);
            AssetDatabase.ImportAsset(path);
            var material = new Material(Shader.Find("Hidden/NTC/MaterialContractTest"));
            var window = ScriptableObject.CreateInstance<NtcBakeWindow>();
            try
            {
                material.SetTexture("_OcclusionMap", AssetDatabase.LoadAssetAtPath<Texture2D>(path));
                Type entryType = typeof(NtcBakeWindow).GetNestedType("MaterialEntry", BindingFlags.NonPublic);
                object entry = Activator.CreateInstance(entryType, true);
                typeof(NtcBakeWindow).GetMethod("CollectTextures", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(window, new[] { (object)material, entry });
                var textures = (IList)entryType.GetField("textures").GetValue(entry);
                Assert.That(textures.Count, Is.EqualTo(1));
                object texture = textures[0];
                Assert.That(texture.GetType().GetField("channelSwizzle").GetValue(texture), Is.EqualTo("G"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(window);
                UnityEngine.Object.DestroyImmediate(material);
                AssetDatabase.DeleteAsset(path);
            }
        }

        [TestCase("transparent")]
        [TestCase("albedoAlphaKeyword")]
        [TestCase("albedoAlphaProperty")]
        public void IncompleteBake_PreservesSourceOpacityAndPackedMask(string alphaUse)
        {
            var host = new GameObject("NTC partial material test");
            host.SetActive(false);
            var source = new Material(Shader.Find("Hidden/NTC/MaterialContractTest"));
            var original = new Texture2D(1, 1);
            source.SetTexture("_BaseMap", original);
            source.SetTexture("_MetallicGlossMap", original);
            source.EnableKeyword("_METALLICSPECGLOSSMAP");
            if (alphaUse == "transparent") source.SetFloat("_Surface", 1f);
            if (alphaUse == "albedoAlphaKeyword") source.EnableKeyword("_SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A");
            if (alphaUse == "albedoAlphaProperty") source.SetFloat("_SmoothnessTextureChannel", 1f);
            source.SetFloat("_Smoothness", 0.23f);
            var destination = new Material(source);
            var targets = new NtcDecompressTargets { BaseColor = new RenderTexture(1, 1, 0), Mask = new RenderTexture(1, 1, 0) };
            try
            {
                var controller = host.AddComponent<NtcSceneController>();
                var layout = new NtcPbrChannelLayout { Metal = 0, Smooth = -1, Alpha = -1 };
                typeof(NtcSceneController).GetMethod("BindPbrTargetsToMaterial", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(controller, new object[] { destination, source, targets, layout });
                Assert.That(destination.GetTexture("_BaseMap"), Is.SameAs(original), "Do not replace source opacity with alpha=1.");
                Assert.That(destination.GetTexture("_MetallicGlossMap"), Is.SameAs(original), "Do not replace source smoothness with zero.");
                Assert.That(destination.IsKeywordEnabled("_METALLICSPECGLOSSMAP"), Is.True);
                Assert.That(destination.GetFloat("_Smoothness"), Is.EqualTo(0.23f));
                Assert.That(destination.IsKeywordEnabled("_SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A"),
                    Is.EqualTo(source.IsKeywordEnabled("_SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A")));
            }
            finally
            {
                targets.Release();
                UnityEngine.Object.DestroyImmediate(destination);
                UnityEngine.Object.DestroyImmediate(source);
                UnityEngine.Object.DestroyImmediate(original);
                UnityEngine.Object.DestroyImmediate(host);
            }
        }

        [Test]
        public void UrpShaders_HaveNoImportErrors()
        {
            if (UnityEditor.PackageManager.PackageInfo.FindForAssetPath("Packages/com.unity.render-pipelines.universal") == null)
                Assert.Ignore("Install URP in the test host to validate its shader imports.");
            foreach (string name in new[] { "NTC/InferenceOnSample_URP", "NTC/PBRChannelDebug_URP", "NTC/LitDirect_URP" })
            {
                Shader shader = Shader.Find(name);
                Assert.That(shader, Is.Not.Null, name);
                Assert.That(ShaderUtil.ShaderHasError(shader), Is.False, name);
            }
        }
    }
}
