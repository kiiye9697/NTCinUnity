using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;
using Process = System.Diagnostics.Process;
using ProcessStartInfo = System.Diagnostics.ProcessStartInfo;

namespace Ntc.Unity
{
    // A single reproducible camera path. A/B changes only the material backend.
    public sealed class NtcShowcasePresenter : MonoBehaviour
    {
        public NtcSceneController controller;
        public Camera viewCamera;
        public Vector3 startPosition, startEuler;
        public float fieldOfView=50, travelDistance=1.5f, travelSeconds=16;
        public int settleFrames=16;
        public Light sun;
        public bool useDaylight=true;
        public float courtyardFillIntensity=.6f;
        Light courtyardFill;
        bool ready,paused=true,switching,recording,hideUI;
        float pathTime;
        NtcSceneController.NtcMode mode=NtcSceneController.NtcMode.Original;
        int renderedFrames;
        string output,error;
        GUIStyle text;
        Process encoder;
        readonly List<string> encoderLog=new List<string>();
        Quaternion sourceSunRotation;
        Color sourceSunColor,sourceSky,sourceEquator,sourceGround;
        float sourceSunIntensity,sourceAmbientIntensity;
        AmbientMode sourceAmbientMode;
        UnityEngine.Rendering.SphericalHarmonicsL2 sourceProbe;
        float previousTimeScale;
        RenderTexture viewTarget,previousTarget;
        string Arg(string name,string fallback)
        {
            var a=Environment.GetCommandLineArgs(); for(int i=0;i+1<a.Length;i++) if(a[i]==name) return a[i+1];return fallback;
        }
        bool Has(string name) => Array.IndexOf(Environment.GetCommandLineArgs(),name)>=0;
        IEnumerator Start()
        {
            Application.runInBackground=true; QualitySettings.vSyncCount=0; Application.targetFrameRate=60;
            if(controller==null) controller=FindObjectOfType<NtcSceneController>();
            if(viewCamera==null) viewCamera=Camera.main;
            output=Path.GetFullPath(Arg("-ntcShowcaseOutput",Path.Combine(Application.persistentDataPath,"NtcShowcase")));
            Directory.CreateDirectory(output);
            if(controller==null || viewCamera==null) { Fail("A controller and MainCamera are required.");yield break; }
            controller.showPanel=false;
            previousTarget=viewCamera.targetTexture;
            viewTarget=new RenderTexture(Screen.width,Screen.height,24,RenderTextureFormat.ARGB32,RenderTextureReadWrite.sRGB) {name="NTC comparison camera",antiAliasing=1};
            viewTarget.Create();viewCamera.targetTexture=viewTarget;
            previousTimeScale=Time.timeScale;Time.timeScale=0;
            foreach(var c in viewCamera.GetComponents<MonoBehaviour>()) if(c.GetType().Name=="SimpleCameraController") c.enabled=false;
            if(sun==null) foreach(var l in FindObjectsOfType<Light>()) if(l.type==LightType.Directional) { sun=l;break; }
            sourceAmbientMode=RenderSettings.ambientMode;sourceAmbientIntensity=RenderSettings.ambientIntensity;
            sourceSky=RenderSettings.ambientSkyColor;sourceEquator=RenderSettings.ambientEquatorColor;sourceGround=RenderSettings.ambientGroundColor;
            sourceProbe=RenderSettings.ambientProbe;
            if(sun!=null) { sourceSunRotation=sun.transform.rotation;sourceSunColor=sun.color;sourceSunIntensity=sun.intensity; }
            RenderPipelineManager.endCameraRendering+=OnRendered;
            ApplyPose(0);
            yield return Guard(Initialize());
        }
        IEnumerator Guard(IEnumerator routine)
        {
            var stack=new Stack<IEnumerator>();stack.Push(routine);
            while(stack.Count>0)
            {
                bool moved=false;object item=null;Exception failure=null;
                try { moved=stack.Peek().MoveNext();if(moved) item=stack.Peek().Current; } catch(Exception e) { failure=e; }
                if(failure!=null) { Fail(failure.ToString());yield break; }
                if(!moved) { (stack.Pop() as IDisposable)?.Dispose();continue; }
                if(item is IEnumerator nested) stack.Push(nested);else yield return item;
            }
        }
        IEnumerator Initialize()
        {
            yield return Ready();
            controller.SetMode(NtcSceneController.NtcMode.Original);yield return Ready();
            if(Has("-ntcReviewLighting"))
            {
                string[] names={"source","skylight","daylight"};
                for(int i=0;i<names.Length;i++)
                {
                    ApplyLighting(i);ApplyPose(0);yield return Settle();
                    hideUI=true;yield return new WaitForEndOfFrame();SaveImage("lighting-"+names[i]+".png");hideUI=false;
                    Debug.Log("[NTC Showcase] Lighting view saved: "+names[i]);
                }
                File.WriteAllText(Path.Combine(output,"lighting.txt"),"Same camera, Original materials and ACES tone mapping. source: existing light. skylight: existing sun plus hemispheric ambient. daylight: the same ambient with angled, warm-neutral sun.\n");
                ApplyLighting(useDaylight?2:0);ready=true;
                if(Has("-ntcShowcaseAutoQuit")) Application.Quit(0);
                yield break;
            }
            ApplyLighting(useDaylight?2:0);yield return Settle();ready=true;
            if(Has("-ntcShowcaseRecord")) yield return Record();
        }
        void ApplyLighting(int preset)
        {
            RenderSettings.sun=sun;
            if(preset==0)
            {
                RenderSettings.ambientMode=sourceAmbientMode;RenderSettings.ambientIntensity=sourceAmbientIntensity;
                RenderSettings.ambientSkyColor=sourceSky;RenderSettings.ambientEquatorColor=sourceEquator;RenderSettings.ambientGroundColor=sourceGround;
                RenderSettings.ambientProbe=sourceProbe;
                if(sun!=null) { sun.transform.rotation=sourceSunRotation;sun.color=sourceSunColor;sun.intensity=sourceSunIntensity; }
            }
            else
            {
                RenderSettings.ambientMode=AmbientMode.Trilight;
                RenderSettings.ambientSkyColor=new Color(.45f,.51f,.60f);
                RenderSettings.ambientEquatorColor=new Color(.29f,.31f,.34f);
                RenderSettings.ambientGroundColor=new Color(.15f,.135f,.115f);
                RenderSettings.ambientIntensity=1;
                var probe=new UnityEngine.Rendering.SphericalHarmonicsL2();
                probe.AddAmbientLight(new Color(.18f,.19f,.22f));
                probe.AddDirectionalLight(Vector3.up,new Color(.10f,.12f,.16f),.7f);
                RenderSettings.ambientProbe=probe;
                if(sun!=null)
                {
                    sun.transform.rotation=preset==1?sourceSunRotation:Quaternion.Euler(55,155,0);
                    sun.color=preset==1?sourceSunColor:new Color(1,.96f,.90f);
                    sun.intensity=sourceSunIntensity;
                }
            }
            if(courtyardFill==null && preset>0)
            {
                courtyardFill=new GameObject("Courtyard fill").AddComponent<Light>();
                courtyardFill.type=LightType.Directional;
                courtyardFill.shadows=LightShadows.None;
                courtyardFill.color=new Color(.78f,.86f,1);
                courtyardFill.intensity=courtyardFillIntensity;
                courtyardFill.transform.rotation=Quaternion.Euler(25,startEuler.y,0);
            }
            if(courtyardFill!=null) courtyardFill.enabled=preset>0;
        }
        void ApplyPose(float normalized)
        {
            var rotation=Quaternion.Euler(startEuler);
            viewCamera.transform.SetPositionAndRotation(startPosition+rotation*Vector3.forward*(normalized*travelDistance),rotation);
            viewCamera.fieldOfView=fieldOfView;
            viewCamera.ResetProjectionMatrix();
            viewCamera.ResetWorldToCameraMatrix();
        }
        void LateUpdate()
        {
            // Hold the exact pose through rendering, including while a material switch
            // or lighting capture is in progress.
            if(viewCamera!=null) ApplyPose(pathTime/travelSeconds);
        }
        void OnRendered(ScriptableRenderContext context,Camera camera) { if(camera==viewCamera) renderedFrames++; }
        IEnumerator Ready()
        {
            float start=Time.realtimeSinceStartup;
            while(!controller.IsReady) { if(Time.realtimeSinceStartup-start>180) throw new TimeoutException("Material switch did not finish.");yield return null; }
        }
        IEnumerator Settle()
        {
            int first=renderedFrames;float start=Time.realtimeSinceStartup;
            while(renderedFrames-first<settleFrames) { if(Time.realtimeSinceStartup-start>90) throw new TimeoutException("Camera did not render.");yield return null; }
            yield return new WaitForEndOfFrame();
        }
        void SaveImage(string file)
        {
            Debug.Log("[NTC Showcase] "+file+" camera="+viewCamera.transform.position+" rotation="+viewCamera.transform.eulerAngles+" fov="+viewCamera.fieldOfView+" projection="+viewCamera.projectionMatrix.m00+","+viewCamera.projectionMatrix.m11);
            var image=ReadCamera();
            try { File.WriteAllBytes(Path.Combine(output,file),image.EncodeToPNG()); } finally { Destroy(image); }
        }
        Texture2D ReadCamera()
        {
            var previous=RenderTexture.active;RenderTexture.active=viewTarget;
            var image=new Texture2D(viewTarget.width,viewTarget.height,TextureFormat.RGB24,false,true);
            image.ReadPixels(new Rect(0,0,viewTarget.width,viewTarget.height),0,0);image.Apply();RenderTexture.active=previous;return image;
        }
        void Update()
        {
            if(!ready || switching || recording) return;
            if(Input.GetKeyDown(KeyCode.Alpha1)) ChangeMode(NtcSceneController.NtcMode.Original);
            if(Input.GetKeyDown(KeyCode.Alpha2)) ChangeMode(NtcSceneController.NtcMode.InferenceOnSampleDirect);
            if(Input.GetKeyDown(KeyCode.Space)) paused=!paused;
            if(Input.GetKeyDown(KeyCode.Home)) { pathTime=0;ApplyPose(0);paused=true; }
            if(!paused) { pathTime=Mathf.Min(pathTime+Time.unscaledDeltaTime,travelSeconds);ApplyPose(pathTime/travelSeconds);if(pathTime>=travelSeconds) paused=true; }
        }
        void ChangeMode(NtcSceneController.NtcMode value)
        { if(switching || mode==value) return;paused=true;StartCoroutine(Guard(Switch(value))); }
        IEnumerator Switch(NtcSceneController.NtcMode value)
        {
            switching=true;controller.SetMode(value);yield return Ready();yield return Settle();mode=value;switching=false;
        }
        IEnumerator Record()
        {
            string executable=Arg("-ntcShowcaseEncoder","");
            if(!File.Exists(executable)) throw new FileNotFoundException("Specify ffmpeg with -ntcShowcaseEncoder.",executable);
            recording=true;paused=true;Time.captureFramerate=30;
            int frameCount=Mathf.RoundToInt(travelSeconds*30);
            if(int.TryParse(Arg("-ntcShowcaseFrames",""),out int requested)) frameCount=Mathf.Max(requested,2);
            for(int pass=0;pass<2;pass++)
            {
                pathTime=0;ApplyPose(0);
                yield return Switch(pass==0?NtcSceneController.NtcMode.Original:NtcSceneController.NtcMode.InferenceOnSampleDirect);
                hideUI=true;yield return new WaitForEndOfFrame();SaveImage(pass==0?"original.png":"ntc-direct.png");hideUI=false;
                string video=Path.Combine(output,pass==0?"Original.mp4":"NTC-Direct.mp4");
                for(int i=0;i<frameCount;i++)
                {
                    ApplyPose(i/(float)(frameCount-1));
                    pathTime=i/(float)(frameCount-1)*travelSeconds;
                    int first=renderedFrames;float deadline=Time.realtimeSinceStartup+90;
                    do { if(Time.realtimeSinceStartup>deadline) throw new TimeoutException("No fresh camera frame was rendered.");yield return new WaitForEndOfFrame(); } while(renderedFrames<=first);
                    var image=ReadCamera();
                    try
                    {
                        var bytes=image.GetRawTextureData();int channels=bytes.Length/(image.width*image.height);
                        if(encoder==null)
                        {
                            if(channels!=3 && channels!=4) throw new InvalidOperationException("Unsupported screenshot format.");
                            var info=new ProcessStartInfo(executable,"-y -hide_banner -loglevel warning -f rawvideo -pix_fmt "+(channels==4?"rgba":"rgb24")+" -s "+image.width+"x"+image.height+" -r 30 -i pipe:0 -an -vf vflip -c:v libx264 -preset fast -crf 18 -pix_fmt yuv420p -movflags +faststart \""+video+"\"")
                            { UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardError=true };
                            encoder=Process.Start(info);encoder.ErrorDataReceived+=(sender,args)=>{if(args.Data!=null) lock(encoderLog) encoderLog.Add(args.Data);};encoder.BeginErrorReadLine();
                        }
                        encoder.StandardInput.BaseStream.Write(bytes,0,bytes.Length);
                    }
                    finally { Destroy(image); }
                    if(i%90==0 || i==frameCount-1) Debug.Log("[NTC Showcase] "+(pass==0?"Original":"NTC")+" "+i+" / "+frameCount+" camera="+viewCamera.transform.position+" rendered="+renderedFrames);
                }
                CloseEncoder();
            }
            Time.captureFramerate=0;recording=false;pathTime=0;ApplyPose(0);
            File.WriteAllText(Path.Combine(output,"recording.txt"),"Two actual Unity camera passes, "+frameCount+" frames each, encoded at 30 fps. Identical camera poses, lighting and tone mapping. Render capture is offline.\n");
            Debug.Log("[NTC Showcase] Both camera passes recorded.");
            if(Has("-ntcShowcaseAutoQuit")) Application.Quit(0);
        }
        void CloseEncoder()
        {
            if(encoder==null) return;
            encoder.StandardInput.Close();if(!encoder.WaitForExit(30000)) { encoder.Kill();throw new TimeoutException("Encoder did not finish."); }
            int code=encoder.ExitCode;encoder.Dispose();encoder=null;File.WriteAllLines(Path.Combine(output,"encoder.log"),encoderLog);
            if(code!=0) throw new InvalidOperationException("Encoder failed: "+code);
        }
        void Fail(string message)
        { error=message;Debug.LogError("[NTC Showcase] "+message);Time.captureFramerate=0;if(output!=null) File.WriteAllText(Path.Combine(output,"showcase-error.txt"),message);if(Has("-ntcShowcaseAutoQuit")) Application.Quit(1); }
        void OnGUI()
        {
            if(viewTarget!=null) GUI.DrawTexture(new Rect(0,0,Screen.width,Screen.height),viewTarget,ScaleMode.StretchToFill,false);
            if(hideUI) return;
            if(text==null) text=new GUIStyle(GUI.skin.label){fontSize=13,alignment=TextAnchor.MiddleCenter};
            if(recording)
            {
                var rect=new Rect(mode==NtcSceneController.NtcMode.Original?16:Screen.width-136,16,120,28);
                GUI.Box(rect,GUIContent.none);GUI.Label(rect,mode==NtcSceneController.NtcMode.Original?"A  Original":"B  NTC",text);return;
            }
            GUI.Box(new Rect(12,Screen.height-48,410,36),GUIContent.none);
            GUI.enabled=ready && !switching;
            if(GUI.Toggle(new Rect(22,Screen.height-39,102,22),mode==NtcSceneController.NtcMode.Original,"A  Original",GUI.skin.button)) ChangeMode(NtcSceneController.NtcMode.Original);
            if(GUI.Toggle(new Rect(128,Screen.height-39,102,22),mode==NtcSceneController.NtcMode.InferenceOnSampleDirect,"B  NTC",GUI.skin.button)) ChangeMode(NtcSceneController.NtcMode.InferenceOnSampleDirect);
            if(GUI.Button(new Rect(242,Screen.height-39,80,22),paused?"Forward":"Pause")) paused=!paused;
            if(GUI.Button(new Rect(328,Screen.height-39,80,22),"Reset")) { pathTime=0;ApplyPose(0);paused=true; }
            GUI.enabled=true;
            if(!ready || switching || error!=null) GUI.Label(new Rect(16,Screen.height-77,Screen.width-32,25),error??"Loading…");
        }
        void OnDestroy()
        {
            RenderPipelineManager.endCameraRendering-=OnRendered;Time.captureFramerate=0;
            Time.timeScale=previousTimeScale;
            if(courtyardFill!=null) Destroy(courtyardFill.gameObject);
            if(viewCamera!=null) viewCamera.targetTexture=previousTarget;
            if(viewTarget!=null) {viewTarget.Release();Destroy(viewTarget);}
            CloseEncoder();
        }
    }
}
