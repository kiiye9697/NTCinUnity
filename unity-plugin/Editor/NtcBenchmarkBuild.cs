// Batch build entry point. Invoke with -executeMethod Ntc.Unity.EditorTools.NtcBenchmarkBuild.BuildWindows64.
using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;
using Ntc.UnityEditorTools;
using UnityEditor.SceneManagement;
using UnityEngine.Rendering;

namespace Ntc.Unity.EditorTools
{
    public static class NtcBenchmarkBuild
    {
        public static void BuildWindows64()
        {
            string scene = GetArg("-ntcBenchmarkScene", "Assets/ClassicSponza/Scenes/Sponza_Showcase.unity");
            string output = GetArg("-ntcBenchmarkPlayer", Path.GetFullPath("../build/NTCinUnity-Benchmark/NTCinUnity.exe"));
            if (!File.Exists(scene)) throw new FileNotFoundException("Benchmark scene not found", scene);
            EditorSceneManager.OpenScene(scene,OpenSceneMode.Single);
            Directory.CreateDirectory(Path.GetDirectoryName(output));

            bool oldFrameTiming = PlayerSettings.enableFrameTimingStats;
            bool oldDefaultApis = PlayerSettings.GetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64);
            var oldApis = PlayerSettings.GetGraphicsAPIs(BuildTarget.StandaloneWindows64);
            var requiredNtc = NtcPlayerData.RequiredInLoadedScenes();
            PlayerSettings.enableFrameTimingStats = true;
            BuildReport report;
            try
            {
                PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64,false);
                PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64,new[] { GraphicsDeviceType.Direct3D12 });
                BuildPlayerOptions options = new BuildPlayerOptions
                {
                    scenes = new[] { scene },
                    locationPathName = output,
                    target = BuildTarget.StandaloneWindows64,
                    options = HasFlag("-ntcBenchmarkDevelopment") ? BuildOptions.Development : BuildOptions.None
                };
                report = BuildPipeline.BuildPlayer(options);
            }
            finally
            {
                PlayerSettings.enableFrameTimingStats = oldFrameTiming;
                PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64,oldApis);
                PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64,oldDefaultApis);
            }
            BuildSummary summary = report.summary;
            Debug.Log(string.Format("[NTC Benchmark Build] result={0} errors={1} warnings={2} size={3} time={4}", summary.result, summary.totalErrors, summary.totalWarnings, summary.totalSize, summary.totalTime));
            if (summary.result != BuildResult.Succeeded) throw new Exception("NTC benchmark Player build failed: " + summary.result);
            NtcPlayerData.PruneGeneratedPlayer(output,requiredNtc);
        }

        static string GetArg(string name, string fallback)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i + 1 < args.Length; i++) if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
            return fallback;
        }

        static bool HasFlag(string name)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++) if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }
}
