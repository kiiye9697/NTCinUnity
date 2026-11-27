using System;
using System.IO;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Ntc.Packing;
using Ntc.Unity;
using UnityEditor;
using UnityEngine;

namespace Ntc.UnityEditorTools
{
    [InitializeOnLoad]
    public static class NtcTextureSetAssetEditor
    {
        sealed class CompletedBake
        {
            public string id, path;
            public Action<bool, string> complete;
        }
        static readonly ConcurrentQueue<CompletedBake> s_Completed = new ConcurrentQueue<CompletedBake>();

        static NtcTextureSetAssetEditor() { EditorApplication.update += CompleteBakes; }

        // Called by the worker: only immutable strings cross the Unity-thread boundary.
        public static void QueueCompletedBake(string id, string path, Action<bool, string> complete)
        {
            s_Completed.Enqueue(new CompletedBake { id = id, path = path, complete = complete });
        }

        static void CompleteBakes()
        {
            while (s_Completed.TryDequeue(out CompletedBake item))
            {
                try
                {
                    Material material = ResolveMaterial(item.id);
                    if (material == null) throw new InvalidOperationException("The source material was deleted during baking: " + item.id);
                    CreateOrUpdate(material, item.path);
                    item.complete?.Invoke(true, "Binding and recoverable original saved.");
                }
                catch (Exception error)
                {
                    Debug.LogError("[NTC Bake] Asset finalization failed: " + error);
                    item.complete?.Invoke(false, error.Message);
                }
            }
        }

        static Material ResolveMaterial(string id)
        {
            int split = id == null ? -1 : id.LastIndexOf('_');
            if (split < 0) return null;
            string path = AssetDatabase.GUIDToAssetPath(id.Substring(0, split));
            foreach (UnityEngine.Object asset in AssetDatabase.LoadAllAssetsAtPath(path))
                if (asset is Material material && StableId(material) == id) return material;
            return null;
        }

        public static string StableId(Material material)
        {
            if (material == null || !AssetDatabase.Contains(material)
                || !AssetDatabase.TryGetGUIDAndLocalFileIdentifier(material, out string guid, out long localId)
                || string.IsNullOrEmpty(guid))
                throw new InvalidOperationException("Save the material as an asset before baking NTC.");
            return guid + "_" + localId.ToString("x");
        }

        public static NtcTextureSetAsset Find(Material material)
        {
            if (material == null) return null;
            string id = StableId(material);
            NtcTextureSetAsset found = null;
            foreach (string guid in AssetDatabase.FindAssets("t:NtcTextureSetAsset"))
            {
                var asset = AssetDatabase.LoadAssetAtPath<NtcTextureSetAsset>(AssetDatabase.GUIDToAssetPath(guid));
                if (asset != null && (asset.editorSourceMaterial == material || asset.stableId == id))
                {
                    if (found != null && found != asset) throw new InvalidOperationException("Multiple NTC bindings for material " + id);
                    found = asset;
                }
            }
            return found;
        }

        public static NtcTextureSetAsset CreateOrUpdate(Material material, string ntcPath, string outputFolder = "Assets/NtcBindings")
        {
            string id = StableId(material);
            outputFolder = outputFolder.Replace('\\', '/').TrimEnd('/');
            string fullFolder = Path.GetFullPath(outputFolder);
            string assetRoot = Path.GetFullPath(Application.dataPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!fullFolder.StartsWith(assetRoot, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("NTC bindings must be saved inside the project's Assets directory.", nameof(outputFolder));
            outputFolder = "Assets/" + fullFolder.Substring(assetRoot.Length).Replace('\\', '/');
            ntcPath = Path.GetFullPath(ntcPath);
            var container = NtcContainerReader.Load(ntcPath);
            NtcAssetBuilder.ValidateInferenceModel(container);
            Directory.CreateDirectory(outputFolder);
            Directory.CreateDirectory("Assets/Resources/NtcOriginals");
            AssetDatabase.Refresh();
            string bindingPath = outputFolder + "/" + id + ".asset";
            var binding = Find(material) ?? AssetDatabase.LoadAssetAtPath<NtcTextureSetAsset>(bindingPath);
            if (binding == null)
            {
                binding = ScriptableObject.CreateInstance<NtcTextureSetAsset>();
                AssetDatabase.CreateAsset(binding, bindingPath);
            }
            binding.stableId = id;
            binding.directUnsupportedReason = DirectUnsupportedReason(material);
            if (string.IsNullOrEmpty(binding.directUnsupportedReason))
                try { NtcMaterialSampling.ValidateChannels(material, NtcAssetBuilder.BuildPbrLayout(container), HasAuthoredTexture(material,"_BaseMap")); }
                catch (NotSupportedException error) { binding.directUnsupportedReason = error.Message; }
            binding.editorSourceMaterial = material;
            binding.streamingPath = "NtcData/" + id + ".ntc";
            string publishedPath = Path.GetFullPath(Path.Combine(Application.streamingAssetsPath, binding.streamingPath));
            Directory.CreateDirectory(Path.GetDirectoryName(publishedPath));
            if (!string.Equals(ntcPath, publishedPath, StringComparison.OrdinalIgnoreCase)) File.Copy(ntcPath, publishedPath, true);
            binding.editorDataPath = "Assets/StreamingAssets/" + binding.streamingPath;
            binding.originalResourcePath = "NtcOriginals/" + id;
            binding.samplers = new[]
            {
                NtcSamplerSettings.Capture(material, "_BaseMap"), NtcSamplerSettings.Capture(material, "_BumpMap"),
                NtcSamplerSettings.Capture(material, "_MetallicGlossMap"), NtcSamplerSettings.Capture(material, "_OcclusionMap")
            };
            binding.texturelessTemplate = SaveMaterial(material, outputFolder + "/" + id + "_Template.mat", true);
            string directPath = outputFolder + "/" + id + "_Direct.mat";
            var directShader = Shader.Find("NTC/LitDirect_URP");
            if (directShader == null) throw new InvalidOperationException("NTC direct material shader is missing.");
            var direct = AssetDatabase.LoadAssetAtPath<Material>(directPath);
            if (direct == null) { direct = new Material(directShader); AssetDatabase.CreateAsset(direct,directPath); }
            direct.shader = directShader;
            direct.CopyPropertiesFromMaterial(binding.texturelessTemplate);
            foreach (string keyword in new[] { "_RECEIVE_SHADOWS_OFF", "_SPECULARHIGHLIGHTS_OFF", "_ENVIRONMENTREFLECTIONS_OFF" })
                if (material.IsKeywordEnabled(keyword)) direct.EnableKeyword(keyword); else direct.DisableKeyword(keyword);
            EditorUtility.SetDirty(direct);
            binding.directTemplate = direct;
            SaveMaterial(material, "Assets/Resources/" + binding.originalResourcePath + ".mat", false);
            AssetDatabase.ImportAsset("Assets/StreamingAssets/" + binding.streamingPath);
            EditorUtility.SetDirty(binding);
            AssetDatabase.SaveAssets();
            return binding;
        }

        public static string DirectUnsupportedReason(Material material)
        {
            if (material == null || material.shader == null || material.shader.name != "Universal Render Pipeline/Lit")
                return "Direct NTC shading currently requires Universal Render Pipeline/Lit.";
            var reasons = new List<string>();
            if (material.IsKeywordEnabled("_SPECULAR_SETUP") || (material.HasProperty("_WorkflowMode") && material.GetFloat("_WorkflowMode") < 0.5f))
                reasons.Add("specular workflow");
            if (material.HasProperty("_Surface") && material.GetFloat("_Surface") > 0.5f) reasons.Add("transparent surface");
            if (material.IsKeywordEnabled("_DETAIL") || material.IsKeywordEnabled("_DETAIL_MULX2") || material.IsKeywordEnabled("_DETAIL_SCALED")) reasons.Add("detail mapping");
            if (material.IsKeywordEnabled("_PARALLAXMAP")) reasons.Add("parallax mapping");
            if (material.IsKeywordEnabled("_CLEARCOAT") || material.IsKeywordEnabled("_CLEARCOATMAP")
                || (material.HasProperty("_ClearCoatMask") && material.GetFloat("_ClearCoatMask") > 0f)) reasons.Add("clear coat");
            if (material.IsKeywordEnabled("_EMISSION") && HasAuthoredTexture(material, "_EmissionMap")) reasons.Add("emission texture");
            var known = new HashSet<string> { "_BaseMap", "_MainTex", "_BumpMap", "_MetallicGlossMap", "_OcclusionMap",
                "_EmissionMap", "_SpecGlossMap", "_ParallaxMap", "_DetailMask", "_DetailAlbedoMap", "_DetailNormalMap", "_ClearCoatMap" };
            foreach (string property in material.GetTexturePropertyNames())
                if (!known.Contains(property) && HasAuthoredTexture(material, property)) reasons.Add("unsupported texture " + property);
            return reasons.Count == 0 ? "" : "Direct NTC shading does not support: " + string.Join(", ", reasons) + ".";
        }

        static bool HasAuthoredTexture(Material material, string property)
        {
            if (!material.HasProperty(property)) return false;
            Texture texture = material.GetTexture(property);
            if (texture == null) return false;
            string path = AssetDatabase.GetAssetPath(texture);
            return path != "Resources/unity_builtin_extra" && path != "Library/unity default resources";
        }

        static Material SaveMaterial(Material source, string path, bool stripTextures)
        {
            Material result = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (result == null)
            {
                result = new Material(source);
                AssetDatabase.CreateAsset(result, path);
            }
            else { result.shader = source.shader; result.CopyPropertiesFromMaterial(source); }
            result.name = source.name;
            result.SetOverrideTag("NtcSourceId", StableId(source));
            if (stripTextures)
            {
                foreach (string property in result.GetTexturePropertyNames()) result.SetTexture(property, null);
                // Materials can retain texture entries belonging to an old shader. Strip those
                // serialized references too, otherwise the template still pins source assets.
                var serialized = new SerializedObject(result);
                SerializedProperty textures = serialized.FindProperty("m_SavedProperties.m_TexEnvs");
                if (textures != null)
                    for (int i = 0; i < textures.arraySize; i++)
                        textures.GetArrayElementAtIndex(i).FindPropertyRelative("second.m_Texture").objectReferenceValue = null;
                serialized.ApplyModifiedPropertiesWithoutUndo();
            }
            EditorUtility.SetDirty(result);
            return result;
        }

        [MenuItem("Assets/NTC/Bind Existing NTC To Material", true)]
        static bool CanBindExisting() => Selection.activeObject is Material;

        [MenuItem("Assets/NTC/Bind Existing NTC To Material")]
        static void BindExisting()
        {
            var material = Selection.activeObject as Material;
            if (material == null) return;
            string path = EditorUtility.OpenFilePanel("Bind an existing NTC texture set", Application.streamingAssetsPath, "ntc");
            if (string.IsNullOrEmpty(path)) return;
            try { Selection.activeObject = CreateOrUpdate(material, path); }
            catch (Exception error) { Debug.LogException(error); EditorUtility.DisplayDialog("NTC Binding", error.Message, "OK"); }
        }
    }
}
