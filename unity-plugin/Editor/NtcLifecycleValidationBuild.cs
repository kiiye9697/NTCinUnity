using System;
using System.IO;
using Ntc.Unity;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace Ntc.UnityEditorTools
{
    public static class NtcLifecycleValidationBuild
    {
        public static void BuildWindows64()
        {
            NtcTextureSetAsset binding = null;
            foreach (string guid in AssetDatabase.FindAssets("t:NtcTextureSetAsset"))
            {
                var candidate = AssetDatabase.LoadAssetAtPath<NtcTextureSetAsset>(AssetDatabase.GUIDToAssetPath(guid));
                if (candidate.editorSourceMaterial != null && candidate.editorSourceMaterial.name == "Column_A"
                    && string.IsNullOrEmpty(candidate.directUnsupportedReason)) { binding = candidate; break; }
            }
            if (binding == null) throw new InvalidOperationException("Prepare the Showcase Column_A binding first.");
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            Directory.CreateDirectory("Assets/NtcValidation");
            var pipelineType = Type.GetType("UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset, Unity.RenderPipelines.Universal.Runtime", true);
            var pipeline = (RenderPipelineAsset)pipelineType.GetMethod("Create").Invoke(null, new object[] { null });
            // The isolated DrawMesh probe submits one explicit main light, without URP's
            // per-camera shadow/additional-light setup. Keep that exact OFF variant in
            // Player builds rather than relying on a nearest stripped-variant match.
            pipelineType.GetProperty("supportsMainLightShadows").SetValue(pipeline, false);
            var additionalLights = pipelineType.GetProperty("additionalLightsRenderingMode");
            additionalLights.SetValue(pipeline, Enum.Parse(additionalLights.PropertyType, "Disabled"));
            AssetDatabase.CreateAsset(pipeline, "Assets/NtcValidation/LifecyclePipeline.asset");
            var rendererData = (UnityEngine.Object[])pipelineType.GetField("m_RendererDataList", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(pipeline);
            foreach (var data in rendererData) if (data != null) AssetDatabase.AddObjectToAsset(data, pipeline);
            RenderPipelineAsset previousGraphics = GraphicsSettings.renderPipelineAsset, previousQuality = QualitySettings.renderPipeline;
            GraphicsSettings.renderPipelineAsset = pipeline; QualitySettings.renderPipeline = pipeline;
            try
            {
                var cameraObject = new GameObject("Camera"); cameraObject.tag = "MainCamera";
                var camera = cameraObject.AddComponent<Camera>();
                camera.transform.position = new Vector3(0,0,-4); camera.orthographic = true; camera.orthographicSize = 2;
                camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = Color.gray;
                var light = new GameObject("Sun").AddComponent<Light>(); light.type = LightType.Directional; light.transform.rotation = Quaternion.Euler(30,20,0);
                var managed = GameObject.CreatePrimitive(PrimitiveType.Quad); managed.name = "Managed consumer"; managed.transform.position = Vector3.left;
                var shared = GameObject.CreatePrimitive(PrimitiveType.Quad); shared.name = "Shared source consumer"; shared.transform.position = Vector3.right;
                managed.GetComponent<Renderer>().sharedMaterial = binding.editorSourceMaterial;
                shared.GetComponent<Renderer>().sharedMaterial = binding.editorSourceMaterial;
                var host = new GameObject("NTC Lifecycle Validation");
                var controller = host.AddComponent<NtcSceneController>();
                controller.mode = NtcSceneController.NtcMode.Original; controller.releaseOriginalTextures = true; controller.showPanel = false;
                controller.directSampleShader = Shader.Find("NTC/LitDirect_URP");
                controller.groups = new[] { new NtcSceneController.MaterialGroup
                {
                    name = binding.editorSourceMaterial.name, textureSet = binding,
                    renderers = new[] { managed.GetComponent<Renderer>() },
                    slots = new[] { new NtcRendererSlot { renderer = managed.GetComponent<Renderer>(), materialIndex = 0 } }
                } };
                var runner = host.AddComponent<NtcLifecycleValidationRunner>();
                runner.controller = controller; runner.managedConsumer = managed.GetComponent<Renderer>(); runner.sharedConsumer = shared.GetComponent<Renderer>();
                const string scene = "Assets/NtcValidation/Lifecycle.unity";
                EditorSceneManager.SaveScene(SceneManager.GetActiveScene(), scene);
                AssetDatabase.SaveAssets();
                string output = GetArg("-ntcLifecyclePlayer", Path.GetFullPath("../build/NTCinUnity-Lifecycle/NTCinUnity.exe"));
                Directory.CreateDirectory(Path.GetDirectoryName(output));
                var requiredNtc = NtcPlayerData.RequiredInLoadedScenes();
                var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions { scenes = new[] { scene }, target = BuildTarget.StandaloneWindows64, locationPathName = output, options = BuildOptions.None });
                Debug.Log("[NTC Lifecycle Build] " + report.summary.result + " errors=" + report.summary.totalErrors);
                if (report.summary.result != BuildResult.Succeeded) throw new Exception("Lifecycle Player build failed.");
                NtcPlayerData.PruneGeneratedPlayer(output,requiredNtc);
            }
            finally { GraphicsSettings.renderPipelineAsset = previousGraphics; QualitySettings.renderPipeline = previousQuality; }
        }
        static string GetArg(string name, string fallback)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i + 1 < args.Length; i++) if (args[i] == name) return args[i + 1]; return fallback;
        }
    }
}
