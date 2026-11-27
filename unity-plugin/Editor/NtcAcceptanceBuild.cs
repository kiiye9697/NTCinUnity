using UnityEditor;
using UnityEngine.Rendering;

namespace Ntc.UnityEditorTools
{
    // One repeatable acceptance build from the prepared project; no UI automation.
    public static class NtcAcceptanceBuild
    {
        public static void BuildAll()
        {
            bool previousDefault = PlayerSettings.GetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64);
            var previousApis = PlayerSettings.GetGraphicsAPIs(BuildTarget.StandaloneWindows64);
            try
            {
                PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64,false);
                PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64,new[] { GraphicsDeviceType.Direct3D12 });
                NtcShowcasePreparation.Prepare();
                NtcLifecycleValidationBuild.BuildWindows64();
                Ntc.Unity.EditorTools.NtcBenchmarkBuild.BuildWindows64();
            }
            finally
            {
                PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64,previousApis);
                PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64,previousDefault);
            }
        }
    }
}
