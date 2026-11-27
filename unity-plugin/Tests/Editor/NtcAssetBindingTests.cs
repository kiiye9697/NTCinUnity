using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using Ntc.Unity;
using Ntc.UnityEditorTools;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Ntc.Tests
{
    public class NtcAssetBindingTests
    {
        string root;
        readonly List<string> generated = new List<string>();

        [SetUp]
        public void SetUp()
        {
            root = "Assets/NtcBindingTests_" + Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(root);
            AssetDatabase.Refresh();
        }

        [TearDown]
        public void TearDown()
        {
            foreach (string path in generated) AssetDatabase.DeleteAsset(path);
            generated.Clear();
            AssetDatabase.DeleteAsset(root);
            AssetDatabase.Refresh();
        }

        Material MakeMaterial(string file)
        {
            var material = new Material(Shader.Find("Hidden/NTC/MaterialContractTest")) { name = "SameName" };
            AssetDatabase.CreateAsset(material, root + "/" + file + ".mat");
            material.name = "SameName";
            EditorUtility.SetDirty(material);
            return material;
        }

        NtcTextureSetAsset Bind(Material material, string data)
        {
            string id = NtcTextureSetAssetEditor.StableId(material);
            generated.Add("Assets/Resources/NtcOriginals/" + id + ".mat");
            generated.Add("Assets/StreamingAssets/NtcData/" + id + ".ntc");
            return NtcTextureSetAssetEditor.CreateOrUpdate(material, data, root + "/Bindings");
        }

        [Test]
        public void BakeSourcePath_ResolvesPackageCacheInsteadOfVirtualPackagesDirectory()
        {
            const string path = "Packages/com.unity.render-pipelines.universal/package.json";
            var package = UnityEditor.PackageManager.PackageInfo.FindForAssetPath(path);
            Assert.That(package, Is.Not.Null, "The test host requires URP.");
            string resolved = NtcBakeWindow.ResolveSourceAssetPath(path);
            Assert.That(resolved, Is.EqualTo(Path.GetFullPath(Path.Combine(package.resolvedPath, "package.json"))));
            Assert.That(File.Exists(resolved), Is.True);
            Assert.That(NtcBakeWindow.ResolveSourceAssetPath(root + "/Source.png"), Is.EqualTo(Path.GetFullPath(root + "/Source.png")));
        }

        [Test]
        public void PlayerData_MissingWholeDirectoryFailsWhenBindingRequiresData()
        {
            string executable = Path.Combine(Path.GetTempPath(), "NtcMissingPlayer_" + Guid.NewGuid().ToString("N"), "Player.exe");
            Assert.Throws<DirectoryNotFoundException>(() => NtcPlayerData.PruneGeneratedPlayer(executable, new HashSet<string> { "required.ntc" }));
            Assert.DoesNotThrow(() => NtcPlayerData.PruneGeneratedPlayer(executable, new HashSet<string>()));
        }

        [Test]
        public void SameNameAssets_HaveDistinctBindingsAndStableUpdatePaths()
        {
            Material first = MakeMaterial("First"), second = MakeMaterial("Second");
            Assert.That(first.name, Is.EqualTo(second.name));
            string data = WriteModel();
            NtcTextureSetAsset a = Bind(first, data), b = Bind(second, data);
            Assert.That(a.stableId, Is.Not.EqualTo(b.stableId));
            Assert.That(a.streamingPath, Is.Not.EqualTo(b.streamingPath));
            Assert.That(a.originalResourcePath, Is.Not.EqualTo(b.originalResourcePath));
            Assert.That(Bind(first, data), Is.SameAs(a));
            Assert.That(NtcTextureSetAssetEditor.Find(second), Is.SameAs(b));
            Assert.That(File.Exists(Path.Combine(Application.streamingAssetsPath, a.streamingPath)), Is.True);
        }

        [Test]
        public void SceneScan_VisitsAllSlotsAndDeduplicatesByObject()
        {
            Material first = MakeMaterial("First"), second = MakeMaterial("Second");
            var host = new GameObject("NTC scan test");
            try
            {
                var renderer = host.AddComponent<MeshRenderer>();
                renderer.sharedMaterials = new[] { first, second, first };
                CollectionAssert.AreEqual(new[] { first, second }, NtcBakeWindow.CollectDistinctMaterials(new Renderer[] { renderer, renderer }));
            }
            finally { UnityEngine.Object.DestroyImmediate(host); }
        }

        [Test]
        public void BindingTemplate_DropsTexturesWhileOriginalAndSamplerRemainRecoverable()
        {
            var material = MakeMaterial("Source");
            var texture = new Texture2D(1, 1) { wrapModeU = TextureWrapMode.Clamp, wrapModeV = TextureWrapMode.Mirror, filterMode = FilterMode.Point };
            AssetDatabase.CreateAsset(texture, root + "/SourceTexture.asset");
            material.SetTexture("_BaseMap", texture);
            material.SetTextureScale("_BaseMap", new Vector2(2, 3));
            material.SetFloat("_Smoothness", 0.27f);
            EditorUtility.SetDirty(material);
            NtcTextureSetAsset binding = Bind(material, WriteModel());
            foreach (string property in binding.texturelessTemplate.GetTexturePropertyNames())
                Assert.That(binding.texturelessTemplate.GetTexture(property), Is.Null, property);
            var original = Resources.Load<Material>(binding.originalResourcePath);
            Assert.That(original, Is.Not.Null);
            Assert.That(AssetDatabase.GetAssetPath(original.GetTexture("_BaseMap")), Is.EqualTo(root + "/SourceTexture.asset"));
            Assert.That(original.GetTexture("_BaseMap").width, Is.EqualTo(1));
            Assert.That(original.GetFloat("_Smoothness"), Is.EqualTo(0.27f));
            NtcSamplerSettings sampler = binding.Sampler("_BaseMap");
            Assert.That(sampler.wrapU, Is.EqualTo(TextureWrapMode.Clamp));
            Assert.That(sampler.wrapV, Is.EqualTo(TextureWrapMode.Mirror));
            Assert.That(sampler.filter, Is.EqualTo(FilterMode.Point));
            Assert.That(sampler.scale, Is.EqualTo(new Vector2(2, 3)));
        }

        [Test]
        public void BrokenExplicitBinding_DoesNotResolveSameNameLegacyFile()
        {
            var material = MakeMaterial("Source");
            var binding = Bind(material, WriteModel());
            binding.editorDataPath = Path.GetFullPath(root + "/Missing.ntc");
            File.Copy(WriteModel(), root + "/" + material.name + ".ntc", true);
            var window = ScriptableObject.CreateInstance<NtcBakeWindow>();
            try
            {
                var type = typeof(NtcBakeWindow);
                type.GetField("_outputDir", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(window, Path.GetFullPath(root));
                Type entryType = type.GetNestedType("MaterialEntry", BindingFlags.NonPublic);
                object entry = Activator.CreateInstance(entryType, true);
                entryType.GetField("sourceMaterial").SetValue(entry, material);
                entryType.GetField("materialName").SetValue(entry, material.name);
                entryType.GetField("stableId").SetValue(entry, binding.stableId);
                type.GetMethod("CheckBakeStatus", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(window, new[] { entry });
                Assert.That(entryType.GetField("hasNtc").GetValue(entry), Is.False);
                Assert.That(entryType.GetField("ntcPath").GetValue(entry), Is.EqualTo(binding.editorDataPath));
            }
            finally { UnityEngine.Object.DestroyImmediate(window); }
        }

        string WriteModel()
        {
            string path = root + "/model.ntc";
            if (File.Exists(path)) return path;
            int[] channels = { 48, 64, 48, 32, 16 };
            var layers = new List<string>();
            var views = new List<string>();
            int offset = 0;
            for (int i = 0; i < 4; i++)
            {
                int first = views.Count;
                foreach (int size in new[] { channels[i] * channels[i + 1], channels[i + 1] * 4, channels[i + 1] * 4 })
                {
                    views.Add("{\"offset\":" + offset + ",\"storedSize\":" + size + "}");
                    offset += size;
                }
                layers.Add("{\"inputChannels\":" + channels[i] + ",\"outputChannels\":" + channels[i + 1]
                    + ",\"weightView\":" + first + ",\"scaleView\":" + (first + 1) + ",\"biasView\":" + (first + 2) + ",\"biasType\":\"Int32\"}");
            }
            int latentView = views.Count;
            views.Add("{\"offset\":" + offset + ",\"storedSize\":2}");
            string json = "{\"schemaVersion\":3,\"width\":1,\"height\":1,\"numChannels\":1,\"numColorMips\":1,"
                + "\"mlpVersions\":[{\"weightLayout\":\"RowMajor\",\"layers\":[" + string.Join(",", layers) + "]}],"
                + "\"latents\":[{\"width\":1,\"height\":1,\"layerViews\":[" + latentView + "]}],"
                + "\"colorMips\":[{\"width\":1,\"height\":1,\"latentMip\":0,\"positionScale\":1}],"
                + "\"views\":[" + string.Join(",", views) + "]}";
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            using (var writer = new BinaryWriter(File.Create(path)))
            {
                writer.Write(Encoding.ASCII.GetBytes("NTEX")); writer.Write(1u);
                writer.Write(40UL); writer.Write((ulong)bytes.Length);
                writer.Write((ulong)(40 + bytes.Length)); writer.Write((ulong)(offset + 2));
                writer.Write(bytes); writer.Write(new byte[offset + 2]);
            }
            return path;
        }
    }
}
