using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;

namespace Ntc.Unity
{
    // Dedicated small Player acceptance case; never installed automatically in user scenes.
    public sealed class NtcLifecycleValidationRunner : MonoBehaviour
    {
        public NtcSceneController controller;
        public Renderer managedConsumer, sharedConsumer;
        [Serializable] public class Phase { public string name; public int originalIdsAlive; public NtcMemorySnapshot memory; }
        [Serializable] public class Report
        {
            public bool completed, sharedConsumerProtected, unreferencedOriginalsReleased, originalRestored;
            public bool directFragmentRendered;
            public string graphicsDevice, unityVersion;
            public string probeTimingScope = "Fresh Player process; first/warm draw plus blocking readback. Driver disk cache is not cleared; these are not GPU frame timings.";
            public double firstDrawAndReadbackSeconds, warmDrawAndReadbackSeconds;
            public double firstSubmitSeconds, firstReadbackWaitSeconds;
            public long processPeakWorkingSetBytes;
            public List<Phase> phases = new List<Phase>();
        }
        readonly Report report = new Report();
        int[] originalIds;

        IEnumerator Start()
        {
            Application.runInBackground = true;
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = 60;
            string output = Argument("-ntcLifecycleOutput", Path.Combine(Application.persistentDataPath, "ntc-lifecycle"));
            Directory.CreateDirectory(output);
            report.graphicsDevice = SystemInfo.graphicsDeviceName;
            report.unityVersion = Application.unityVersion;
            try
            {
                yield return WaitReady();
                originalIds = CaptureTextureIds(managedConsumer);
                if (originalIds.Length == 0) throw new InvalidOperationException("The lifecycle case needs real source textures.");
                Record("original");
                controller.SetMode(NtcSceneController.NtcMode.InferenceOnSampleDirect);
                yield return null;
                yield return WaitReady();
                Record("direct_with_shared_original_consumer");
                var firstProbe = System.Diagnostics.Stopwatch.StartNew();
                report.directFragmentRendered = RenderProbe(Path.Combine(output, "direct-lit-probe.png"),true);
                report.firstDrawAndReadbackSeconds = firstProbe.Elapsed.TotalSeconds;
                var warmProbe = System.Diagnostics.Stopwatch.StartNew();
                report.directFragmentRendered &= RenderProbe(Path.Combine(output,"direct-lit-probe-warm.png"),false);
                report.warmDrawAndReadbackSeconds = warmProbe.Elapsed.TotalSeconds;
                report.processPeakWorkingSetBytes = System.Diagnostics.Process.GetCurrentProcess().PeakWorkingSet64;
                report.sharedConsumerProtected = AliveCount() == originalIds.Length && HasOriginalMap(sharedConsumer);
                if (!report.sharedConsumerProtected) throw new InvalidOperationException("A shared source texture was incorrectly unloaded.");
                sharedConsumer.sharedMaterials = Array.Empty<Material>();
                sharedConsumer.gameObject.SetActive(false);
                yield return Resources.UnloadUnusedAssets();
                for (int i = 0; i < 4; i++) yield return null;
                Record("direct_without_original_consumers");
                report.unreferencedOriginalsReleased = AliveCount() < originalIds.Length;
                if (!report.unreferencedOriginalsReleased) throw new InvalidOperationException("No original texture was released in the Player.");
                controller.SetMode(NtcSceneController.NtcMode.Original);
                yield return null;
                yield return WaitReady();
                report.originalRestored = HasOriginalMap(managedConsumer);
                Record("original_restored");
                if (!report.originalRestored) throw new InvalidOperationException("Original material recovery failed.");
                controller.SetMode(NtcSceneController.NtcMode.InferenceOnSampleDirect);
                yield return null;
                yield return WaitReady();
                Record("direct_second_cycle");
                if (!controller.OriginalsReleased) throw new InvalidOperationException("Second-cycle original release did not complete.");
                double budget;
                if (!double.TryParse(Argument("-ntcFirstDrawBudgetSeconds","10"),System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out budget)) budget=10;
                report.completed = report.directFragmentRendered && report.firstDrawAndReadbackSeconds <= budget;
                if (!report.completed) Debug.LogError("[NTC Lifecycle] First-draw probe failed or exceeded its wall-time budget: " + report.firstDrawAndReadbackSeconds);
            }
            finally
            {
                File.WriteAllText(Path.Combine(output, "lifecycle.json"), JsonUtility.ToJson(report, true));
                Debug.Log("[NTC Lifecycle] completed=" + report.completed + " sharedProtected=" + report.sharedConsumerProtected
                    + " released=" + report.unreferencedOriginalsReleased + " restored=" + report.originalRestored + " output=" + output);
                Application.Quit(report.completed ? 0 : 1);
            }
        }

        IEnumerator WaitReady()
        {
            float start = Time.realtimeSinceStartup;
            while (!controller.IsReady)
            {
                if (Time.realtimeSinceStartup - start > 120) throw new TimeoutException("NTC controller did not settle.");
                yield return null;
            }
            foreach (var group in controller.groups)
                if (group.status != null && (group.status.Contains("err") || group.status.Contains("failed")))
                    throw new InvalidOperationException(group.status);
        }

        void Record(string name) => report.phases.Add(new Phase { name = name, originalIdsAlive = AliveCount(), memory = controller.MemorySnapshot });
        int AliveCount()
        {
            var live = new HashSet<int>();
            foreach (var texture in Resources.FindObjectsOfTypeAll<Texture>()) live.Add(texture.GetInstanceID());
            int count = 0; foreach (int id in originalIds) if (live.Contains(id)) count++;
            return count;
        }
        static int[] CaptureTextureIds(Renderer renderer)
        {
            var ids = new HashSet<int>();
            NtcMemoryAccounting.RememberTextures(renderer.sharedMaterials, ids);
            var result = new int[ids.Count]; ids.CopyTo(result); return result;
        }
        static bool HasOriginalMap(Renderer renderer)
        {
            Material m = renderer.sharedMaterial;
            return m != null && m.HasProperty("_BaseMap") && m.GetTexture("_BaseMap") != null;
        }
        bool RenderProbe(string path, bool first)
        {
            const int size = 64;
            var target = new RenderTexture(size,size,0,RenderTextureFormat.ARGB32,RenderTextureReadWrite.Linear);
            target.Create();
            var command = new CommandBuffer();
            var copy = new Texture2D(size,size,TextureFormat.RGBA32,false,true);
            Material material = managedConsumer.sharedMaterial;
            string previousPipeline = Shader.globalRenderPipeline;
            bool previousBatching = GraphicsSettings.useScriptableRenderPipelineBatching;
            float previousCull = material.GetFloat("_Cull");
            RenderTexture previous = RenderTexture.active;
            try
            {
                Shader.globalRenderPipeline = "UniversalPipeline";
                GraphicsSettings.useScriptableRenderPipelineBatching = false;
                material.SetFloat("_Cull",0);
                Camera camera = Camera.main;
                command.SetRenderTarget(target); command.ClearRenderTarget(false,true,Color.clear);
                command.SetViewport(new Rect(0,0,size,size));
                command.SetViewProjectionMatrices(Matrix4x4.identity,GL.GetGPUProjectionMatrix(Matrix4x4.Ortho(-1,1,-1,1,-1,1),true));
                command.SetGlobalVector("_WorldSpaceCameraPos",new Vector4(0,0,-2,1));
                command.SetGlobalVector("_MainLightPosition",new Vector4(0,0,-1,0));
                command.SetGlobalVector("_MainLightColor",Vector4.one);
                command.SetGlobalVector("unity_LightData",new Vector4(0,0,1,0));
                int pass = material.FindPass("ForwardLit");
                Debug.Log("[NTC Probe] shader="+material.shader.name+" supported="+material.shader.isSupported+" pass="+pass+" backend="+material.GetFloat("_NtcBackend"));
                Debug.Log("[NTC Probe] clip="+material.GetFloat("_AlphaClip")+" tint="+material.GetColor("_BaseColor")
                    +" materialKeywords="+string.Join(",",material.shaderKeywords)+" enabledGlobalKeywords="+string.Join(",",Array.ConvertAll(Shader.enabledGlobalKeywords,keyword => keyword.name))
                    +" mesh="+managedConsumer.GetComponent<MeshFilter>().sharedMesh.bounds);
                command.DrawMesh(managedConsumer.GetComponent<MeshFilter>().sharedMesh,Matrix4x4.identity,material,0,pass);
                var submit = System.Diagnostics.Stopwatch.StartNew();
                Graphics.ExecuteCommandBuffer(command);
                double submitSeconds = submit.Elapsed.TotalSeconds;
                RenderTexture.active = target;
                var readback = System.Diagnostics.Stopwatch.StartNew();
                copy.ReadPixels(new Rect(0,0,size,size),0,0); copy.Apply();
                if (first) { report.firstSubmitSeconds=submitSeconds; report.firstReadbackWaitSeconds=readback.Elapsed.TotalSeconds; }
                Color[] pixels = copy.GetPixels(); bool covered = false;
                for (int i = 0; i < pixels.Length; i++) { if (pixels[i].a > 0.5f) covered = true; pixels[i] = pixels[i].gamma; }
                copy.SetPixels(pixels); copy.Apply(); File.WriteAllBytes(path,copy.EncodeToPNG()); return covered;
            }
            finally
            {
                material.SetFloat("_Cull",previousCull); RenderTexture.active = previous;
                Shader.globalRenderPipeline = previousPipeline;
                GraphicsSettings.useScriptableRenderPipelineBatching = previousBatching;
                command.Release(); target.Release(); Destroy(copy); Destroy(target);
            }
        }
        static string Argument(string name, string fallback)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i + 1 < args.Length; i++) if (args[i] == name) return args[i + 1];
            return fallback;
        }
    }
}
