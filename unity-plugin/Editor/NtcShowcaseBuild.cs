using System;
using System.IO;
using Ntc.Unity;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Ntc.UnityEditorTools
{
    public static class NtcShowcaseBuild
    {
        public static void BuildWindows64()
        {
            string source=Arg("-ntcSourceScene","Assets/ClassicSponza/Scenes/Sponza_Showcase.unity");
            var scene=EditorSceneManager.OpenScene(source,OpenSceneMode.Single);
            var controller=UnityEngine.Object.FindObjectOfType<NtcSceneController>();
            if(controller==null) throw new InvalidOperationException("Prepare the scene's NTC controller and bindings first.");
            var camera=Camera.main;
            if(camera==null) throw new InvalidOperationException("The showcase needs a MainCamera.");
            var presenter=camera.gameObject.AddComponent<NtcShowcasePresenter>();
            presenter.controller=controller; presenter.viewCamera=camera;
            var p=camera.transform.position; var e=camera.transform.eulerAngles;
            presenter.startPosition=p; presenter.startEuler=e; presenter.fieldOfView=camera.fieldOfView;
            if(float.TryParse(Arg("-ntcCameraHeight",""),System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out float cameraHeight))
                presenter.startPosition=new Vector3(p.x,cameraHeight,p.z);
            presenter.travelDistance=1.5f; presenter.travelSeconds=16;
            controller.mode=NtcSceneController.NtcMode.Original;
            controller.showPanel=false;
            Directory.CreateDirectory("Assets/NtcShowcase");
            const string variant="Assets/NtcShowcase/Presentation.unity";
            EditorSceneManager.SaveScene(scene,variant,true);
            string output=Arg("-ntcShowcasePlayer",Path.GetFullPath("../build/NTCinUnity-Presentation/NTCinUnity.exe"));
            bool oldDefault=PlayerSettings.GetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64);
            var oldApis=PlayerSettings.GetGraphicsAPIs(BuildTarget.StandaloneWindows64);
            try
            {
                EditorSceneManager.OpenScene(variant,OpenSceneMode.Single);
                PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64,false);
                PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64,new[] { UnityEngine.Rendering.GraphicsDeviceType.Direct3D12 });
                Directory.CreateDirectory(Path.GetDirectoryName(output));
                var required=NtcPlayerData.RequiredInLoadedScenes();
                var report=BuildPipeline.BuildPlayer(new BuildPlayerOptions {scenes=new[] {variant},target=BuildTarget.StandaloneWindows64,locationPathName=output,options=BuildOptions.None});
                Debug.Log("[NTC Showcase Build] "+report.summary.result+" errors="+report.summary.totalErrors);
                if(report.summary.result!=UnityEditor.Build.Reporting.BuildResult.Succeeded) throw new Exception("Showcase build failed.");
                NtcPlayerData.PruneGeneratedPlayer(output,required);
            }
            finally
            {
                PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64,oldApis);
                PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64,oldDefault);
            }
        }
        static string Arg(string name,string fallback)
        {
            var a=Environment.GetCommandLineArgs(); for(int i=0;i+1<a.Length;i++) if(a[i]==name) return a[i+1];return fallback;
        }
    }
}
