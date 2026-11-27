// Automated, non-interactive benchmark harness for NTC Unity integrations.
// Enable in a standalone Player with: -ntcBenchmark
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace Ntc.Unity
{
    [DefaultExecutionOrder(32000)]
    public sealed class NtcBenchmarkRunner : MonoBehaviour
    {
        [Serializable]
        public sealed class RunReport
        {
            public string schema = "ntc-unity-benchmark/v1";
            public string utcTimestamp;
            public string unityVersion;
            public string operatingSystem;
            public string deviceModel;
            public string graphicsDeviceName;
            public string graphicsDeviceType;
            public int graphicsMemoryMB;
            public int width;
            public int height;
            public bool batchMode;
            public bool cameraRenderingObserved;
            public bool framePerformanceValid;
            public int cameraRenderCountAtEnd;
            public string cameraName;
            public string cameraPosition;
            public string cameraRotationEuler;
            public string startupTimerOrigin;
            public double startupToControllerReadySeconds;
            public string outputDirectory;
            public string[] commandLine;
            public bool renderDocCaptureRequested;
            public bool renderDocCaptureAttempted;
            public bool renderDocCaptureSucceeded;
            public string renderDocCaptureStatus;
            public string renderDocCapturePathTemplate;
            public ModeReport[] modes;
        }

        [Serializable]
        public sealed class ModeReport
        {
            public string mode;
            public bool completed;
            public string completionReason;
            public double switchToReadySeconds;
            public int warmupFrames;
            public int sampleFrames;
            public int cameraFramesDuringSamples;
            public MetricSummary frameIntervalMs;
            public MetricSummary cpuFrameMs;
            public MetricSummary mainThreadMs;
            public MetricSummary renderThreadMs;
            public MetricSummary gpuFrameMs;
            public string gpuTimingStatus;
            public bool gpuFenceAvailable;
            public bool gpuFencePassed;
            public double gpuQueueDrainSeconds = -1;
            public string gpuFenceStatus;
            public SceneSnapshot beforeSamples;
            public SceneSnapshot afterSamples;
        }

        [Serializable]
        public sealed class MetricSummary
        {
            public bool available;
            public int sampleCount;
            public double p50 = -1;
            public double p95 = -1;
            public double p99 = -1;
            public double max = -1;
        }

        [Serializable]
        public sealed class SceneSnapshot
        {
            public NtcMemorySnapshot memory;
            public bool originalsReleased;
            public int directMaterialGroups, originalFallbackGroups;
            public int totalGroups;
            public int bakedGroups;
            public int gpuDataGroups;
            public int failedGroups;
            public int visibleGroups;
            public int frustumGroups;
            public int streamingGroups;
            public int visibleStreamEntries;
            public int eligibleStreamEntries;
            public int residentStreamEntries;
            public int deferredStreamEntries;
            public int residentTiles;
            public int totalVisibleTiles;
            public long streamCacheBytes;
            public long streamCacheBudgetBytes;
        }

        sealed class FrameSample
        {
            public string mode;
            public int index;
            public double intervalMs = -1;
            public double cpuFrameMs = -1;
            public double mainThreadMs = -1;
            public double renderThreadMs = -1;
            public double gpuFrameMs = -1;
        }

        static readonly Stopwatch ProcessClock = Stopwatch.StartNew();
        static bool _started;
        static readonly BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        static readonly FieldInfo InitializedField = typeof(NtcSceneController).GetField("_initialized", PrivateInstance);
        static readonly FieldInfo InitializingField = typeof(NtcSceneController).GetField("_initializing", PrivateInstance);
        static readonly FieldInfo ApplyingField = typeof(NtcSceneController).GetField("_applyingMode", PrivateInstance);
        static readonly FieldInfo StreamerField = typeof(NtcSceneController).GetField("_streamer", PrivateInstance);

        readonly List<FrameSample> _rawSamples = new List<FrameSample>();
        ProfilerRecorder _mainThreadRecorder;
        ProfilerRecorder _renderThreadRecorder;
        NtcSceneController _controller;
        string _outputDirectory;
        int _warmupFrames;
        int _sampleFrames;
        float _initializationTimeout;
        float _modeTimeout;
        float _gpuFenceTimeout;
        bool _noQuit;
        bool _allowNoRenderedFrame;
        bool _renderDocCaptureRequested;
        bool _renderDocCaptureAttempted;
        bool _renderDocCaptureSucceeded;
        string _renderDocCaptureStatus = "not requested";
        string _renderDocCapturePathTemplate = "";
        Camera _lockedCamera;
        bool _lockCameraTransform;
        Vector3 _lockedCameraPosition;
        Quaternion _lockedCameraRotation;
        int _cameraRenderCount;
        readonly WaitForEndOfFrame _waitForEndOfFrame = new WaitForEndOfFrame();
        RenderTexture _offscreenTarget;
        RenderTexture _previousCameraTarget;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSplashScreen)]
        static void MarkManagedStartup()
        {
            // Native process bootstrap before this earliest managed callback is outside scope.
            ProcessClock.Restart();
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void AutoStart()
        {
            if (_started || !HasFlag("-ntcBenchmark")) return;
            _started = true;
            GameObject host = new GameObject("NTC Benchmark Runner");
            DontDestroyOnLoad(host);
            host.AddComponent<NtcBenchmarkRunner>();
        }

        IEnumerator Start()
        {
            Application.runInBackground = true;
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = -1;
            _warmupFrames = GetIntArg("-ntcBenchmarkWarmupFrames", 180, 1);
            _sampleFrames = GetIntArg("-ntcBenchmarkSampleFrames", 600, 10);
            _initializationTimeout = GetFloatArg("-ntcBenchmarkInitTimeout", 180f, 1f);
            _modeTimeout = GetFloatArg("-ntcBenchmarkModeTimeout", 180f, 1f);
            _gpuFenceTimeout = GetFloatArg("-ntcBenchmarkGpuFenceTimeout", 30f, 1f);
            _noQuit = HasFlag("-ntcBenchmarkNoQuit");
            _allowNoRenderedFrame = HasFlag("-ntcBenchmarkAllowNoRenderedFrame");
            _renderDocCaptureRequested = HasFlag("-ntcBenchmarkRenderDocCapture");
            _outputDirectory = ResolveOutputDirectory();

            Directory.CreateDirectory(_outputDirectory);
            UnityEngine.Debug.Log("[NTC Benchmark] Starting. Output: " + _outputDirectory);

            yield return null;
            _controller = FindObjectOfType<NtcSceneController>();
            if (_controller == null)
            {
                FinishWithError("No active NtcSceneController was found in scene " + SceneManager.GetActiveScene().path);
                yield break;
            }
            _controller.showPanel = false;
            _lockedCamera = Camera.main;
            _lockCameraTransform = !HasFlag("-ntcBenchmarkAllowCameraMotion");
            if (_lockedCamera != null)
            {
                // Occlusion results can vary with driver/window state. Frustum visibility is
                // sufficient for exercising the streamer's renderer.isVisible path.
                _lockedCamera.useOcclusionCulling = false;
                if (_lockCameraTransform)
                {
                    _lockedCameraPosition = _lockedCamera.transform.position;
                    _lockedCameraRotation = _lockedCamera.transform.rotation;
                }
            }

            double initStart = ProcessClock.Elapsed.TotalSeconds;
            while (!GetPrivateBool(_controller, InitializedField))
            {
                if (!GetPrivateBool(_controller, InitializingField) && Time.realtimeSinceStartup > 2f)
                {
                    FinishWithError("NtcSceneController stopped before initialization completed.");
                    yield break;
                }
                if (ProcessClock.Elapsed.TotalSeconds - initStart > _initializationTimeout)
                {
                    FinishWithError("NtcSceneController initialization timeout.");
                    yield break;
                }
                yield return null;
            }

            RenderPipelineManager.endCameraRendering += OnEndCameraRendering;
            EnsureNtcCameraFraming();
            ConfigureOffscreenRendering();
            for (int i = 0; i < 10; i++) yield return _waitForEndOfFrame;
            if (_lockedCamera == null || _cameraRenderCount == 0)
            {
                const string message = "No rendered frame was observed from the benchmark camera.";
                if (!_allowNoRenderedFrame)
                {
                    FinishWithError(message + " Run a normal hidden Player; do not pass -batchmode or -nographics. Use -ntcBenchmarkAllowNoRenderedFrame only for compute/resource validation.");
                    yield break;
                }
                UnityEngine.Debug.LogWarning("[NTC Benchmark] " + message + " Continuing because -ntcBenchmarkAllowNoRenderedFrame was supplied; frame-performance data is invalid.");
            }

            TryStartRecorders();
            RunReport report = CreateRunReport(ProcessClock.Elapsed.TotalSeconds);
            List<ModeReport> modeReports = new List<ModeReport>();
            NtcSceneController.NtcMode[] modes = ParseModes();

            for (int i = 0; i < modes.Length; i++)
            {
                ModeReport modeReport = new ModeReport { mode = modes[i].ToString() };
                yield return RunMode(modes[i], modeReport);
                modeReports.Add(modeReport);
            }

            report.modes = modeReports.ToArray();
            report.cameraRenderingObserved = _cameraRenderCount > 0;
            report.framePerformanceValid = report.cameraRenderingObserved;
            foreach (var modeReport in modeReports)
                report.framePerformanceValid &= modeReport.completed && modeReport.cameraFramesDuringSamples > 0;
            report.cameraRenderCountAtEnd = _cameraRenderCount;
            report.renderDocCaptureAttempted = _renderDocCaptureAttempted;
            report.renderDocCaptureSucceeded = _renderDocCaptureSucceeded;
            report.renderDocCaptureStatus = _renderDocCaptureStatus;
            report.renderDocCapturePathTemplate = _renderDocCapturePathTemplate;
            StopRecorders();
            WriteOutputs(report);
            UnityEngine.Debug.Log("[NTC Benchmark] Complete. JSON: " + Path.Combine(_outputDirectory, "benchmark.json"));
            bool completed = modeReports.Count > 0;
            foreach (var modeReport in modeReports)
                completed &= modeReport.completed && modeReport.gpuFencePassed
                    && modeReport.afterSamples != null && modeReport.afterSamples.failedGroups == 0;
            completed &= _allowNoRenderedFrame || report.framePerformanceValid;
            if (!_noQuit) Application.Quit(completed ? 0 : 1);
        }

        void LateUpdate()
        {
            if (_lockedCamera == null || !_lockCameraTransform) return;
            _lockedCamera.transform.SetPositionAndRotation(_lockedCameraPosition, _lockedCameraRotation);
        }

        void OnDestroy()
        {
            RenderPipelineManager.endCameraRendering -= OnEndCameraRendering;
            if (_lockedCamera != null && _lockedCamera.targetTexture == _offscreenTarget)
                _lockedCamera.targetTexture = _previousCameraTarget;
            if (_offscreenTarget != null)
            {
                _offscreenTarget.Release();
                Destroy(_offscreenTarget);
                _offscreenTarget = null;
            }
            StopRecorders();
        }

        void OnEndCameraRendering(ScriptableRenderContext context, Camera camera)
        {
            if (camera == _lockedCamera) _cameraRenderCount++;
        }

        IEnumerator RunMode(NtcSceneController.NtcMode mode, ModeReport report)
        {
            Stopwatch switchClock = Stopwatch.StartNew();
            _controller.SetMode(mode);
            yield return null;

            bool completed = false;
            string reason = "mode timeout";
            int stableFrames = 0;
            while (switchClock.Elapsed.TotalSeconds < _modeTimeout)
            {
                bool applying = GetPrivateBool(_controller, ApplyingField);
                SceneSnapshot snapshot = CaptureSnapshot();
                if (!applying && snapshot.failedGroups > 0)
                {
                    reason = "material preparation failed: " + snapshot.failedGroups + " group(s)";
                    break;
                }
                bool streamReady = mode != NtcSceneController.NtcMode.InferenceOnSample
                    || (snapshot.visibleStreamEntries > 0
                        && snapshot.eligibleStreamEntries > 0
                        && snapshot.residentStreamEntries >= snapshot.eligibleStreamEntries);
                if (mode == NtcSceneController.NtcMode.InferenceOnSampleDirect) streamReady = snapshot.directMaterialGroups > 0;

                // Capture while the tile streamer is still doing useful work, rather than after
                // it has settled. The capture itself spans the next Update and rendered frame so
                // RenderDoc can observe NTC compute dispatches as well as the consuming draw calls.
                bool streamWorkPending = mode == NtcSceneController.NtcMode.InferenceOnSample
                    && snapshot.visibleStreamEntries > 0
                    && snapshot.eligibleStreamEntries > 0
                    && (!streamReady || snapshot.residentTiles < snapshot.totalVisibleTiles);
                if (_renderDocCaptureRequested && !_renderDocCaptureAttempted && streamWorkPending)
                    yield return CaptureRenderDocStreamingFrame();

                if (!applying && streamReady) stableFrames++; else stableFrames = 0;
                if (stableFrames >= 5)
                {
                    completed = true;
                    reason = mode != NtcSceneController.NtcMode.InferenceOnSample
                        ? "mode applied"
                        : "mode applied and budget-eligible visible residency settled";
                    break;
                }
                yield return _waitForEndOfFrame;
            }

            report.completed = completed;
            report.completionReason = reason;
            report.switchToReadySeconds = switchClock.Elapsed.TotalSeconds;
            report.beforeSamples = CaptureSnapshot();
            report.warmupFrames = _warmupFrames;
            report.sampleFrames = _sampleFrames;

            if (completed)
                yield return DrainGpuQueue(report);
            else
            {
                report.gpuFenceAvailable = false;
                report.gpuFencePassed = false;
                report.gpuFenceStatus = "not attempted because mode did not become ready";
            }

            for (int i = 0; i < _warmupFrames; i++)
            {
                FrameTimingManager.CaptureFrameTimings();
                yield return _waitForEndOfFrame;
            }

            List<double> interval = new List<double>(_sampleFrames);
            List<double> cpu = new List<double>(_sampleFrames);
            List<double> main = new List<double>(_sampleFrames);
            List<double> render = new List<double>(_sampleFrames);
            List<double> gpu = new List<double>(_sampleFrames);
            Stopwatch frameClock = Stopwatch.StartNew();
            int cameraFramesBeforeSamples = _cameraRenderCount;

            for (int i = 0; i < _sampleFrames; i++)
            {
                frameClock.Restart();
                FrameTimingManager.CaptureFrameTimings();
                yield return null;

                FrameSample sample = CaptureFrameSample(mode.ToString(), i, frameClock.Elapsed.TotalMilliseconds);
                _rawSamples.Add(sample);
                AddIfAvailable(interval, sample.intervalMs);
                AddIfAvailable(cpu, sample.cpuFrameMs);
                AddIfAvailable(main, sample.mainThreadMs);
                AddIfAvailable(render, sample.renderThreadMs);
                AddIfAvailable(gpu, sample.gpuFrameMs);
            }

            report.frameIntervalMs = Summarize(interval);
            report.cameraFramesDuringSamples = _cameraRenderCount - cameraFramesBeforeSamples;
            report.cpuFrameMs = Summarize(cpu);
            report.mainThreadMs = Summarize(main);
            report.renderThreadMs = Summarize(render);
            report.gpuFrameMs = Summarize(gpu);
            report.gpuTimingStatus = report.gpuFrameMs.available
                ? "FrameTimingManager returned GPU frame times"
                : "unavailable; use frameInterval/cpu/mainThread/renderThread metrics or an external GPU profiler (not substituted as GPU time)";
            report.afterSamples = CaptureSnapshot();
        }

        IEnumerator DrainGpuQueue(ModeReport report)
        {
            if (!SystemInfo.supportsGraphicsFence)
            {
                report.gpuFenceAvailable = false;
                report.gpuFencePassed = false;
                report.gpuFenceStatus = "unavailable: SystemInfo.supportsGraphicsFence is false";
                yield break;
            }

            GraphicsFence fence;
            CommandBuffer commandBuffer = new CommandBuffer { name = "NTC Benchmark GPU queue drain fence" };
            try
            {
                fence = commandBuffer.CreateGraphicsFence(
                    GraphicsFenceType.AsyncQueueSynchronisation,
                    SynchronisationStageFlags.ComputeProcessing);
                Graphics.ExecuteCommandBuffer(commandBuffer);
                report.gpuFenceAvailable = true;
            }
            catch (Exception e)
            {
                report.gpuFenceAvailable = false;
                report.gpuFencePassed = false;
                report.gpuFenceStatus = "unavailable: " + e.GetType().Name + ": " + e.Message;
                commandBuffer.Release();
                yield break;
            }
            commandBuffer.Release();

            Stopwatch drainClock = Stopwatch.StartNew();
            bool passed = false;
            while (drainClock.Elapsed.TotalSeconds < _gpuFenceTimeout)
            {
                try { passed = fence.passed; }
                catch (Exception e)
                {
                    report.gpuFenceAvailable = false;
                    report.gpuFencePassed = false;
                    report.gpuQueueDrainSeconds = drainClock.Elapsed.TotalSeconds;
                    report.gpuFenceStatus = "unavailable while polling: " + e.GetType().Name + ": " + e.Message;
                    yield break;
                }
                if (passed) break;
                yield return null;
            }
            report.gpuQueueDrainSeconds = drainClock.Elapsed.TotalSeconds;
            report.gpuFencePassed = passed;
            report.gpuFenceStatus = passed
                ? "passed; GPU queue completed work submitted before the fence"
                : "timeout waiting for GPU fence";
        }

        IEnumerator CaptureRenderDocStreamingFrame()
        {
            _renderDocCaptureAttempted = true;
            string captureDirectory = Path.Combine(_outputDirectory, "renderdoc");
            _renderDocCapturePathTemplate = Path.Combine(captureDirectory, "ntc-onsample");
            Directory.CreateDirectory(captureDirectory);

            RenderDocApi api;
            string error;
            if (!RenderDocApi.TryCreate(out api, out error))
            {
                _renderDocCaptureStatus = error;
                UnityEngine.Debug.LogWarning("[NTC Benchmark] RenderDoc capture skipped: " + error);
                yield break;
            }

            bool captureStarted = false;
            try
            {
                api.SetCaptureFilePathTemplate(_renderDocCapturePathTemplate);
                api.StartFrameCapture();
                captureStarted = api.IsFrameCapturing();
                if (!captureStarted)
                {
                    _renderDocCaptureStatus = "RenderDoc API was available, but StartFrameCapture did not begin a capture.";
                    UnityEngine.Debug.LogWarning("[NTC Benchmark] " + _renderDocCaptureStatus);
                }
            }
            catch (Exception e)
            {
                _renderDocCaptureSucceeded = false;
                _renderDocCaptureStatus = e.GetType().Name + ": " + e.Message;
                UnityEngine.Debug.LogWarning("[NTC Benchmark] RenderDoc capture failed: " + _renderDocCaptureStatus);
            }
            if (!captureStarted) yield break;

            // Cross the next player-loop Update so the active capture includes the tile streamer's
            // compute submissions, then finish only after that frame has rendered.
            yield return null;
            yield return _waitForEndOfFrame;
            try
            {
                _renderDocCaptureSucceeded = api.EndFrameCapture();
                _renderDocCaptureStatus = _renderDocCaptureSucceeded
                    ? "captured one on-Sample streaming frame including the next Update and render"
                    : "EndFrameCapture reported failure";
                if (_renderDocCaptureSucceeded)
                    UnityEngine.Debug.Log("[NTC Benchmark] RenderDoc capture succeeded. Path template: " + _renderDocCapturePathTemplate);
                else
                    UnityEngine.Debug.LogWarning("[NTC Benchmark] RenderDoc capture failed: " + _renderDocCaptureStatus);
            }
            catch (Exception e)
            {
                _renderDocCaptureSucceeded = false;
                _renderDocCaptureStatus = e.GetType().Name + ": " + e.Message;
                UnityEngine.Debug.LogWarning("[NTC Benchmark] RenderDoc capture failed: " + _renderDocCaptureStatus);
            }
        }

        FrameSample CaptureFrameSample(string mode, int index, double intervalMs)
        {
            FrameSample sample = new FrameSample { mode = mode, index = index, intervalMs = intervalMs };
            FrameTiming[] timings = new FrameTiming[1];
            if (FrameTimingManager.GetLatestTimings(1, timings) > 0)
            {
                if (timings[0].cpuFrameTime > 0) sample.cpuFrameMs = timings[0].cpuFrameTime;
                if (timings[0].gpuFrameTime > 0) sample.gpuFrameMs = timings[0].gpuFrameTime;
            }
            sample.mainThreadMs = RecorderMilliseconds(_mainThreadRecorder);
            sample.renderThreadMs = RecorderMilliseconds(_renderThreadRecorder);
            return sample;
        }

        SceneSnapshot CaptureSnapshot()
        {
            SceneSnapshot s = new SceneSnapshot();
            s.memory = _controller.MemorySnapshot;
            s.originalsReleased = _controller.OriginalsReleased;
            NtcSceneController.MaterialGroup[] groups = _controller.groups ?? new NtcSceneController.MaterialGroup[0];
            Plane[] planes = _lockedCamera != null ? GeometryUtility.CalculateFrustumPlanes(_lockedCamera) : null;
            s.totalGroups = groups.Length;
            for (int i = 0; i < groups.Length; i++)
            {
                NtcSceneController.MaterialGroup g = groups[i];
                if (g == null) continue;
                if (g.hasNtcFile) s.bakedGroups++;
                if (g.hasNtcData) s.gpuDataGroups++;
                if (g.status != null && g.status.StartsWith("direct NTC PBR ready",StringComparison.Ordinal)) s.directMaterialGroups++;
                if (g.status != null && g.status.StartsWith("direct original fallback:",StringComparison.Ordinal)) s.originalFallbackGroups++;
                if (IsFailureStatus(g.status)) s.failedGroups++;
                bool visible = false;
                bool inFrustum = false;
                if (g.renderers != null)
                    for (int r = 0; r < g.renderers.Length; r++)
                    {
                        Renderer renderer = g.renderers[r];
                        if (renderer == null) continue;
                        bool intersects = planes != null
                            ? GeometryUtility.TestPlanesAABB(planes, renderer.bounds)
                            : renderer.isVisible;
                        if (intersects) { visible = true; inFrustum = true; }
                    }
                if (visible) s.visibleGroups++;
                if (inFrustum) s.frustumGroups++;
                if (g.streamActive) s.streamingGroups++;
            }

            NtcTileStreamer streamer = StreamerField != null ? StreamerField.GetValue(_controller) as NtcTileStreamer : null;
            if (streamer != null)
            {
                s.streamCacheBytes = streamer.TotalCacheVram();
                s.streamCacheBudgetBytes = streamer.MaxCacheBytes;
                s.eligibleStreamEntries = streamer.EligibleVisibleEntryCount;
                s.residentStreamEntries = streamer.ResidentVisibleEntryCount;
                s.deferredStreamEntries = streamer.DeferredVisibleEntryCount;
                for (int i = 0; i < groups.Length; i++)
                {
                    NtcSceneController.MaterialGroup g = groups[i];
                    if (g == null) continue;
                    string id = string.IsNullOrEmpty(g.ntcFilePath) ? g.name : g.ntcFilePath;
                    NtcTileStreamer.Entry e = streamer.Get(id);
                    if (e == null || !e.visibleNow) continue;
                    s.visibleStreamEntries++;
                    if (e.selectedForBudget)
                    {
                        s.residentTiles += e.residentCount;
                        s.totalVisibleTiles += e.tileResident != null ? e.tileResident.Length : 0;
                    }
                }
            }
            return s;
        }

        static bool IsFailureStatus(string status)
        {
            if (string.IsNullOrWhiteSpace(status)) return false;

            // Match status words, not arbitrary substrings: "deferred" contains "err" but is
            // a normal VRAM-budget state. Keep the short standalone "err" token because the
            // controller intentionally reports real failures as "load err:" / "apply err:".
            int start = -1;
            for (int i = 0; i <= status.Length; i++)
            {
                bool isWord = i < status.Length && char.IsLetter(status[i]);
                if (isWord && start < 0)
                {
                    start = i;
                }
                else if (!isWord && start >= 0)
                {
                    string word = status.Substring(start, i - start);
                    if (word.StartsWith("fail", StringComparison.OrdinalIgnoreCase)
                        || word.Equals("err", StringComparison.OrdinalIgnoreCase)
                        || word.StartsWith("error", StringComparison.OrdinalIgnoreCase)
                        || word.StartsWith("missing", StringComparison.OrdinalIgnoreCase)
                        || word.StartsWith("invalid", StringComparison.OrdinalIgnoreCase)
                        || word.StartsWith("exception", StringComparison.OrdinalIgnoreCase))
                        return true;
                    start = -1;
                }
            }
            return false;
        }

        void TryStartRecorders()
        {
            try { _mainThreadRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "Main Thread", 1); }
            catch (Exception e) { UnityEngine.Debug.LogWarning("[NTC Benchmark] Main Thread recorder unavailable: " + e.Message); }
            try { _renderThreadRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "Render Thread", 1); }
            catch (Exception e) { UnityEngine.Debug.LogWarning("[NTC Benchmark] Render Thread recorder unavailable: " + e.Message); }
        }

        void EnsureNtcCameraFraming()
        {
            if (_lockedCamera == null || _controller == null || _controller.groups == null) return;

            Plane[] planes = GeometryUtility.CalculateFrustumPlanes(_lockedCamera);
            Bounds combined = new Bounds();
            bool hasBounds = false;
            bool alreadyFramed = false;
            for (int gi = 0; gi < _controller.groups.Length; gi++)
            {
                NtcSceneController.MaterialGroup group = _controller.groups[gi];
                if (group == null || group.renderers == null) continue;
                for (int ri = 0; ri < group.renderers.Length; ri++)
                {
                    Renderer renderer = group.renderers[ri];
                    if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy) continue;
                    if (!hasBounds) { combined = renderer.bounds; hasBounds = true; }
                    else combined.Encapsulate(renderer.bounds);
                    if (GeometryUtility.TestPlanesAABB(planes, renderer.bounds)) alreadyFramed = true;
                }
            }
            if (!hasBounds || alreadyFramed) return;

            Vector3 viewDirection = combined.center - _lockedCamera.transform.position;
            if (viewDirection.sqrMagnitude < 0.001f) viewDirection = Vector3.forward;
            viewDirection.Normalize();
            float halfFov = Mathf.Max(_lockedCamera.fieldOfView * 0.5f * Mathf.Deg2Rad, 0.1f);
            float distance = combined.extents.magnitude / Mathf.Sin(halfFov) + combined.extents.magnitude;
            _lockedCamera.transform.position = combined.center - viewDirection * Mathf.Max(distance, 2f);
            _lockedCamera.transform.rotation = Quaternion.LookRotation(viewDirection, Vector3.up);
            _lockedCamera.farClipPlane = Mathf.Max(_lockedCamera.farClipPlane, distance * 3f);
            if (_lockCameraTransform)
            {
                _lockedCameraPosition = _lockedCamera.transform.position;
                _lockedCameraRotation = _lockedCamera.transform.rotation;
            }
            UnityEngine.Debug.Log("[NTC Benchmark] Reframed camera to NTC renderer bounds: " + combined);
        }

        void ConfigureOffscreenRendering()
        {
            if (_lockedCamera == null) return;
            int width = Mathf.Max(Screen.width, 1);
            int height = Mathf.Max(Screen.height, 1);
            _previousCameraTarget = _lockedCamera.targetTexture;
            _offscreenTarget = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default)
            {
                name = "NTC Benchmark Offscreen Target",
                antiAliasing = 1,
                useMipMap = false,
                autoGenerateMips = false
            };
            _offscreenTarget.Create();
            _lockedCamera.targetTexture = _offscreenTarget;
            UnityEngine.Debug.Log(string.Format(CultureInfo.InvariantCulture,
                "[NTC Benchmark] Rendering to deterministic offscreen target {0}x{1}.", width, height));
        }

        void StopRecorders()
        {
            if (_mainThreadRecorder.Valid) _mainThreadRecorder.Dispose();
            if (_renderThreadRecorder.Valid) _renderThreadRecorder.Dispose();
        }

        RunReport CreateRunReport(double startupSeconds)
        {
            Camera cam = _lockedCamera != null ? _lockedCamera : Camera.main;
            return new RunReport
            {
                utcTimestamp = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                unityVersion = Application.unityVersion,
                operatingSystem = SystemInfo.operatingSystem,
                deviceModel = SystemInfo.deviceModel,
                graphicsDeviceName = SystemInfo.graphicsDeviceName,
                graphicsDeviceType = SystemInfo.graphicsDeviceType.ToString(),
                graphicsMemoryMB = SystemInfo.graphicsMemorySize,
                width = Screen.width,
                height = Screen.height,
                batchMode = Application.isBatchMode,
                cameraName = cam != null ? cam.name : "unavailable",
                cameraPosition = cam != null ? VectorString(cam.transform.position) : "unavailable",
                cameraRotationEuler = cam != null ? VectorString(cam.transform.eulerAngles) : "unavailable",
                startupTimerOrigin = "RuntimeInitializeLoadType.BeforeSplashScreen",
                startupToControllerReadySeconds = startupSeconds,
                outputDirectory = _outputDirectory,
                commandLine = Environment.GetCommandLineArgs(),
                renderDocCaptureRequested = _renderDocCaptureRequested,
                renderDocCaptureStatus = _renderDocCaptureStatus
            };
        }

        // Minimal binding to renderdoc_app.h. There is no link-time dependency: renderdoc.dll
        // must already be injected into the Windows Player. renderdoc_app.h defines RENDERDOC_CC
        // as __cdecl on Windows. API 1.0.0 is requested because the slots used here are ABI-stable
        // through 1.6.0; slot indices match RENDERDOC_API_1_6_0 in RenderDoc 1.39.
        sealed class RenderDocApi
        {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            const int ApiVersion100 = 10000;
            const int SetCaptureFilePathTemplateSlot = 11;
            const int StartFrameCaptureSlot = 19;
            const int IsFrameCapturingSlot = 20;
            const int EndFrameCaptureSlot = 21;

            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            delegate int GetApiDelegate(int version, out IntPtr apiPointers);

            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            delegate void SetCapturePathDelegate(IntPtr utf8Path);

            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            delegate void StartFrameCaptureDelegate(IntPtr device, IntPtr window);

            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            delegate uint IsFrameCapturingDelegate();

            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            delegate uint EndFrameCaptureDelegate(IntPtr device, IntPtr window);

            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
            static extern IntPtr GetModuleHandle(string moduleName);

            [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = false)]
            static extern IntPtr GetProcAddress(IntPtr module, string procedureName);

            readonly SetCapturePathDelegate _setCapturePath;
            readonly StartFrameCaptureDelegate _startFrameCapture;
            readonly IsFrameCapturingDelegate _isFrameCapturing;
            readonly EndFrameCaptureDelegate _endFrameCapture;

            RenderDocApi(IntPtr apiPointers)
            {
                _setCapturePath = GetFunction<SetCapturePathDelegate>(apiPointers, SetCaptureFilePathTemplateSlot);
                _startFrameCapture = GetFunction<StartFrameCaptureDelegate>(apiPointers, StartFrameCaptureSlot);
                _isFrameCapturing = GetFunction<IsFrameCapturingDelegate>(apiPointers, IsFrameCapturingSlot);
                _endFrameCapture = GetFunction<EndFrameCaptureDelegate>(apiPointers, EndFrameCaptureSlot);
            }
#endif

            public static bool TryCreate(out RenderDocApi api, out string error)
            {
                api = null;
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
                if (Application.platform != RuntimePlatform.WindowsPlayer)
                {
                    error = "supported only by a Windows standalone Player";
                    return false;
                }

                IntPtr module = GetModuleHandle("renderdoc.dll");
                if (module == IntPtr.Zero)
                {
                    error = "renderdoc.dll is not injected; launch the Player through RenderDoc";
                    return false;
                }
                IntPtr entryPoint = GetProcAddress(module, "RENDERDOC_GetAPI");
                if (entryPoint == IntPtr.Zero)
                {
                    error = "injected renderdoc.dll does not export RENDERDOC_GetAPI";
                    return false;
                }

                try
                {
                    GetApiDelegate getApi = (GetApiDelegate)Marshal.GetDelegateForFunctionPointer(entryPoint, typeof(GetApiDelegate));
                    IntPtr apiPointers;
                    if (getApi(ApiVersion100, out apiPointers) != 1 || apiPointers == IntPtr.Zero)
                    {
                        error = "RENDERDOC_GetAPI rejected API version 1.0.0";
                        return false;
                    }
                    api = new RenderDocApi(apiPointers);
                    error = "";
                    return true;
                }
                catch (Exception e)
                {
                    error = "could not bind renderdoc_app API: " + e.GetType().Name + ": " + e.Message;
                    return false;
                }
#else
                error = "RenderDoc capture integration is disabled on this platform";
                return false;
#endif
            }

            public void SetCaptureFilePathTemplate(string path)
            {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
                byte[] bytes = Encoding.UTF8.GetBytes(path + "\0");
                IntPtr nativePath = Marshal.AllocHGlobal(bytes.Length);
                try
                {
                    Marshal.Copy(bytes, 0, nativePath, bytes.Length);
                    _setCapturePath(nativePath);
                }
                finally { Marshal.FreeHGlobal(nativePath); }
#endif
            }

            public void StartFrameCapture()
            {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
                _startFrameCapture(IntPtr.Zero, IntPtr.Zero);
#endif
            }

            public bool IsFrameCapturing()
            {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
                return _isFrameCapturing() != 0;
#else
                return false;
#endif
            }

            public bool EndFrameCapture()
            {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
                return _endFrameCapture(IntPtr.Zero, IntPtr.Zero) != 0;
#else
                return false;
#endif
            }

#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            static T GetFunction<T>(IntPtr apiPointers, int slot) where T : class
            {
                IntPtr function = Marshal.ReadIntPtr(apiPointers, slot * IntPtr.Size);
                if (function == IntPtr.Zero) throw new MissingMethodException("RenderDoc API slot " + slot + " is null.");
                return Marshal.GetDelegateForFunctionPointer(function, typeof(T)) as T;
            }
#endif
        }

        void WriteOutputs(RunReport report)
        {
            File.WriteAllText(Path.Combine(_outputDirectory, "benchmark.json"), JsonUtility.ToJson(report, true));
            using (StreamWriter w = new StreamWriter(Path.Combine(_outputDirectory, "summary.csv"), false))
            {
                w.WriteLine("mode,completed,completion_reason,switch_to_ready_s,warmup_frames,sample_frames,frame_p50_ms,frame_p95_ms,frame_p99_ms,frame_max_ms,cpu_p50_ms,cpu_p95_ms,cpu_p99_ms,cpu_max_ms,gpu_available,gpu_p50_ms,gpu_p95_ms,gpu_p99_ms,gpu_max_ms,gpu_fence_available,gpu_fence_passed,gpu_queue_drain_s,gpu_fence_status,visible_groups,visible_stream_entries,eligible_stream_entries,resident_stream_entries,deferred_stream_entries,resident_tiles,total_eligible_tiles,stream_cache_bytes,stream_cache_budget_bytes");
                for (int i = 0; i < report.modes.Length; i++)
                {
                    ModeReport m = report.modes[i];
                    w.WriteLine(string.Join(",", new string[] {
                        Csv(m.mode), m.completed ? "true" : "false", Csv(m.completionReason), F(m.switchToReadySeconds), m.warmupFrames.ToString(), m.sampleFrames.ToString(),
                        F(m.frameIntervalMs.p50), F(m.frameIntervalMs.p95), F(m.frameIntervalMs.p99), F(m.frameIntervalMs.max),
                        F(m.cpuFrameMs.p50), F(m.cpuFrameMs.p95), F(m.cpuFrameMs.p99), F(m.cpuFrameMs.max),
                        m.gpuFrameMs.available ? "true" : "false", F(m.gpuFrameMs.p50), F(m.gpuFrameMs.p95), F(m.gpuFrameMs.p99), F(m.gpuFrameMs.max),
                        m.gpuFenceAvailable ? "true" : "false", m.gpuFencePassed ? "true" : "false", F(m.gpuQueueDrainSeconds), Csv(m.gpuFenceStatus),
                        m.afterSamples.visibleGroups.ToString(), m.afterSamples.visibleStreamEntries.ToString(),
                        m.afterSamples.eligibleStreamEntries.ToString(), m.afterSamples.residentStreamEntries.ToString(),
                        m.afterSamples.deferredStreamEntries.ToString(), m.afterSamples.residentTiles.ToString(),
                        m.afterSamples.totalVisibleTiles.ToString(), m.afterSamples.streamCacheBytes.ToString(),
                        m.afterSamples.streamCacheBudgetBytes.ToString()
                    }));
                }
            }
            using (StreamWriter w = new StreamWriter(Path.Combine(_outputDirectory, "frames.csv"), false))
            {
                w.WriteLine("mode,sample,frame_interval_ms,cpu_frame_ms,main_thread_ms,render_thread_ms,gpu_frame_ms");
                for (int i = 0; i < _rawSamples.Count; i++)
                {
                    FrameSample s = _rawSamples[i];
                    w.WriteLine(string.Join(",", Csv(s.mode), s.index.ToString(), F(s.intervalMs), F(s.cpuFrameMs), F(s.mainThreadMs), F(s.renderThreadMs), F(s.gpuFrameMs)));
                }
            }
        }

        void FinishWithError(string message)
        {
            UnityEngine.Debug.LogError("[NTC Benchmark] " + message);
            Directory.CreateDirectory(_outputDirectory);
            File.WriteAllText(Path.Combine(_outputDirectory, "benchmark-error.txt"), message + Environment.NewLine);
            if (!_noQuit) Application.Quit(2);
        }

        static MetricSummary Summarize(List<double> values)
        {
            MetricSummary s = new MetricSummary { available = values.Count > 0, sampleCount = values.Count };
            if (values.Count == 0) return s;
            values.Sort();
            s.p50 = Percentile(values, 0.50);
            s.p95 = Percentile(values, 0.95);
            s.p99 = Percentile(values, 0.99);
            s.max = values[values.Count - 1];
            return s;
        }

        static double Percentile(List<double> sorted, double p)
        {
            double position = (sorted.Count - 1) * p;
            int lower = (int)Math.Floor(position);
            int upper = (int)Math.Ceiling(position);
            if (lower == upper) return sorted[lower];
            return sorted[lower] + (sorted[upper] - sorted[lower]) * (position - lower);
        }

        static void AddIfAvailable(List<double> values, double value)
        {
            if (value >= 0 && !double.IsNaN(value) && !double.IsInfinity(value)) values.Add(value);
        }

        static double RecorderMilliseconds(ProfilerRecorder recorder)
        {
            return recorder.Valid && recorder.LastValue > 0 ? recorder.LastValue * 1e-6 : -1;
        }

        static bool GetPrivateBool(object target, FieldInfo field)
        {
            return field != null && target != null && (bool)field.GetValue(target);
        }

        static bool HasFlag(string flag)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++) if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        static string GetArg(string name, string fallback)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i + 1 < args.Length; i++) if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
            return fallback;
        }

        static int GetIntArg(string name, int fallback, int minimum)
        {
            int parsed;
            return int.TryParse(GetArg(name, ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) ? Math.Max(parsed, minimum) : fallback;
        }

        static float GetFloatArg(string name, float fallback, float minimum)
        {
            float parsed;
            return float.TryParse(GetArg(name, ""), NumberStyles.Float, CultureInfo.InvariantCulture, out parsed) ? Mathf.Max(parsed, minimum) : fallback;
        }

        static string ResolveOutputDirectory()
        {
            string requested = GetArg("-ntcBenchmarkOutput", "");
            if (!string.IsNullOrEmpty(requested)) return Path.GetFullPath(requested);
            return Path.Combine(Application.persistentDataPath, "NtcBenchmarks", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
        }

        static NtcSceneController.NtcMode[] ParseModes()
        {
            string text = GetArg("-ntcBenchmarkModes", "Original,InferenceOnLoad,InferenceOnSample,InferenceOnSampleDirect");
            string[] names = text.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
            List<NtcSceneController.NtcMode> modes = new List<NtcSceneController.NtcMode>();
            for (int i = 0; i < names.Length; i++)
            {
                NtcSceneController.NtcMode mode;
                if (Enum.TryParse(names[i].Trim(), true, out mode)) modes.Add(mode);
            }
            if (modes.Count == 0) modes.Add(NtcSceneController.NtcMode.Original);
            return modes.ToArray();
        }

        static string F(double value) { return value < 0 ? "" : value.ToString("0.######", CultureInfo.InvariantCulture); }
        static string Csv(string value) { return "\"" + (value ?? "").Replace("\"", "\"\"") + "\""; }
        static string VectorString(Vector3 value)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0:0.######},{1:0.######},{2:0.######}", value.x, value.y, value.z);
        }
    }
}
