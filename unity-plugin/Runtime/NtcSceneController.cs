// NtcSceneController.cs
// Multi-material NTC controller: scans the scene for all materials, loads their .ntc
// data, and drives the three NTC modes (Original / on-Load / on-Sample) with an
// on-screen button panel (no keyboard required) and live debug stats.
//
// Each material becomes a "MaterialGroup" with its own .ntc file, decompressed PNGs,
// and cached original / on-Load / on-Sample materials. Mode switches apply to all
// groups simultaneously.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.SceneManagement;
using Ntc.Packing;

namespace Ntc.Unity
{
    [DisallowMultipleComponent]
    public class NtcSceneController : MonoBehaviour
    {
        public enum NtcMode { Original = 0, InferenceOnLoad = 1, InferenceOnSample = 2 }
        public enum NtcViewMode { Lit = 0, BaseColor = 1, Normal = 2, Metallic = 3, Roughness = 4 }

        [Serializable]
        public class MaterialGroup
        {
            public string name;
            public Renderer[] renderers;
            public string ntcFilePath = "";   // relative to Assets/, e.g. "NtcPlugin/NtcData/Arches.ntc"
            public Texture2D ntcColor, ntcNormal, ntcMetalness, ntcAO, ntcSmoothness, ntcAlpha;

            // runtime-only (not serialized)
            [NonSerialized] public Material[] originalMaterials;
            [NonSerialized] public Material onLoadMaterial;
            [NonSerialized] public Material onSampleMaterial;
            [NonSerialized] public NtcBuiltAssets assets;
            [NonSerialized] public NtcContainer container;
            [NonSerialized] public bool hasNtcFile;
            [NonSerialized] public bool hasNtcData;
            [NonSerialized] public bool onSampleLoadAttempted;
            [NonSerialized] public Texture2D metallicGlossMap;
            [NonSerialized] public Texture2D colorWithAlphaMap;   // legacy-PNG-path: Color.png RGB + Alpha.png R merged
            [NonSerialized] public string status;

            // compute-decompress runtime state
            [NonSerialized] public NtcPbrChannelLayout pbrLayout;
            [NonSerialized] public bool hasPbrLayout;
            [NonSerialized] public NtcDecompressTargets onLoadTargets; // mode2: full-res GPU-decompressed cache
            [NonSerialized] public Material streamMaterial;            // mode3: material bound to the streamed cache
            [NonSerialized] public Material debugViewMaterial;         // channel-view material wrapping the current source material
            [NonSerialized] public int streamBoundVersion;            // last NtcTileStreamer.Entry.version we bound
            [NonSerialized] public bool streamActive;
        }

        [Header("Mode")]
        public NtcMode mode = NtcMode.Original;

        [Header("Material Groups")]
        [Tooltip("If empty, auto-scans the scene on Start for all renderers and maps them by material name.")]
        public MaterialGroup[] groups = Array.Empty<MaterialGroup>();

        [Header("Settings")]
        public Shader onSampleShader;
        [Tooltip("Optional debug shader for showing Base/Normal/Metallic/Roughness channels. If null, Shader.Find(\"NTC/PBRChannelDebug_URP\") is used.")]
        public Shader pbrDebugShader;
        public bool onLoadSwapAllMaps = true;
        public string ntcDataPath = "NtcPlugin/NtcData";
        [Tooltip("Optional prefix prepended to .ntc file names when looking them up (e.g. \"Sponza\" if your bake output uses Sponza{name}.ntc). Empty = file must be {materialName}.ntc.")]
        public string ntcFilePrefix = "";

        [Header("Compute Decompress (mode2 on-Load / mode3 on-Sample)")]
        [Tooltip("NtcDecompress.compute. If null, loaded from Resources/NtcDecompress at runtime.")]
        public ComputeShader decompressCompute;
        [Tooltip("mode2: decompress the whole .ntc on the GPU at load time into ordinary textures (no PNG).")]
        public bool onLoadUseCompute = true;
        [Tooltip("mode3: tile-streaming parameters.")]
        public int streamTileSize = 256;
        public int streamCapResolution = 1024;
        public int streamBudgetTilesPerFrame = 8;
        public float streamEvictSeconds = 5f;

        NtcDecompressor _decompressor;
        NtcTileStreamer _streamer;

        [Header("UI")]
        public bool showPanel = true;
        public KeyCode togglePanelKey = KeyCode.F1;
        public NtcViewMode viewMode = NtcViewMode.Lit;

        // URP Lit property names
        const string kBaseMap = "_BaseMap";
        const string kBumpMap = "_BumpMap";
        const string kMetallicGlossMap = "_MetallicGlossMap";
        const string kOcclusionMap = "_OcclusionMap";
        const string kMetallic = "_Metallic";
        const string kSmoothness = "_Smoothness";

        Vector2 _scrollPos;
        bool _showMaterials = true;
        bool _showStats = true;
        bool _initialized;
        bool _initializing;
        float _loadProgress;
        string _loadStatus = "Not started";

        // Mode switching is applied progressively (one group per frame) so heavy work --
        // decoding on-Load PNGs and building on-Sample latent textures/weight buffers --
        // never blocks the main thread for multiple seconds (the old "freeze" on switching).
        Coroutine _applyRoutine;
        bool _applyingMode;
        float _applyProgress;
        string _applyStatus = "";
        int _bakedNtcGroups;
        int _loadedNtcGroups;
        int _failedNtcGroups;
        string _initError;

        void OnEnable()
        {
            if (!_initialized && !_initializing)
                StartCoroutine(InitializeRoutine());
        }

        void Start()
        {
            if (_initialized)
                StartApplyMode(mode);
        }

        void Update()
        {
            if (Input.GetKeyDown(togglePanelKey)) showPanel = !showPanel;
            if (_initialized && mode == NtcMode.InferenceOnSample)
                UpdateStreaming();
        }

        public void SetMode(NtcMode m)
        {
            mode = m;
            if (_initialized)
                StartApplyMode(m);
        }

        public void SetViewMode(NtcViewMode m)
        {
            if (viewMode == m) return;
            viewMode = m;
            if (_initialized)
                RefreshViewModeMaterials();
        }

        /// <summary>
        /// Debug-only: force the BaseColor sRGB->linear handling on/off across on-Load and
        /// on-Sample (both compute-based paths, plus the legacy per-pixel fallback shader), to
        /// A/B-test a suspected color-space/brightness mismatch against the "Original" material.
        /// Invalidates already-decoded caches so the change is visible immediately without a
        /// full mode switch.
        /// </summary>
        public void SetColorSpaceDebugMode(NtcDecompressor.ColorSpaceMode m)
        {
            if (NtcDecompressor.DebugColorSpaceMode == m) return;
            NtcDecompressor.DebugColorSpaceMode = m;

            if (groups != null)
            {
                foreach (var g in groups)
                {
                    if (g == null) continue;
                    // on-Load (mode2): drop the cached full-res decode so ApplyOnLoadCompute redoes it.
                    if (g.onLoadTargets != null)
                    {
                        g.onLoadTargets.Release();
                        g.onLoadTargets = null;
                    }
                    // legacy on-Sample fragment shader: just re-push the material float, no rebuild needed.
                    if (g.onSampleMaterial != null && g.assets != null)
                        BindOnSampleMaterial(g.onSampleMaterial, g.assets);
                }
            }
            // on-Sample streamed tile cache: force every resident tile to re-decompress next Tick.
            if (_streamer != null) _streamer.InvalidateAllTiles();

            if (_initialized && mode == NtcMode.InferenceOnLoad)
                StartApplyMode(mode); // re-run on-Load so the cleared cache is rebuilt right away
        }

        // -------------------------------------------------------------------- init

        IEnumerator InitializeRoutine()
        {
            if (_initialized || _initializing) yield break;
            _initializing = true;
            _loadProgress = 0f;
            _loadStatus = "Scanning scene materials...";
            _bakedNtcGroups = 0;
            _loadedNtcGroups = 0;
            _failedNtcGroups = 0;
            _initError = null;

            yield return null;

            bool scanOk = true;
            try
            {
                if (onSampleShader == null)
                    onSampleShader = Shader.Find("NTC/InferenceOnSample_URP");

                if (_decompressor == null)
                {
                    _decompressor = NtcDecompressor.Create(decompressCompute);
                    if (_decompressor != null && _decompressor.IsValid)
                    {
                        _streamer = new NtcTileStreamer(_decompressor)
                        {
                            TileSize = Mathf.Max(streamTileSize, 32),
                            CapResolution = Mathf.Max(streamCapResolution, 128),
                            BudgetTilesPerFrame = Mathf.Max(streamBudgetTilesPerFrame, 1),
                            EvictAfterSeconds = Mathf.Max(streamEvictSeconds, 0f),
                        };
                        Debug.Log("[NTC] Compute decompressor ready (DP4A hardware capable: "
                            + NtcDecompressor.Dp4aHardwareSupported + ").");
                    }
                    else
                    {
                        Debug.LogWarning("[NTC] Compute decompressor unavailable; on-Load falls back to PNG, on-Sample to per-pixel shader.");
                    }
                }

                if (groups == null || groups.Length == 0)
                    AutoScanScene();
            }
            catch (Exception e)
            {
                scanOk = false;
                _initError = e.Message;
                _loadStatus = "NTC scan failed";
                _initializing = false;
                Debug.LogError("[NTC] Init failed: " + e);
            }

            if (!scanOk)
                yield break;

            int total = Mathf.Max(groups != null ? groups.Length : 0, 1);
            for (int i = 0; groups != null && i < groups.Length; i++)
            {
                MaterialGroup g = groups[i];
                _loadStatus = string.Format("Checking NTC bake {0}/{1}: {2}", i + 1, groups.Length, g != null ? g.name : "<null>");

                try
                {
                    if (g != null)
                        InitGroup(g);
                }
                catch (Exception e)
                {
                    if (g != null)
                        g.status = "load err: " + e.Message;
                    _failedNtcGroups++;
                    Debug.LogError("[NTC] Failed to init material group: " + e);
                }

                if (g != null && g.hasNtcFile) _bakedNtcGroups++;
                if (g != null && g.hasNtcData) _loadedNtcGroups++;
                else if (g != null && !string.Equals(g.status, "not baked", StringComparison.OrdinalIgnoreCase) && !g.status.StartsWith("load err", StringComparison.OrdinalIgnoreCase)) _failedNtcGroups++;

                _loadProgress = (float)(i + 1) / total;
                yield return null;
            }

            _initialized = true;
            _initializing = false;
            _loadStatus = string.Format("NTC bake check: {0} baked, {1} GPU-loaded, {2} failed, {3} total", _bakedNtcGroups, _loadedNtcGroups, _failedNtcGroups, groups != null ? groups.Length : 0);
            StartApplyMode(mode);
        }

        /// <summary>
        /// Scans the currently open scene for all Renderers/materials and rebuilds <see cref="groups"/>.
        /// Safe to call both in Play mode (from InitializeRoutine) and in Edit mode (from the
        /// NTC/Add Scene Controller and NTC/Rescan Scene Materials menu items). Editor callers should
        /// mark the object/scene dirty afterwards so the scan result is actually saved -- otherwise any
        /// change made while in Play mode is discarded by Unity as soon as Play Mode stops, which is why
        /// the groups list in the Inspector would appear to "never auto-populate".
        /// </summary>
        public void ScanSceneMaterials()
        {
            AutoScanScene();
        }

        void AutoScanScene()
        {
            var byMat = new Dictionary<string, List<Renderer>>();
#if UNITY_2022_2_OR_NEWER
            Renderer[] all = UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None);
#else
            Renderer[] all = UnityEngine.Object.FindObjectsOfType<Renderer>();
#endif
            foreach (Renderer r in all)
            {
                Material m = r.sharedMaterial;
                if (m == null) continue;
                string key = m.name;
                if (!byMat.ContainsKey(key)) byMat[key] = new List<Renderer>();
                byMat[key].Add(r);
            }

            var list = new List<MaterialGroup>();
            foreach (var kv in byMat)
            {
                var g = new MaterialGroup
                {
                    name = kv.Key,
                    renderers = kv.Value.ToArray()
                };
                // Check .ntc bake status right away (also in Edit mode), so the Inspector /
                // NTC/Rescan Scene Materials reflect real bake state without needing Play.
                CheckNtcFile(g);
                list.Add(g);
            }
            groups = list.ToArray();
            Debug.Log("[NTC] Auto-scanned " + groups.Length + " material groups ("
                + list.FindAll(g => g.hasNtcFile).Count + " already baked).");
        }

        /// <summary>
        /// Looks up whether a .ntc file exists for this group's material name and fills in
        /// ntcFilePath/hasNtcFile. Cheap (just a file existence check), safe to call in Edit mode.
        /// </summary>
        void CheckNtcFile(MaterialGroup g)
        {
            string dataAbs = ToAbsolute(ntcDataPath);
            string ntcPath = FindNtcFile(dataAbs, g.name);
            if (ntcPath == null)
            {
                g.ntcFilePath = "";
                g.hasNtcFile = false;
                g.status = "not baked";
                return;
            }
            g.ntcFilePath = MakeRelative(ntcPath);
            g.hasNtcFile = true;
        }

        void InitGroup(MaterialGroup g)
        {
            g.status = "checking";
            g.hasNtcData = false;
            g.onSampleLoadAttempted = false;

            // Cache original materials
            if (g.renderers == null)
                g.renderers = Array.Empty<Renderer>();
            g.originalMaterials = new Material[g.renderers.Length];
            for (int i = 0; i < g.renderers.Length; i++)
                g.originalMaterials[i] = g.renderers[i] != null ? g.renderers[i].sharedMaterial : null;

            // Re-check .ntc bake status now (it may have changed since the last Edit-mode scan,
            // e.g. you just baked it in NTC/Bake Window without re-scanning).
            CheckNtcFile(g);

            // IMPORTANT: do NOT decode any PNGs or parse any .ntc here. Startup used to eagerly
            // Texture2D.LoadImage() every decompressed PNG for all 24 materials up front, which is
            // what made "the load before entering a mode" so slow. We now only do the cheap file
            // existence check. Actual GPU/CPU work is deferred to the moment a mode is entered:
            //   - on-Load  : LoadDecompressedTextures() runs lazily inside ApplyOnLoad
            //   - on-Sample: EnsureOnSampleData() runs lazily inside ApplyOnSample
            // and both are driven one-group-per-frame by ApplyModeRoutine so nothing freezes.
            g.status = g.hasNtcFile ? "baked (.ntc)" : "not baked";
        }

        string FindNtcFile(string dataDir, string matName)
        {
            // Try {name}.ntc
            string p = Path.Combine(dataDir, matName + ".ntc");
            if (File.Exists(p)) return p;
            // Try {prefix}{name}.ntc (e.g. SponzaBricks.ntc when ntcFilePrefix="Sponza")
            if (!string.IsNullOrEmpty(ntcFilePrefix))
            {
                p = Path.Combine(dataDir, ntcFilePrefix + matName + ".ntc");
                if (File.Exists(p)) return p;
            }
            // Try {name} with spaces removed
            p = Path.Combine(dataDir, matName.Replace(" ", "") + ".ntc");
            if (File.Exists(p)) return p;
            return null;
        }

        void LoadDecompressedTextures(MaterialGroup g, string dataDir)
        {
            // Decompressed dir matches .ntc file name first (SponzaBricks.ntc -> Decompressed/SponzaBricks/),
            // then material name, then flat fallback.
            string ntcName = Path.GetFileNameWithoutExtension(g.ntcFilePath);
            string decompDir = Path.Combine(dataDir, "Decompressed", ntcName);
            if (!Directory.Exists(decompDir))
                decompDir = Path.Combine(dataDir, "Decompressed", g.name);
            if (!Directory.Exists(decompDir))
                decompDir = Path.Combine(dataDir, "Decompressed");

            if (g.ntcColor == null)      g.ntcColor = LoadPng(Path.Combine(decompDir, "Color.png"), true);
            if (g.ntcNormal == null)     g.ntcNormal = LoadPng(Path.Combine(decompDir, "Normal.png"), false);
            if (g.ntcMetalness == null)  g.ntcMetalness = LoadPng(Path.Combine(decompDir, "Metalness.png"), false);
            if (g.ntcAO == null)         g.ntcAO = LoadPng(Path.Combine(decompDir, "AO.png"), false);
            if (g.ntcSmoothness == null) g.ntcSmoothness = LoadPng(Path.Combine(decompDir, "Smoothness.png"), false);
            if (g.ntcAlpha == null)      g.ntcAlpha = LoadPng(Path.Combine(decompDir, "Alpha.png"), false);
        }

        static Texture2D LoadPng(string path, bool sRGB)
        {
            if (!File.Exists(path)) return null;
            byte[] bytes = File.ReadAllBytes(path);
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, true, !sRGB);
            tex.LoadImage(bytes);
            tex.wrapMode = TextureWrapMode.Repeat;
            return tex;
        }

        Texture2D GetOrCreateMetallicGlossMap(MaterialGroup g)
        {
            if (g.metallicGlossMap != null) return g.metallicGlossMap;
            if (g.ntcMetalness == null && g.ntcSmoothness == null) return null;

            Texture2D baseTex = g.ntcMetalness != null ? g.ntcMetalness : g.ntcSmoothness;
            int width = Mathf.Max(baseTex.width, 1);
            int height = Mathf.Max(baseTex.height, 1);
            Color32[] metal = SafeGetPixels(g.ntcMetalness);
            Color32[] smooth = SafeGetPixels(g.ntcSmoothness);
            var packed = new Color32[width * height];

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    byte m = SampleChannel(g.ntcMetalness, metal, x, y, width, height, 0);
                    byte s = SampleChannel(g.ntcSmoothness, smooth, x, y, width, height, 255);
                    packed[y * width + x] = new Color32(m, 0, 0, s);
                }
            }

            g.metallicGlossMap = new Texture2D(width, height, TextureFormat.RGBA32, true, true);
            g.metallicGlossMap.name = g.name + " NTC MetallicSmoothness";
            g.metallicGlossMap.wrapMode = TextureWrapMode.Repeat;
            g.metallicGlossMap.filterMode = FilterMode.Bilinear;
            g.metallicGlossMap.SetPixels32(packed);
            g.metallicGlossMap.Apply(true, false);
            return g.metallicGlossMap;
        }

        // Legacy-PNG on-Load path only (compute path writes alpha directly into the RenderTexture
        // in NtcDecompress.compute). ntc-cli decompresses each NTC texture into its own separate
        // PNG file (Color.png, Alpha.png, ...), so "Color.png" itself has no usable alpha -- merge
        // Alpha.png's red channel into Color.png's alpha channel here to get a BaseMap texture
        // whose .a URP Lit's alpha-clip can actually test against.
        Texture2D MergeAlphaIntoColorMap(MaterialGroup g)
        {
            if (g.colorWithAlphaMap != null) return g.colorWithAlphaMap;
            if (g.ntcColor == null || g.ntcAlpha == null) return g.ntcColor;

            int width = Mathf.Max(g.ntcColor.width, 1);
            int height = Mathf.Max(g.ntcColor.height, 1);
            Color32[] color = SafeGetPixels(g.ntcColor);
            Color32[] alpha = SafeGetPixels(g.ntcAlpha);
            if (color == null) return g.ntcColor;
            var packed = new Color32[width * height];

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    Color32 c = color[y * width + x];
                    byte a = SampleChannel(g.ntcAlpha, alpha, x, y, width, height, 255);
                    packed[y * width + x] = new Color32(c.r, c.g, c.b, a);
                }
            }

            g.colorWithAlphaMap = new Texture2D(width, height, TextureFormat.RGBA32, true, false);
            g.colorWithAlphaMap.name = g.name + " NTC ColorWithAlpha";
            g.colorWithAlphaMap.wrapMode = TextureWrapMode.Repeat;
            g.colorWithAlphaMap.filterMode = FilterMode.Bilinear;
            g.colorWithAlphaMap.SetPixels32(packed);
            g.colorWithAlphaMap.Apply(true, false);
            return g.colorWithAlphaMap;
        }

        static Color32[] SafeGetPixels(Texture2D tex)
        {
            if (tex == null) return null;
            try { return tex.GetPixels32(); }
            catch { return null; }
        }

        static byte SampleChannel(Texture2D tex, Color32[] pixels, int x, int y, int targetWidth, int targetHeight, byte fallback)
        {
            if (tex == null || pixels == null || pixels.Length == 0) return fallback;
            int sx = Mathf.Clamp((int)((x + 0.5f) * tex.width / Mathf.Max(targetWidth, 1)), 0, tex.width - 1);
            int sy = Mathf.Clamp((int)((y + 0.5f) * tex.height / Mathf.Max(targetHeight, 1)), 0, tex.height - 1);
            return pixels[sy * tex.width + sx].r;
        }

        // -------------------------------------------------------------------- mode apply

        void ApplyMode(NtcMode m)
        {
            StartApplyMode(m);
        }

        /// <summary>
        /// Kicks off a progressive (coroutine) mode switch. Any in-flight switch is cancelled first
        /// so rapid button mashing never stacks up work. Original mode is cheap (just re-points
        /// sharedMaterial), while on-Load/on-Sample do heavy lazy loading -- so we spread that heavy
        /// work across frames instead of doing all 24 materials in a single frame (the freeze).
        /// </summary>
        void StartApplyMode(NtcMode m)
        {
            if (!Application.isPlaying)
            {
                // Edit mode: no coroutines. Apply synchronously (Original only is expected here).
                ApplyModeImmediate(m);
                return;
            }
            if (_applyRoutine != null)
                StopCoroutine(_applyRoutine);
            _applyRoutine = StartCoroutine(ApplyModeRoutine(m));
        }

        void ApplyModeImmediate(NtcMode m)
        {
            if (groups == null) return;
            foreach (var g in groups)
            {
                if (g == null) continue;
                ApplyOne(g, m);
            }
        }

        IEnumerator ApplyModeRoutine(NtcMode m)
        {
            _applyingMode = true;
            _applyProgress = 0f;
            int total = groups != null ? groups.Length : 0;

            // Fast, always-safe first step: revert everything to Original immediately so the scene
            // is never left showing a half-applied state while heavy modes stream in group by group.
            if (m != NtcMode.Original)
            {
                _applyStatus = "Switching to " + m + " ...";
                if (groups != null)
                    foreach (var g in groups)
                        if (g != null) ApplyOriginal(g);
                yield return null;
            }

            for (int i = 0; groups != null && i < groups.Length; i++)
            {
                MaterialGroup g = groups[i];
                if (g != null)
                {
                    _applyStatus = string.Format("Applying {0}: {1}/{2}  {3}", m, i + 1, total, g.name);
                    try
                    {
                        ApplyOne(g, m);
                    }
                    catch (Exception e)
                    {
                        g.status = "apply err: " + e.Message;
                        Debug.LogError("[NTC] Apply " + m + " failed for " + g.name + ": " + e);
                    }
                }

                _applyProgress = total > 0 ? (float)(i + 1) / total : 1f;

                // Original is light -> batch several per frame. on-Load/on-Sample are heavy
                // (PNG decode / .ntc parse + latent upload) -> yield after every single group.
                if (m == NtcMode.Original)
                {
                    if ((i & 7) == 7) yield return null;
                }
                else
                {
                    yield return null;
                }
            }

            _applyStatus = m + " applied";
            _applyingMode = false;
            _applyProgress = 1f;
            _applyRoutine = null;
        }

        void ApplyOne(MaterialGroup g, NtcMode m)
        {
            // Streaming (mode3) is only alive while on-Sample is the active mode.
            if (m != NtcMode.InferenceOnSample)
                g.streamActive = false;

            switch (m)
            {
                case NtcMode.Original:          ApplyOriginal(g); break;
                case NtcMode.InferenceOnLoad:   ApplyOnLoad(g); break;
                case NtcMode.InferenceOnSample: ApplyOnSample(g); break;
            }
        }

        void ApplyOriginal(MaterialGroup g)
        {
            if (g.renderers == null || g.originalMaterials == null) return;
            int count = Mathf.Min(g.renderers.Length, g.originalMaterials.Length);
            for (int i = 0; i < count; i++)
                if (g.renderers[i] != null)
                    g.renderers[i].sharedMaterial = GetViewMaterial(g, g.originalMaterials[i]);
        }

        void RestoreOriginalMaterialRefs(MaterialGroup g)
        {
            if (g.renderers == null || g.originalMaterials == null) return;
            int count = Mathf.Min(g.renderers.Length, g.originalMaterials.Length);
            for (int i = 0; i < count; i++)
                if (g.renderers[i] != null)
                    g.renderers[i].sharedMaterial = g.originalMaterials[i];
        }

        void ApplyOnLoad(MaterialGroup g)
        {
            if (!g.hasNtcFile) { g.status = "not baked"; ApplyOriginal(g); return; }

            // Preferred path: decompress the whole .ntc on the GPU (compute) into ordinary
            // textures, right here in the load coroutine. No pre-baked PNG, no CPU decode.
            if (onLoadUseCompute && _decompressor != null && _decompressor.IsValid)
            {
                if (ApplyOnLoadCompute(g))
                    return;
                // else fall through to the legacy PNG path below.
            }

            // Legacy fallback: runtime-decode the pre-baked decompressed PNGs.
            if (g.ntcColor == null && g.ntcNormal == null && g.ntcMetalness == null && g.ntcAO == null)
                LoadDecompressedTextures(g, ToAbsolute(ntcDataPath));

            if (g.ntcColor == null) { g.status = "no Color.png"; ApplyOriginal(g); return; }

            if (g.onLoadMaterial == null)
            {
                Material src = g.originalMaterials != null && g.originalMaterials.Length > 0 ? g.originalMaterials[0] : null;
                if (src == null) return;
                g.onLoadMaterial = new Material(src) { name = src.name + " (NTC on-Load)" };

                // Cutout mask: the "Color.png" decompressed by ntc-cli has no meaningful alpha
                // (it's opaque RGB), and the mask was baked as a SEPARATE "Alpha.png" scalar
                // texture (see NtcBakeWindow.AddAlphaCutoutFromBaseMap). Merge it into BaseMap's
                // alpha channel here so URP Lit's alpha-clip (which reads BaseMap.a, not a
                // separate texture slot) actually has real cutout data instead of a constant 1.
                Texture2D baseMapTex = g.ntcAlpha != null ? MergeAlphaIntoColorMap(g) : g.ntcColor;
                if (g.onLoadMaterial.HasProperty(kBaseMap)) g.onLoadMaterial.SetTexture(kBaseMap, baseMapTex);

                if (onLoadSwapAllMaps)
                {
                    // Normal map: a runtime RGBA32 PNG (X in R, Y in G, alpha=255) is decoded correctly
                    // by URP's UnpackNormalmapRGorAG (it does x *= a, so a=255 leaves R untouched, then
                    // reconstructs Z from x,y). Skipping the normal was what made cloth/fabric lighting
                    // look flat and wrong -- fabrics carry almost all their surface detail in the normal.
                    if (g.ntcNormal != null && g.onLoadMaterial.HasProperty(kBumpMap))
                    {
                        g.ntcNormal.wrapMode = TextureWrapMode.Repeat;
                        g.onLoadMaterial.SetTexture(kBumpMap, g.ntcNormal);
                        g.onLoadMaterial.EnableKeyword("_NORMALMAP");
                        if (g.onLoadMaterial.HasProperty("_BumpScale")) g.onLoadMaterial.SetFloat("_BumpScale", 1f);
                    }

                    if (g.ntcAO != null && g.onLoadMaterial.HasProperty(kOcclusionMap))
                    {
                        g.onLoadMaterial.SetTexture(kOcclusionMap, g.ntcAO);
                        g.onLoadMaterial.EnableKeyword("_OCCLUSIONMAP");
                    }

                    bool haveMetal = g.ntcMetalness != null;
                    bool haveSmooth = g.ntcSmoothness != null;
                    if (haveMetal || haveSmooth)
                    {
                        Texture2D metallicGloss = GetOrCreateMetallicGlossMap(g);
                        if (metallicGloss != null && g.onLoadMaterial.HasProperty(kMetallicGlossMap))
                        {
                            g.onLoadMaterial.SetTexture(kMetallicGlossMap, metallicGloss);
                            g.onLoadMaterial.EnableKeyword("_METALLICSPECGLOSSMAP");
                            // When a channel is actually driven by the packed map, the scalar multiplier
                            // must be 1. When a channel is MISSING (e.g. cloth has no smoothness map), we
                            // must NOT force it to 1 -- doing so made non-metal cloth render fully smooth
                            // (shiny/plastic). Instead keep the original material's scalar so the look
                            // matches Original as closely as the available maps allow.
                            if (g.onLoadMaterial.HasProperty(kMetallic))
                                g.onLoadMaterial.SetFloat(kMetallic, haveMetal ? 1f : SafeGetFloat(src, kMetallic, 0f));
                            if (g.onLoadMaterial.HasProperty(kSmoothness))
                                g.onLoadMaterial.SetFloat(kSmoothness, haveSmooth ? 1f : SafeGetFloat(src, kSmoothness, 0.5f));
                        }
                    }
                }
            }
            if (g.renderers == null) return;
            Material display = GetViewMaterial(g, g.onLoadMaterial);
            for (int i = 0; i < g.renderers.Length; i++)
                if (g.renderers[i] != null) g.renderers[i].sharedMaterial = display;
            g.status = "on-Load active";
        }

        static float SafeGetFloat(Material m, string prop, float fallback)
        {
            return (m != null && m.HasProperty(prop)) ? m.GetFloat(prop) : fallback;
        }

        // ------------------------------------------------------- compute on-Load (mode2)

        /// <summary>
        /// mode2: GPU-decompress the entire .ntc into full-resolution RenderTextures and bind
        /// them to a cloned URP Lit material. Runs during the (progress-barred) mode-switch
        /// coroutine, i.e. the "loading before entering the map" phase the user asked for.
        /// Returns false if data/compute is unavailable so the caller can fall back to PNGs.
        /// </summary>
        bool ApplyOnLoadCompute(MaterialGroup g)
        {
            if (!EnsureGpuData(g) || !g.hasPbrLayout)
                return false;

            if (g.onLoadTargets == null)
            {
                int w = Mathf.Max(g.assets.Constants.imageWidth, 1);
                int h = Mathf.Max(g.assets.Constants.imageHeight, 1);
                g.onLoadTargets = _decompressor.AllocateTargets(w, h, g.pbrLayout, true);
                _decompressor.DecompressFull(g.assets, g.pbrLayout, g.onLoadTargets, 0);
                NtcDecompressor.GenerateMips(g.onLoadTargets);
            }

            Material src = g.originalMaterials != null && g.originalMaterials.Length > 0 ? g.originalMaterials[0] : null;
            if (src == null) return false;

            if (g.onLoadMaterial == null)
                g.onLoadMaterial = new Material(src) { name = src.name + " (NTC on-Load compute)" };
            BindPbrTargetsToMaterial(g.onLoadMaterial, src, g.onLoadTargets, g.pbrLayout);

            if (g.renderers != null)
            {
                Material display = GetViewMaterial(g, g.onLoadMaterial);
                for (int i = 0; i < g.renderers.Length; i++)
                    if (g.renderers[i] != null) g.renderers[i].sharedMaterial = display;
            }

            g.status = "on-Load active (compute)";
            return true;
        }

        /// <summary>
        /// Bind a set of GPU-decompressed PBR RenderTextures onto a URP Lit material clone.
        /// Shared by mode2 (full-res cache) and mode3 (streamed capped cache). Keep the
        /// routing identical for both modes: BaseColor.rgb, Normal.rgb, Mask.r/a, AO.g.
        /// The channel indices come from the .ntc texture metadata, so old assets baked
        /// without scalar channelSwizzle must be rebaked instead of patched here.
        /// </summary>
        void BindPbrTargetsToMaterial(Material mat, Material src, NtcDecompressTargets t, NtcPbrChannelLayout layout)
        {
            if (mat == null || t == null) return;

            if (t.BaseColor != null && mat.HasProperty(kBaseMap))
                mat.SetTexture(kBaseMap, t.BaseColor);

            if (onLoadSwapAllMaps)
            {
                if (t.Normal != null && mat.HasProperty(kBumpMap))
                {
                    mat.SetTexture(kBumpMap, t.Normal);
                    mat.EnableKeyword("_NORMALMAP");
                    if (mat.HasProperty("_BumpScale")) mat.SetFloat("_BumpScale", 1f);
                }
                if (t.AO != null && mat.HasProperty(kOcclusionMap))
                {
                    mat.SetTexture(kOcclusionMap, t.AO);
                    mat.EnableKeyword("_OCCLUSIONMAP");
                }
                if (t.Mask != null && mat.HasProperty(kMetallicGlossMap))
                {
                    mat.SetTexture(kMetallicGlossMap, t.Mask);
                    mat.EnableKeyword("_METALLICSPECGLOSSMAP");
                    if (mat.HasProperty(kMetallic)) mat.SetFloat(kMetallic, layout.Metal >= 0 ? 1f : SafeGetFloat(src, kMetallic, 0f));
                    if (mat.HasProperty(kSmoothness)) mat.SetFloat(kSmoothness, layout.Smooth >= 0 ? 1f : SafeGetFloat(src, kSmoothness, 0.5f));
                }
            }
        }

        Material GetViewMaterial(MaterialGroup g, Material source)
        {
            if (viewMode == NtcViewMode.Lit || source == null)
                return source;

            if (pbrDebugShader == null)
                pbrDebugShader = Shader.Find("NTC/PBRChannelDebug_URP");
            if (pbrDebugShader == null)
                return source;

            if (g.debugViewMaterial == null || g.debugViewMaterial.shader != pbrDebugShader)
            {
                DestroyUnityObject(g.debugViewMaterial);
                g.debugViewMaterial = new Material(pbrDebugShader) { name = g.name + " (NTC channel view)" };
            }

            BindDebugViewMaterial(g.debugViewMaterial, source);
            g.debugViewMaterial.SetFloat("_NtcDebugMode", (float)viewMode);
            return g.debugViewMaterial;
        }

        // Must branch EXACTLY like URP's real LitInput.hlsl SampleMetallicSpecGloss(): that
        // function's behavior is gated on the _METALLICSPECGLOSSMAP shader KEYWORD, not on
        // "does the _MetallicGlossMap slot merely have a texture assigned". A material can have
        // a texture in the slot with the keyword disabled (e.g. right after a mode switch before
        // EnableKeyword runs) and the real shader would still use the pure _Metallic/_Smoothness
        // scalars in that case. Getting this wrong (and separately, unconditionally multiplying
        // the sampled metallic by the _Metallic scalar, which the real shader never does when the
        // map is bound) is what caused the 2026-07-10 "metal overcompensation" debug-view-only bug:
        // most Sponza materials have _Metallic=0 (Ceiling/Bricks/Floor/Column_A/B/Details/Ivy/
        // Lion_*/Vase_*/Roof), so Original showed 0 metal in the debug view while on-Load (which
        // forces the scalar to 1 in BindPbrTargetsToMaterial) suddenly showed the raw texture value
        // -- a swing that never existed in the actual lit render.
        static void BindDebugViewMaterial(Material dst, Material src)
        {
            if (dst == null || src == null) return;
            CopyTextureProperty(src, dst, kBaseMap, Texture2D.whiteTexture);
            CopyTextureProperty(src, dst, kBumpMap, Texture2D.normalTexture);
            CopyTextureProperty(src, dst, kMetallicGlossMap, Texture2D.whiteTexture);
            CopyTextureProperty(src, dst, kOcclusionMap, Texture2D.whiteTexture);
            if (dst.HasProperty(kMetallic)) dst.SetFloat(kMetallic, SafeGetFloat(src, kMetallic, 0f));
            if (dst.HasProperty(kSmoothness)) dst.SetFloat(kSmoothness, SafeGetFloat(src, kSmoothness, 0.5f));
            if (dst.HasProperty("_BumpScale")) dst.SetFloat("_BumpScale", SafeGetFloat(src, "_BumpScale", 1f));
            bool hasGlossMap = src.HasProperty(kMetallicGlossMap) && src.IsKeywordEnabled("_METALLICSPECGLOSSMAP");
            if (dst.HasProperty("_NtcHasGlossMap")) dst.SetFloat("_NtcHasGlossMap", hasGlossMap ? 1f : 0f);
        }

        static void CopyTextureProperty(Material src, Material dst, string prop, Texture fallback)
        {
            if (!dst.HasProperty(prop)) return;
            Texture tex = (src.HasProperty(prop) && src.GetTexture(prop) != null) ? src.GetTexture(prop) : fallback;
            dst.SetTexture(prop, tex);
            if (src.HasProperty(prop))
            {
                dst.SetTextureScale(prop, src.GetTextureScale(prop));
                dst.SetTextureOffset(prop, src.GetTextureOffset(prop));
            }
        }

        void RefreshViewModeMaterials()
        {
            if (groups == null) return;
            foreach (var g in groups)
            {
                if (g == null) continue;
                if (mode == NtcMode.Original)
                {
                    ApplyOriginal(g);
                }
                else if (mode == NtcMode.InferenceOnLoad && g.onLoadMaterial != null)
                {
                    AssignGroupMaterial(g, GetViewMaterial(g, g.onLoadMaterial));
                }
                else if (mode == NtcMode.InferenceOnSample)
                {
                    string id = string.IsNullOrEmpty(g.ntcFilePath) ? g.name : g.ntcFilePath;
                    NtcTileStreamer.Entry e = _streamer != null ? _streamer.Get(id) : null;
                    if (g.streamActive && g.streamMaterial != null && e != null && e.targets != null && e.residentCount > 0)
                        AssignGroupMaterial(g, GetViewMaterial(g, g.streamMaterial));
                    else if (g.onSampleMaterial != null)
                        AssignGroupMaterial(g, GetViewMaterial(g, g.onSampleMaterial));
                    else
                        ApplyOriginal(g);
                }
                else
                {
                    ApplyOriginal(g);
                }
            }
        }

        static void AssignGroupMaterial(MaterialGroup g, Material material)
        {
            if (g == null || g.renderers == null || material == null) return;
            for (int i = 0; i < g.renderers.Length; i++)
                if (g.renderers[i] != null)
                    g.renderers[i].sharedMaterial = material;
        }

        /// <summary>Ensure the .ntc container + GPU assets + PBR layout are loaded for this group.</summary>
        bool EnsureGpuData(MaterialGroup g)
        {
            return EnsureOnSampleData(g);
        }

        void ApplyOnSample(MaterialGroup g)
        {
            // Preferred path: tile-streamed GPU decompression into a capped-resolution cache,
            // then shade as ordinary textures. Only visible materials get decompressed, and the
            // per-frame work is budgeted by NtcTileStreamer (driven from Update()).
            if (_streamer != null && _decompressor != null && _decompressor.IsValid)
            {
                if (ApplyOnSampleStreamed(g))
                    return;
                // else fall through to the legacy per-pixel shader.
            }

            if (onSampleShader == null)
            { g.status = "missing on-Sample shader"; ApplyOriginal(g); return; }

            if (!EnsureOnSampleData(g))
            {
                // Keep the scene visible if the .ntc cannot be read by this POC path.
                ApplyOriginal(g);
                return;
            }

            if (g.onSampleMaterial == null)
            {
                g.onSampleMaterial = new Material(onSampleShader) { name = g.name + " (NTC on-Sample)" };
                BindOnSampleMaterial(g.onSampleMaterial, g.assets);
            }
            if (g.renderers == null) return;
            Material display = GetViewMaterial(g, g.onSampleMaterial);
            for (int i = 0; i < g.renderers.Length; i++)
                if (g.renderers[i] != null) g.renderers[i].sharedMaterial = display;
        }

        /// <summary>
        /// mode3: register the material with the tile streamer and prepare its streamed material.
        /// Actual decompression + material binding + renderer swap happen progressively in
        /// UpdateStreaming() as visible tiles become resident. Until then renderers keep the
        /// original material so the scene is never black.
        /// </summary>
        bool ApplyOnSampleStreamed(MaterialGroup g)
        {
            if (!EnsureGpuData(g) || !g.hasPbrLayout)
                return false;

            string id = string.IsNullOrEmpty(g.ntcFilePath) ? g.name : g.ntcFilePath;
            if (!_streamer.IsRegistered(id))
                _streamer.Register(id, g.assets, g.pbrLayout, g.renderers);

            Material src = g.originalMaterials != null && g.originalMaterials.Length > 0 ? g.originalMaterials[0] : null;
            if (src == null) return false;
            if (g.streamMaterial == null)
                g.streamMaterial = new Material(src) { name = src.name + " (NTC on-Sample stream)" };

            g.streamActive = true;
            g.streamBoundVersion = -1;
            g.status = "on-Sample streaming";

            // Show original until the first tiles arrive (UpdateStreaming swaps it in).
            ApplyOriginal(g);
            return true;
        }

        /// <summary>Per-frame streaming pump for mode3: decompress budgeted tiles, rebind + swap.</summary>
        void UpdateStreaming()
        {
            if (_streamer == null || groups == null) return;
            Camera cam = Camera.main;
            _streamer.Tick(cam);

            for (int gi = 0; gi < groups.Length; gi++)
            {
                MaterialGroup g = groups[gi];
                if (g == null || !g.streamActive || g.streamMaterial == null) continue;

                string id = string.IsNullOrEmpty(g.ntcFilePath) ? g.name : g.ntcFilePath;
                NtcTileStreamer.Entry e = _streamer.Get(id);
                if (e == null) continue;

                bool resident = e.targets != null && e.residentCount > 0;
                if (resident)
                {
                    if (e.version != g.streamBoundVersion)
                    {
                        Material src = g.originalMaterials != null && g.originalMaterials.Length > 0 ? g.originalMaterials[0] : null;
                        BindPbrTargetsToMaterial(g.streamMaterial, src, e.targets, g.pbrLayout);
                        g.streamBoundVersion = e.version;
                    }
                    if (g.renderers != null)
                    {
                        Material display = GetViewMaterial(g, g.streamMaterial);
                        for (int i = 0; i < g.renderers.Length; i++)
                            if (g.renderers[i] != null && g.renderers[i].sharedMaterial != display)
                                g.renderers[i].sharedMaterial = display;
                    }
                }
                else
                {
                    // Cache was evicted (hidden too long) or not yet streamed: show original.
                    if (g.renderers != null && g.originalMaterials != null)
                        for (int i = 0; i < g.renderers.Length && i < g.originalMaterials.Length; i++)
                            if (g.renderers[i] != null && (g.renderers[i].sharedMaterial == g.streamMaterial || g.renderers[i].sharedMaterial == g.debugViewMaterial))
                                g.renderers[i].sharedMaterial = GetViewMaterial(g, g.originalMaterials[i]);
                    g.streamBoundVersion = -1;
                }
            }
        }

        bool EnsureOnSampleData(MaterialGroup g)
        {
            if (g.hasNtcData && g.assets != null && g.assets.LatentTexture != null && g.assets.WeightBuffer != null)
                return true;
            if (!g.hasNtcFile || string.IsNullOrEmpty(g.ntcFilePath))
            {
                g.status = "not baked";
                return false;
            }

            g.onSampleLoadAttempted = true;
            string ntcAbsPath = ToAbsolute(g.ntcFilePath);
            if (!File.Exists(ntcAbsPath))
            {
                g.status = "missing .ntc";
                return false;
            }

            ReleaseGroupGpuAssets(g);
            try
            {
                _loadStatus = "Loading on-Sample NTC: " + g.name;
                g.container = NtcContainerReader.Load(ntcAbsPath);
                g.assets = NtcAssetBuilder.Build(g.container, 0);
                g.hasNtcData = g.assets != null && g.assets.LatentTexture != null && g.assets.WeightBuffer != null;
                if (g.hasNtcData)
                {
                    g.pbrLayout = NtcAssetBuilder.BuildPbrLayout(g.container);
                    g.hasPbrLayout = true;
                }
                g.status = g.hasNtcData ? "on-Sample ready" : "on-Sample build failed";
                if (g.hasNtcData) _loadedNtcGroups++;
                return g.hasNtcData;
            }
            catch (Exception e)
            {
                ReleaseGroupGpuAssets(g);
                g.status = "on-Sample load err: " + e.Message;
                Debug.LogError("[NTC] Failed to load on-Sample data for " + g.name + ": " + e);
                return false;
            }
        }

        void BindOnSampleMaterial(Material mat, NtcBuiltAssets assets)
        {
            NtcTextureSetConstants c = assets.Constants;
            mat.SetTexture("_NtcLatents", assets.LatentTexture);
            mat.SetBuffer("_NtcWeights", assets.WeightBuffer);

            var mips = new Vector4[16];
            for (int i = 0; i < 16; i++)
            {
                Vector4Int m = c.colorMips[i];
                mips[i] = new Vector4(m.x, IntBitsToFloat(m.y), IntBitsToFloat(m.z), 0f);
            }
            mat.SetVectorArray("_NtcColorMips", mips);
            mat.SetVector("_NtcWeightOffsets", ToVec4(c.networkWeightOffsets));
            mat.SetVector("_NtcBiasOffsets", ToVec4(c.networkBiasOffsets));
            mat.SetVector("_NtcScaleOffsets", ToVec4(c.networkScaleOffsets));
            mat.SetVector("_NtcImageInfo", new Vector4(c.imageWidth, c.imageHeight, c.imageMips, UIntBitsToFloat(c.validChannelMask)));
            mat.SetFloat("_NtcChannelColorSpaces", UIntBitsToFloat(c.channelColorSpaces));
            mat.SetFloat("_ChannelOffset", 0f);
            mat.SetFloat("_NtcColorSpaceOverride", (float)NtcDecompressor.DebugColorSpaceMode);
        }

        static Vector4 ToVec4(Vector4Int v) { return new Vector4(v.x, v.y, v.z, v.w); }
        static float IntBitsToFloat(int bits) { return BitConverter.ToSingle(BitConverter.GetBytes(bits), 0); }
        static float UIntBitsToFloat(uint bits) { return IntBitsToFloat(unchecked((int)bits)); }

        // -------------------------------------------------------------------- GUI

        void OnGUI()
        {
            if (!showPanel) return;

            int groupCount = groups != null ? groups.Length : 0;
            int panelW = 520;
            // The box itself has a fixed max height so it never grows off-screen, but everything
            // inside it lives in ONE scroll view (see below) so long content is reachable by
            // scrolling instead of being silently clipped by GUILayout.BeginArea's fixed Rect.
            int panelH = Mathf.Min(Screen.height - 24, 640);
            var rect = new Rect(12, 12, panelW, panelH);
            GUI.Box(rect, GUIContent.none);
            GUILayout.BeginArea(new Rect(rect.x + 8, rect.y + 6, panelW - 16, panelH - 12));

            // ---- Header (always visible, never scrolls away) ----
            GUIStyle title = new GUIStyle(GUI.skin.label) { fontSize = 13, fontStyle = FontStyle.Bold };
            GUILayout.Label("NTC Scene Manager  (" + togglePanelKey + " toggle)", title);
            GUILayout.Label("Scene: " + SceneManager.GetActiveScene().name, EditorGUIStyleLabel());

            // Mode buttons
            GUI.enabled = _initialized;
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(mode == NtcMode.Original ? "▶ Original" : "  Original")) SetMode(NtcMode.Original);
            if (GUILayout.Button(mode == NtcMode.InferenceOnLoad ? "▶ on-Load" : "  on-Load")) SetMode(NtcMode.InferenceOnLoad);
            if (GUILayout.Button(mode == NtcMode.InferenceOnSample ? "▶ on-Sample" : "  on-Sample")) SetMode(NtcMode.InferenceOnSample);
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("View", GUILayout.Width(36));
            if (GUILayout.Button(viewMode == NtcViewMode.Lit ? "▶ Lit" : "  Lit")) SetViewMode(NtcViewMode.Lit);
            if (GUILayout.Button(viewMode == NtcViewMode.BaseColor ? "▶ Base" : "  Base")) SetViewMode(NtcViewMode.BaseColor);
            if (GUILayout.Button(viewMode == NtcViewMode.Normal ? "▶ Normal" : "  Normal")) SetViewMode(NtcViewMode.Normal);
            if (GUILayout.Button(viewMode == NtcViewMode.Metallic ? "▶ Metal" : "  Metal")) SetViewMode(NtcViewMode.Metallic);
            if (GUILayout.Button(viewMode == NtcViewMode.Roughness ? "▶ Rough" : "  Rough")) SetViewMode(NtcViewMode.Roughness);
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("ColorSpace", GUILayout.Width(72));
            var csMode = NtcDecompressor.DebugColorSpaceMode;
            if (GUILayout.Button(csMode == NtcDecompressor.ColorSpaceMode.Auto ? "▶ Auto" : "  Auto"))
                SetColorSpaceDebugMode(NtcDecompressor.ColorSpaceMode.Auto);
            if (GUILayout.Button(csMode == NtcDecompressor.ColorSpaceMode.ForceSrgbDecode ? "▶ Force sRGB" : "  Force sRGB"))
                SetColorSpaceDebugMode(NtcDecompressor.ColorSpaceMode.ForceSrgbDecode);
            if (GUILayout.Button(csMode == NtcDecompressor.ColorSpaceMode.ForceRawLinear ? "▶ Force Raw" : "  Force Raw"))
                SetColorSpaceDebugMode(NtcDecompressor.ColorSpaceMode.ForceRawLinear);
            GUILayout.EndHorizontal();
            GUILayout.Label("  Auto = use .ntc container's rgbColorSpace flag (default, correct). Force sRGB/Raw\n"
                + "  are diagnostic-only overrides for A/B-testing a brightness mismatch vs Original.", EditorGUIStyleLabel());
            GUI.enabled = true;

            // Big, unambiguous "what is active right now" line, so switching is never a mystery.
            GUIStyle activeStyle = new GUIStyle(GUI.skin.label) { fontSize = 12, fontStyle = FontStyle.Bold };
            GUI.color = Color.yellow;
            GUILayout.Label("ACTIVE: " + ModeDescription(mode) + " | View: " + viewMode, activeStyle);
            GUI.color = Color.white;

            if (!string.IsNullOrEmpty(_initError))
            {
                GUI.color = Color.red;
                GUILayout.Label("Error: " + _initError);
                GUI.color = Color.white;
                GUILayout.EndArea();
                return;
            }

            if (_initializing || !_initialized)
            {
                Rect progressRect = GUILayoutUtility.GetRect(1, 18);
                GUI.Box(progressRect, GUIContent.none);
                GUI.Label(progressRect, string.Format("{0:P0}  {1}", _loadProgress, _loadStatus));
            }
            else if (_applyingMode)
            {
                // Progressive mode switch in flight -- show which group is loading so a multi-second
                // on-Sample .ntc stream reads as "loading X/24", never as a frozen client.
                Rect progressRect = GUILayoutUtility.GetRect(1, 18);
                GUI.Box(progressRect, GUIContent.none);
                GUI.color = Color.cyan;
                GUI.Label(progressRect, string.Format("{0:P0}  {1}", _applyProgress, _applyStatus));
                GUI.color = Color.white;
            }
            else
            {
                GUILayout.Label(_loadStatus, EditorGUIStyleLabel());
            }

            GUILayout.Space(4);

            // ---- Everything below is one scroll view: collapse sections you don't need with the
            // ▼/▶ headers, or just scroll to see the rest. Nothing here gets silently cut off. ----
            _scrollPos = GUILayout.BeginScrollView(_scrollPos);

            _showMaterials = DrawFoldoutHeader("Materials (" + groupCount + ")", _showMaterials);
            if (_showMaterials)
            {
                if (groups == null || groupCount == 0)
                {
                    GUILayout.Label("  (none found -- open the target scene, then NTC/Rescan Scene Materials)", EditorGUIStyleLabel());
                }
                else
                {
                    foreach (var g in groups)
                    {
                        if (g == null) continue;
                        string icon = g.hasNtcData ? "OK" : (g.ntcColor != null ? "PNG" : (g.hasNtcFile ? ".ntc" : "--"));
                        int rendererCount = g.renderers != null ? g.renderers.Length : 0;
                        GUILayout.Label(string.Format("  [{0}] {1}  ({2} renderers)  {3}",
                            icon, g.name, rendererCount, g.status), EditorGUIStyleLabel());
                    }
                }
            }

            GUILayout.Space(6);
            _showStats = DrawFoldoutHeader("Stats: disk / VRAM / bandwidth", _showStats);
            if (_showStats)
                DrawStats();

            GUILayout.Space(8);
            GUILayout.EndScrollView();

            GUILayout.EndArea();
        }

        GUIStyle EditorGUIStyleLabel()
        {
            return new GUIStyle(GUI.skin.label) { fontSize = 11 };
        }

        static bool DrawFoldoutHeader(string label, bool expanded)
        {
            GUIStyle style = new GUIStyle(GUI.skin.button) { alignment = TextAnchor.MiddleLeft, fontStyle = FontStyle.Bold, fontSize = 11 };
            if (GUILayout.Button((expanded ? "\u25BC " : "\u25B6 ") + label, style))
                expanded = !expanded;
            return expanded;
        }

        static string ModeDescription(NtcMode m)
        {
            switch (m)
            {
                case NtcMode.Original: return "Original textures (unchanged Unity materials, baseline)";
                case NtcMode.InferenceOnLoad: return "on-Load: GPU compute-decompresses the whole .ntc into ordinary textures at load time (no PNG). Shaded as normal textures -> full-res VRAM, zero per-pixel MLP.";
                case NtcMode.InferenceOnSample: return "on-Sample: tile-streamed GPU decompression into a capped-res cache, only for VISIBLE materials, budgeted per frame. Shaded as normal textures; cache evicted when hidden -> low VRAM + no per-pixel MLP.";
                default: return m.ToString();
            }
        }

        void DrawStats()
        {
            long totalNtc = 0, totalRaw = 0, totalLatent = 0, totalWeights = 0, totalOnLoad = 0;
            int groupsBaked = 0, groupsOnLoadLoaded = 0, groupsOnSampleReady = 0;
            long onLoadTexturesPerPixelBytes = 0; // sum of bytes-per-pixel across ALL bound on-Load textures (worst case, RGBA32, no BC)
            long onSampleLatentBytesPerPixel = 0; // worst-case latent fetch bytes/pixel (4 layers x bilinear x RGBA32)
            long onSampleWeightBytesTotal = 0;    // MLP weight buffer size (read once per pixel in the naive/uncached model)

            if (groups != null)
            {
                foreach (var g in groups)
                {
                    if (g == null) continue;

                    // Baked = a .ntc file exists on disk for this material. Independent of whether the
                    // PNGs/GPU data have been lazily loaded yet (they only load when you enter a mode).
                    if (g.hasNtcFile) groupsBaked++;
                    if (!string.IsNullOrEmpty(g.ntcFilePath))
                    {
                        string abs = ToAbsolute(g.ntcFilePath);
                        if (File.Exists(abs)) totalNtc += new FileInfo(abs).Length;
                    }

                    // on-Load side: PNGs that have actually been decoded (after entering on-Load once).
                    if (g.ntcColor != null || g.ntcNormal != null || g.ntcMetalness != null || g.ntcAO != null)
                    {
                        groupsOnLoadLoaded++;
                        totalOnLoad += TexVram(g.ntcColor) + TexVram(g.ntcNormal) + TexVram(g.ntcMetalness) + TexVram(g.ntcAO);
                        if (g.ntcColor != null) onLoadTexturesPerPixelBytes = Math.Max(onLoadTexturesPerPixelBytes, 4); // RGBA32
                    }

                    // on-Sample side: only groups whose GPU latent+weights are actually resident right now.
                    if (!g.hasNtcData || g.container == null || g.assets == null) continue;
                    groupsOnSampleReady++;
                    var doc = g.container.Document;
                    totalRaw += (long)doc.Width * doc.Height * Math.Max(doc.NumChannels, 1);

                    if (g.assets.LatentTexture != null)
                    {
                        totalLatent += (long)g.assets.LatentTexture.width * g.assets.LatentTexture.height * g.assets.LatentTexture.depth * 4;
                        // 4 texel bilinear fetch x 4 bytes (RGBA32) per layer, one neural mip resolved per pixel.
                        onSampleLatentBytesPerPixel = Math.Max(onSampleLatentBytesPerPixel, (long)g.assets.LatentTexture.depth * 4 * 4);
                    }
                    if (g.assets.WeightBuffer != null)
                    {
                        long wBytes = (long)g.assets.WeightBuffer.count * 4;
                        totalWeights += wBytes;
                        onSampleWeightBytesTotal = Math.Max(onSampleWeightBytesTotal, wBytes);
                    }
                }
            }

            GUILayout.Label(string.Format("  {0} baked (.ntc on disk) | {1} on-Load PNG decoded | {2} on-Sample GPU-loaded",
                groupsBaked, groupsOnLoadLoaded, groupsOnSampleReady), EditorGUIStyleLabel());

            bool computeReady = _decompressor != null && _decompressor.IsValid;
            long streamVram = _streamer != null ? _streamer.TotalCacheVram() : 0;
            GUI.color = computeReady ? new Color(0.7f, 1f, 0.7f) : new Color(1f, 0.7f, 0.4f);
            GUILayout.Label(string.Format("  Compute decompress: {0}  (DP4A HW capable: {1}) | stream cache VRAM: {2}",
                computeReady ? "ready" : "unavailable", NtcDecompressor.Dp4aHardwareSupported, MB(streamVram)), EditorGUIStyleLabel());
            GUI.color = Color.white;

            if (groupsBaked == 0)
            {
                GUI.color = new Color(1f, 0.6f, 0.2f);
                GUILayout.Label("No baked NTC data for the materials found in THIS open scene.\nCheck: 1) did you Bake these materials in NTC/Bake Window? 2) does ntcDataPath\nmatch the Bake output dir? 3) if the bake used a file-name prefix, does ntcFilePrefix\non this controller match it?", EditorGUIStyleLabel());
                GUI.color = Color.white;
                return;
            }

            if (totalRaw > 0)
            {
                double diskRatio = (double)totalRaw / Math.Max(totalNtc, 1);
                GUILayout.Label(string.Format("Disk: raw {0} -> .ntc {1}  ({2:0.0}:1, -{3:0.0}%)",
                    MB(totalRaw), MB(totalNtc), diskRatio,
                    (1.0 - (double)totalNtc / totalRaw) * 100.0), EditorGUIStyleLabel());
            }

            GUILayout.Space(2);
            GUILayout.Label("--- Per-mode comparison (Original ≈ on-Load resolution/VRAM) ---", EditorGUIStyleLabel());

            long onSampleVram = totalLatent + totalWeights;
            GUILayout.Label(string.Format("VRAM:  Original/on-Load {0}  |  on-Sample latent+weights {1}",
                MB(totalOnLoad), groupsOnSampleReady > 0 ? MB(onSampleVram) : "(load on-Sample once)"), EditorGUIStyleLabel());
            if (totalOnLoad > 0 && groupsOnSampleReady > 0)
            {
                double save = (1.0 - (double)onSampleVram / totalOnLoad) * 100.0;
                GUI.color = save > 0 ? Color.green : Color.white;
                GUILayout.Label(string.Format("       on-Sample saves {0:0.0}% VRAM vs Original/on-Load", save), EditorGUIStyleLabel());
                GUI.color = Color.white;
            }

            GUILayout.Space(2);
            GUILayout.Label("Est. bytes touched per shaded pixel (theoretical, NOT GPU-profiled):", EditorGUIStyleLabel());
            GUILayout.Label(string.Format("  Original/on-Load: ~{0} B (1 texel/sampled map, RGBA32 - this POC does not use real BC7/BC4)",
                onLoadTexturesPerPixelBytes), EditorGUIStyleLabel());
            if (groupsOnSampleReady > 0)
            {
                GUILayout.Label(string.Format("  on-Sample: ~{0} B latent (bilinear x4 layers) + {1} B weights (worst case, uncached)",
                    onSampleLatentBytesPerPixel, onSampleWeightBytesTotal), EditorGUIStyleLabel());
                GUI.color = new Color(0.7f, 0.85f, 1f);
                GUILayout.Label("  (weight bytes are re-read from the SAME buffer by every pixel -> real HW\n   bandwidth is far lower thanks to L1/L2 cache hits. To measure real GB/s,\n   use Nsight Graphics / RenderDoc frame capture, not this estimate.)", EditorGUIStyleLabel());
                GUI.color = Color.white;
            }
        }

        // -------------------------------------------------------------------- utils

        static long TexVram(Texture2D t)
        {
            if (t == null) return 0;
            long baseBytes = (long)t.width * t.height * 4;
            return t.mipmapCount > 1 ? (long)(baseBytes * 1.3333) : baseBytes;
        }

        static string MB(long bytes) { return (bytes / (1024.0 * 1024.0)).ToString("0.00") + " MB"; }

        static string ToAbsolute(string assetsRelative)
        {
            if (Path.IsPathRooted(assetsRelative)) return assetsRelative;
            return Path.Combine(Application.dataPath, assetsRelative).Replace('\\', '/');
        }

        static string MakeRelative(string absPath)
        {
            string dataPath = Application.dataPath.Replace('\\', '/');
            string ap = absPath.Replace('\\', '/');
            if (ap.StartsWith(dataPath, StringComparison.OrdinalIgnoreCase))
                return ap.Substring(dataPath.Length).TrimStart('/');
            return absPath;
        }

        static void ReleaseGroupGpuAssets(MaterialGroup g)
        {
            if (g == null) return;
            if (g.assets != null && g.assets.WeightBuffer != null)
            {
                g.assets.WeightBuffer.Release();
                g.assets.WeightBuffer = null;
            }
            if (g.onLoadTargets != null)
            {
                g.onLoadTargets.Release();
                g.onLoadTargets = null;
            }
            g.assets = null;
            g.container = null;
            g.hasNtcData = false;
            g.hasPbrLayout = false;
        }

        static void DestroyUnityObject(UnityEngine.Object obj)
        {
            if (obj == null) return;
            if (Application.isPlaying) Destroy(obj);
            else DestroyImmediate(obj);
        }

        void OnDisable()
        {
            if (!_initialized || groups == null) return;
            foreach (var g in groups)
                if (g != null)
                    RestoreOriginalMaterialRefs(g);
        }

        void OnDestroy()
        {
            if (_streamer != null)
            {
                _streamer.ReleaseAll();
                _streamer = null;
            }
            if (_decompressor != null)
            {
                _decompressor.Dispose();
                _decompressor = null;
            }
            if (groups == null) return;
            foreach (var g in groups)
            {
                if (g == null) continue;
                ReleaseGroupGpuAssets(g);
                DestroyUnityObject(g.onLoadMaterial);
                DestroyUnityObject(g.onSampleMaterial);
                DestroyUnityObject(g.streamMaterial);
                DestroyUnityObject(g.debugViewMaterial);
                DestroyUnityObject(g.metallicGlossMap);
                g.onLoadMaterial = null;
                g.onSampleMaterial = null;
                g.streamMaterial = null;
                g.debugViewMaterial = null;
                g.metallicGlossMap = null;
            }
        }
    }
}
