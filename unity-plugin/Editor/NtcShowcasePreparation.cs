using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using Ntc.Unity;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace Ntc.UnityEditorTools
{
    public static class NtcShowcasePreparation
    {
        [MenuItem("NTC/Prepare Showcase Bindings")]
        public static void Prepare()
        {
            string scene = Arg("-ntcBenchmarkScene", "Assets/ClassicSponza/Scenes/Sponza_Showcase.unity");
            if (!File.Exists(scene)) throw new FileNotFoundException("Showcase scene missing", scene);
            if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            EditorSceneManager.OpenScene(scene, OpenSceneMode.Single);
            EnsurePipeline();
            var controller = UnityEngine.Object.FindObjectOfType<NtcSceneController>();
            if (controller == null) controller = new GameObject("NTC_Controller").AddComponent<NtcSceneController>();
            Material[] materials = NtcBakeWindow.CollectDistinctMaterials(UnityEngine.Object.FindObjectsOfType<Renderer>(true));
            var names = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (Material material in materials) { names.TryGetValue(material.name, out int count); names[material.name] = count + 1; }
            var planned = new Dictionary<Material, string>();
            string legacyDirectory = Arg("-ntcLegacyDirectory", Path.Combine(Application.streamingAssetsPath, "NtcData"));
            foreach (Material material in materials)
            {
                var binding = NtcTextureSetAssetEditor.Find(material);
                if (binding != null)
                {
                    string explicitPath = binding.ResolveDataPath();
                    if (!File.Exists(explicitPath)) throw new FileNotFoundException("Explicit NTC binding is broken: " + material.name, explicitPath);
                    planned.Add(material, explicitPath);
                    continue;
                }
                var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { legacyDirectory, Path.Combine(Application.dataPath, "NtcPlugin/NtcData") };
                var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string directory in directories)
                    foreach (string file in new[] { material.name + ".ntc", controller.ntcFilePrefix + material.name + ".ntc" })
                    {
                        string path = Path.GetFullPath(Path.Combine(directory, file));
                        if (File.Exists(path)) candidates.Add(path);
                    }
                if (candidates.Count == 0) { Debug.LogWarning("[NTC Prepare] No legacy bake for " + AssetDatabase.GetAssetPath(material)); continue; }
                if (names[material.name] != 1)
                    throw new InvalidOperationException("Ambiguous legacy NTC for " + material.name + "; bind each source material explicitly first.");
                planned.Add(material, SelectLegacyCandidate(candidates, legacyDirectory));
            }
            var bindings = new List<NtcTextureSetAsset>();
            foreach (var item in planned)
            {
                NtcTextureSetAsset binding = NtcTextureSetAssetEditor.CreateOrUpdate(item.Key, item.Value);
                bindings.Add(binding);
                Debug.Log("[NTC Prepare] source=" + AssetDatabase.GetAssetPath(item.Key) + " binding=" + AssetDatabase.GetAssetPath(binding) + " data=" + binding.streamingPath);
            }
            controller.textureSets = bindings.ToArray();
            controller.onSampleShader = RequiredShader("NTC/InferenceOnSample_URP");
            controller.directSampleShader = RequiredShader("NTC/LitDirect_URP");
            controller.pbrDebugShader = RequiredShader("NTC/PBRChannelDebug_URP");
            controller.releaseOriginalTextures = Has("-ntcReleaseOriginalTextures");
            if (!Enum.TryParse(Arg("-ntcInitialMode", "Original"), true, out NtcSceneController.NtcMode mode) || !Enum.IsDefined(typeof(NtcSceneController.NtcMode), mode))
                throw new ArgumentException("Unknown -ntcInitialMode; use Original, InferenceOnLoad, InferenceOnSample, or InferenceOnSampleDirect.");
            controller.mode = mode;
            controller.ScanSceneMaterials();
            int slots = 0;
            foreach (var group in controller.groups) if (group.slots != null) slots += group.slots.Length;
            EditorUtility.SetDirty(controller);
            EditorSceneManager.MarkSceneDirty(controller.gameObject.scene);
            EditorSceneManager.SaveScene(controller.gameObject.scene);
            var scenes = new List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
            int sceneIndex = scenes.FindIndex(item => item.path == scene);
            if (sceneIndex < 0) scenes.Add(new EditorBuildSettingsScene(scene, true));
            else scenes[sceneIndex] = new EditorBuildSettingsScene(scene, true);
            EditorBuildSettings.scenes = scenes.ToArray();
            AssetDatabase.SaveAssets();
            Debug.Log("[NTC Prepare] scene=" + scene + " bindcount=" + bindings.Count + " groups=" + controller.groups.Length + " slots=" + slots
                + " pipeline=" + AssetDatabase.GetAssetPath(GraphicsSettings.renderPipelineAsset) + " initialMode=" + mode + " releaseOriginalTextures=" + controller.releaseOriginalTextures);
        }

        static Shader RequiredShader(string name)
        {
            Shader shader = Shader.Find(name);
            if (shader == null) throw new InvalidOperationException("Required shader missing: " + name + ". Sync Shaders and Resources before preparation.");
            return shader;
        }

        static void EnsurePipeline()
        {
            string requested = Arg("-ntcPipelineAsset", "");
            RenderPipelineAsset pipeline = string.IsNullOrEmpty(requested) ? QualitySettings.renderPipeline ?? GraphicsSettings.renderPipelineAsset
                : AssetDatabase.LoadAssetAtPath<RenderPipelineAsset>(requested);
            if (!string.IsNullOrEmpty(requested) && pipeline == null) throw new FileNotFoundException("Requested URP pipeline asset is missing", requested);
            if (pipeline == null)
            {
                var candidates = new List<RenderPipelineAsset>();
                foreach (string guid in AssetDatabase.FindAssets("t:RenderPipelineAsset"))
                {
                    var asset = AssetDatabase.LoadAssetAtPath<RenderPipelineAsset>(AssetDatabase.GUIDToAssetPath(guid));
                    if (asset != null && asset.GetType().Name == "UniversalRenderPipelineAsset") candidates.Add(asset);
                }
                if (candidates.Count != 1) throw new InvalidOperationException("Select an existing URP asset with -ntcPipelineAsset; found " + candidates.Count + " candidates.");
                pipeline = candidates[0];
            }
            if (pipeline.GetType().Name != "UniversalRenderPipelineAsset") throw new InvalidOperationException("Showcase requires an existing UniversalRenderPipelineAsset.");
            GraphicsSettings.renderPipelineAsset = pipeline;
            QualitySettings.renderPipeline = pipeline;
            Debug.Log("[NTC Prepare] pipeline=" + AssetDatabase.GetAssetPath(pipeline));
        }

        public static string SelectLegacyCandidate(IEnumerable<string> candidates, string preferredDirectory)
        {
            var paths = new List<string>(candidates);
            paths.Sort(StringComparer.OrdinalIgnoreCase);
            string fingerprint = null, selected = null;
            string preferred = Path.GetFullPath(preferredDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            foreach (string file in paths)
            {
                string path = Path.GetFullPath(file), hash;
                using (var stream = File.OpenRead(path)) using (var sha = SHA256.Create()) hash = Convert.ToBase64String(sha.ComputeHash(stream));
                if (fingerprint != null && hash != fingerprint)
                    throw new InvalidOperationException("Conflicting legacy NTC contents; bind the desired file explicitly: " + string.Join(", ", paths));
                fingerprint = hash;
                if (selected == null || path.StartsWith(preferred, StringComparison.OrdinalIgnoreCase)) selected = path;
            }
            return selected;
        }

        static bool Has(string name) => Array.Exists(Environment.GetCommandLineArgs(), item => string.Equals(item, name, StringComparison.OrdinalIgnoreCase));
        static string Arg(string name, string fallback)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i + 1 < args.Length; i++) if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
            return fallback;
        }
    }
}
