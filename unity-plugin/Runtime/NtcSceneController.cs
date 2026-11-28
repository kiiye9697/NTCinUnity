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
    // Runtime-only slot metadata deliberately lives outside NtcSceneController and
    // MaterialGroup. Keeping it in a static registry preserves the serialized type tree, so
    // an updated managed assembly remains compatible with Players/scenes built before
    // multi-submesh support was added.
    internal struct NtcMaterialSlotBinding
    {
        public Renderer Renderer;
        public int MaterialIndex;

        public NtcMaterialSlotBinding(Renderer renderer, int materialIndex)
        {
            Renderer = renderer;
            MaterialIndex = materialIndex;
        }
    }

    internal static class NtcMaterialSlotRegistry
    {
        static readonly Dictionary<NtcSceneController.MaterialGroup, NtcMaterialSlotBinding[]> s_Bindings
            = new Dictionary<NtcSceneController.MaterialGroup, NtcMaterialSlotBinding[]>();

        public static void Set(NtcSceneController.MaterialGroup group, NtcMaterialSlotBinding[] bindings)
        {
            if (group != null) s_Bindings[group] = bindings ?? Array.Empty<NtcMaterialSlotBinding>();
        }

        public static bool TryGet(NtcSceneController.MaterialGroup group, out NtcMaterialSlotBinding[] bindings)
        {
            return s_Bindings.TryGetValue(group, out bindings);
        }

        public static void Remove(NtcSceneController.MaterialGroup group)
        {
            if (group != null) s_Bindings.Remove(group);
        }
    }

    [DisallowMultipleComponent]
    public class NtcSceneController : MonoBehaviour
    {
        public enum NtcMode { Original = 0, InferenceOnLoad = 1, InferenceOnSample = 2, InferenceOnSampleDirect = 3 }
        public enum NtcViewMode { Lit = 0, BaseColor = 1, Normal = 2, Metallic = 3, Roughness = 4 }

        [Serializable]
        public class MaterialGroup
        {
            public string name;
            public Renderer[] renderers;
            public NtcTextureSetAsset textureSet;
            public NtcRendererSlot[] slots = Array.Empty<NtcRendererSlot>();
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
            [NonSerialized] public bool ownsNtcColor, ownsNtcNormal, ownsNtcMetalness, ownsNtcAO, ownsNtcSmoothness, ownsNtcAlpha;
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
        public NtcTextureSetAsset[] textureSets = Array.Empty<NtcTextureSetAsset>();
        [Tooltip("Release unreferenced original textures after direct NTC is ready. Requires generated binding assets with a Resources recovery material.")]
        public bool releaseOriginalTextures;
        public Shader directSampleShader;
        public bool OriginalsReleased { get; private set; }
        public bool IsReady => _initialized && !_applyingMode;
        public NtcMemorySnapshot MemorySnapshot => NtcMemoryAccounting.Capture(groups, _originalTextureIds);
        readonly HashSet<int> _originalTextureIds = new HashSet<int>();
        NtcMemorySnapshot _displayMemory;
        float _nextMemorySample;

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
        public int streamMaxCacheMB
        {
            get { return NtcTileStreamer.GlobalMaxCacheMB; }
            set { NtcTileStreamer.GlobalMaxCacheMB = Mathf.Max(value, 0); }
        }

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
        Coroutine _initializeRoutine;
        float _loadProgress;
        string _loadStatus = "Not started";

        // Mode switching is applied progressively (one group per frame) so heavy work --
        // decoding on-Load PNGs and building on-Sample latent textures/weight buffers --
        // never blocks the main thread for multiple seconds (the old "freeze" on switching).
        Coroutine _applyRoutine;
        bool _applyingMode;
        NtcMode _lastAppliedMode = NtcMode.Original;
        float _applyProgress;
        string _applyStatus = "";
        int _bakedNtcGroups;
        int _loadedNtcGroups;
        int _failedNtcGroups;
        string _initError;

        void OnEnable()
        {
            if (!_initialized && !_initializing)
                _initializeRoutine = StartCoroutine(InitializeRoutine());
            else if (_initialized)
                StartApplyMode(mode);
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
                            MaxCacheBytes = (long)Mathf.Max(streamMaxCacheMB, 0) * 1024L * 1024L,
                        };
                        Debug.Log("[NTC] Compute decompressor ready; active inference path: "
                            + NtcDecompressor.ActualComputePath + ".");
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
                _initializeRoutine = null;
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
            _initializeRoutine = null;
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
            if (groups != null)
                for (int i = 0; i < groups.Length; i++)
                    NtcMaterialSlotRegistry.Remove(groups[i]);
            var byMat = new Dictionary<Material, List<NtcMaterialSlotBinding>>();
#if UNITY_2022_2_OR_NEWER
            Renderer[] all = UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None);
#else
            Renderer[] all = UnityEngine.Object.FindObjectsOfType<Renderer>();
#endif
            foreach (Renderer r in all)
            {
                Material[] materials = r.sharedMaterials;
                for (int materialIndex = 0; materialIndex < materials.Length; materialIndex++)
                {
                    Material m = materials[materialIndex];
                    if (m == null) continue;
                    List<NtcMaterialSlotBinding> bindings;
                    if (!byMat.TryGetValue(m, out bindings))
                    {
                        bindings = new List<NtcMaterialSlotBinding>();
                        byMat.Add(m, bindings);
                    }
                    bindings.Add(new NtcMaterialSlotBinding(r, materialIndex));
                }
            }

            var list = new List<MaterialGroup>();
            foreach (var kv in byMat)
            {
                var uniqueRenderers = new List<Renderer>();
                for (int i = 0; i < kv.Value.Count; i++)
                    if (!uniqueRenderers.Contains(kv.Value[i].Renderer))
                        uniqueRenderers.Add(kv.Value[i].Renderer);
                var g = new MaterialGroup
                {
                    name = kv.Key.name,
                    renderers = uniqueRenderers.ToArray(),
                    textureSet = FindTextureSet(kv.Key),
                    slots = kv.Value.ConvertAll(b => new NtcRendererSlot { renderer = b.Renderer, materialIndex = b.MaterialIndex }).ToArray()
                };
                NtcMaterialSlotRegistry.Set(g, kv.Value.ToArray());
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
            if (g.textureSet != null)
            {
                string explicitPath = g.textureSet.ResolveDataPath();
                g.ntcFilePath = MakeRelative(explicitPath);
                g.hasNtcFile = File.Exists(explicitPath);
                g.status = g.hasNtcFile ? "bound " + g.textureSet.stableId : "binding data missing: " + explicitPath;
                return; // An explicit broken binding must never silently select a namesake.
            }
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

            // Cache the original material at every targeted slot. Auto-scan has exact slot
            // bindings; legacy serialized groups derive them from their renderer list.
            if (g.renderers == null)
                g.renderers = Array.Empty<Renderer>();
            NtcMaterialSlotBinding[] bindings = GetOrCreateSlotBindings(g);
            g.originalMaterials = new Material[bindings.Length];
            for (int i = 0; i < bindings.Length; i++)
                g.originalMaterials[i] = GetSlotMaterial(bindings[i]);
            if (g.textureSet == null && g.originalMaterials.Length > 0)
                g.textureSet = FindTextureSet(g.originalMaterials[0]);
            NtcMemoryAccounting.RememberTextures(g.originalMaterials, _originalTextureIds);

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
            if (g.hasNtcFile) g.status = "baked (.ntc)";
        }

        NtcTextureSetAsset FindTextureSet(Material source)
        {
            if (source == null) return null;
            string sourceId = source.GetTag("NtcSourceId", false, "");
            foreach (var binding in textureSets ?? Array.Empty<NtcTextureSetAsset>())
                if (binding != null && (binding.texturelessTemplate == source || (!string.IsNullOrEmpty(sourceId) && binding.stableId == sourceId))) return binding;
#if UNITY_EDITOR
            foreach (var binding in textureSets ?? Array.Empty<NtcTextureSetAsset>())
                if (binding != null && binding.editorSourceMaterial == source) return binding;
            foreach (string guid in UnityEditor.AssetDatabase.FindAssets("t:NtcTextureSetAsset"))
            {
                var binding = UnityEditor.AssetDatabase.LoadAssetAtPath<NtcTextureSetAsset>(UnityEditor.AssetDatabase.GUIDToAssetPath(guid));
                if (binding != null && binding.editorSourceMaterial == source) return binding;
            }
#endif
            return null;
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

            if (g.ntcColor == null)      { g.ntcColor = LoadPng(Path.Combine(decompDir, "Color.png"), true); g.ownsNtcColor = g.ntcColor != null; }
            if (g.ntcNormal == null)     { g.ntcNormal = LoadPng(Path.Combine(decompDir, "Normal.png"), false); g.ownsNtcNormal = g.ntcNormal != null; }
            if (g.ntcMetalness == null)  { g.ntcMetalness = LoadPng(Path.Combine(decompDir, "Metalness.png"), false); g.ownsNtcMetalness = g.ntcMetalness != null; }
            if (g.ntcAO == null)         { g.ntcAO = LoadPng(Path.Combine(decompDir, "AO.png"), false); g.ownsNtcAO = g.ntcAO != null; }
            if (g.ntcSmoothness == null) { g.ntcSmoothness = LoadPng(Path.Combine(decompDir, "Smoothness.png"), false); g.ownsNtcSmoothness = g.ntcSmoothness != null; }
            if (g.ntcAlpha == null)      { g.ntcAlpha = LoadPng(Path.Combine(decompDir, "Alpha.png"), false); g.ownsNtcAlpha = g.ntcAlpha != null; }
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

            Texture2D result = null;
            try
            {
                result = new Texture2D(width, height, TextureFormat.RGBA32, true, true);
                result.name = g.name + " NTC MetallicSmoothness";
                result.wrapMode = TextureWrapMode.Repeat;
                result.filterMode = FilterMode.Bilinear;
                result.SetPixels32(packed);
                result.Apply(true, false);
                g.metallicGlossMap = result;
                return result;
            }
            catch
            {
                DestroyUnityObject(result);
                throw;
            }
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

            Texture2D result = null;
            try
            {
                result = new Texture2D(width, height, TextureFormat.RGBA32, true, false);
                result.name = g.name + " NTC ColorWithAlpha";
                result.wrapMode = TextureWrapMode.Repeat;
                result.filterMode = FilterMode.Bilinear;
                result.SetPixels32(packed);
                result.Apply(true, false);
                g.colorWithAlphaMap = result;
                return result;
            }
            catch
            {
                DestroyUnityObject(result);
                throw;
            }
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
                if (g != null) ApplyOriginal(g);
            ReleaseResourcesForMode(m);
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

            // Revert first so no renderer references a cache while it is released below. Then
            // discard resources that are mutually exclusive with the destination mode.
            if (groups != null)
            {
                _applyStatus = "Switching to " + m + " ...";
                bool originalsReady = true;
                try
                {
                    // Load every recovery material before changing a renderer or releasing NTC.
                    foreach (var g in groups) if (g != null) EnsureOriginalMaterials(g);
                }
                catch (Exception error)
                {
                    originalsReady = false;
                    mode = _lastAppliedMode;
                    _applyStatus = "Original recovery failed: " + error.Message;
                    Debug.LogError("[NTC] " + _applyStatus);
                }
                if (!originalsReady) { _applyingMode = false; _applyRoutine = null; yield break; }
                foreach (var g in groups)
                    if (g != null) ApplyOriginal(g);
                ReleaseResourcesForMode(m);
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

            if (m == NtcMode.InferenceOnSampleDirect)
                yield return ReleaseOriginalsAfterDirectReady();
            _applyStatus = m + " applied";
            _lastAppliedMode = m;
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
                case NtcMode.InferenceOnSampleDirect: ApplyOnSampleDirect(g); break;
            }
        }

        static NtcMaterialSlotBinding[] GetOrCreateSlotBindings(MaterialGroup g)
        {
            NtcMaterialSlotBinding[] existing;
            if (g != null && NtcMaterialSlotRegistry.TryGet(g, out existing))
                return existing;

            var result = new List<NtcMaterialSlotBinding>();
            if (g != null && g.slots != null && g.slots.Length > 0)
            {
                foreach (NtcRendererSlot slot in g.slots)
                    if (slot.renderer != null && slot.materialIndex >= 0 && slot.materialIndex < slot.renderer.sharedMaterials.Length)
                        AddUniqueBinding(result, slot.renderer, slot.materialIndex);
                existing = result.ToArray();
                NtcMaterialSlotRegistry.Set(g, existing);
                return existing;
            }
            if (g != null && g.renderers != null)
            {
                for (int rendererIndex = 0; rendererIndex < g.renderers.Length; rendererIndex++)
                {
                    Renderer renderer = g.renderers[rendererIndex];
                    if (renderer == null) continue;
                    Material[] materials = renderer.sharedMaterials;
                    bool foundNamedSlot = false;
                    for (int materialIndex = 0; materialIndex < materials.Length; materialIndex++)
                    {
                        if (!MaterialMatchesLegacyGroup(materials[materialIndex], g)) continue;
                        AddUniqueBinding(result, renderer, materialIndex);
                        foundNamedSlot = true;
                    }

                    // Old serialized data recorded Renderer only. Slot zero was its historical
                    // meaning, so retain that fallback when a material/group name cannot identify
                    // a non-zero slot. Exact non-zero recovery is impossible without serialized
                    // slot metadata; auto-scanned groups do not have this ambiguity.
                    if (!foundNamedSlot && materials.Length > 0)
                    {
                        AddUniqueBinding(result, renderer, 0);
                        if (materials.Length > 1)
                            Debug.LogWarning("[NTC] Legacy group '" + g.name + "' could not identify a material slot on '"
                                + renderer.name + "'; using the backward-compatible slot 0 fallback.", renderer);
                    }
                }
            }
            existing = result.ToArray();
            if (g != null) NtcMaterialSlotRegistry.Set(g, existing);
            return existing;
        }

        static void AddUniqueBinding(List<NtcMaterialSlotBinding> bindings, Renderer renderer, int materialIndex)
        {
            for (int i = 0; i < bindings.Count; i++)
                if (bindings[i].Renderer == renderer && bindings[i].MaterialIndex == materialIndex)
                    return;
            bindings.Add(new NtcMaterialSlotBinding(renderer, materialIndex));
        }

        static bool MaterialMatchesLegacyGroup(Material material, MaterialGroup group)
        {
            if (material == null || group == null) return false;
            string materialName = NormalizeMaterialName(material.name);
            string groupName = NormalizeMaterialName(group.name);
            if (string.Equals(materialName, groupName, StringComparison.OrdinalIgnoreCase)) return true;

            if (!string.IsNullOrEmpty(group.ntcFilePath))
            {
                string ntcName = NormalizeMaterialName(Path.GetFileNameWithoutExtension(group.ntcFilePath));
                if (string.Equals(materialName, ntcName, StringComparison.OrdinalIgnoreCase)
                    || ntcName.EndsWith(materialName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        static string NormalizeMaterialName(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            const string instanceSuffix = " (Instance)";
            return value.EndsWith(instanceSuffix, StringComparison.Ordinal)
                ? value.Substring(0, value.Length - instanceSuffix.Length)
                : value;
        }

        static Material GetSlotMaterial(NtcMaterialSlotBinding binding)
        {
            if (binding.Renderer == null) return null;
            Material[] materials = binding.Renderer.sharedMaterials;
            return binding.MaterialIndex >= 0 && binding.MaterialIndex < materials.Length
                ? materials[binding.MaterialIndex]
                : null;
        }

        static void SetSlotMaterial(NtcMaterialSlotBinding binding, Material material)
        {
            if (binding.Renderer == null) return;
            Material[] materials = binding.Renderer.sharedMaterials;
            if (binding.MaterialIndex < 0 || binding.MaterialIndex >= materials.Length) return;
            if (materials[binding.MaterialIndex] == material) return;
            materials[binding.MaterialIndex] = material;
            binding.Renderer.sharedMaterials = materials;
        }

        void ApplyOriginal(MaterialGroup g)
        {
            EnsureOriginalMaterials(g);
            if (g.originalMaterials == null) return;
            NtcMaterialSlotBinding[] bindings = GetOrCreateSlotBindings(g);
            int count = Mathf.Min(bindings.Length, g.originalMaterials.Length);
            for (int i = 0; i < count; i++)
                SetSlotMaterial(bindings[i], GetViewMaterial(g, g.originalMaterials[i]));
        }

        void RestoreOriginalMaterialRefs(MaterialGroup g)
        {
            EnsureOriginalMaterials(g);
            if (g.originalMaterials == null) return;
            NtcMaterialSlotBinding[] bindings = GetOrCreateSlotBindings(g);
            int count = Mathf.Min(bindings.Length, g.originalMaterials.Length);
            for (int i = 0; i < count; i++)
                SetSlotMaterial(bindings[i], g.originalMaterials[i]);
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
            {
                try { LoadDecompressedTextures(g, ToAbsolute(ntcDataPath)); }
                catch { ReleaseOnLoadCache(g); throw; }
            }

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
                if (g.onLoadMaterial.HasProperty(kBaseMap) && (g.ntcAlpha != null || !RequiresBaseAlpha(src)))
                    g.onLoadMaterial.SetTexture(kBaseMap, baseMapTex);

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
                        if (g.onLoadMaterial.HasProperty("_BumpScale")) g.onLoadMaterial.SetFloat("_BumpScale", SafeGetFloat(src, "_BumpScale", 1f));
                    }

                    if (g.ntcAO != null && g.onLoadMaterial.HasProperty(kOcclusionMap))
                    {
                        g.onLoadMaterial.SetTexture(kOcclusionMap, g.ntcAO);
                    }

                    bool haveMetal = g.ntcMetalness != null;
                    bool haveSmooth = g.ntcSmoothness != null;
                    // A partial NTC mask cannot replace a packed URP map without losing
                    // the other source component. Keep the original mask until both exist.
                    if (haveMetal && haveSmooth)
                    {
                        Texture2D metallicGloss = GetOrCreateMetallicGlossMap(g);
                        if (metallicGloss != null && g.onLoadMaterial.HasProperty(kMetallicGlossMap))
                        {
                            g.onLoadMaterial.SetTexture(kMetallicGlossMap, metallicGloss);
                            // When a channel is actually driven by the packed map, the scalar multiplier
                            // must be 1. When a channel is MISSING (e.g. cloth has no smoothness map), we
                            // must NOT force it to 1 -- doing so made non-metal cloth render fully smooth
                            // (shiny/plastic). Instead keep the original material's scalar so the look
                            // matches Original as closely as the available maps allow.
                            if (g.onLoadMaterial.HasProperty(kMetallic))
                                g.onLoadMaterial.SetFloat(kMetallic, SafeGetFloat(src, kMetallic, 0f));
                            if (g.onLoadMaterial.HasProperty(kSmoothness))
                                g.onLoadMaterial.SetFloat(kSmoothness, SafeGetFloat(src, kSmoothness, 0.5f));
                        }
                    }
                }
            }
            Material display = GetViewMaterial(g, g.onLoadMaterial);
            AssignGroupMaterial(g, display);
            g.status = "on-Load active";
        }

        static float SafeGetFloat(Material m, string prop, float fallback)
        {
            return (m != null && m.HasProperty(prop)) ? m.GetFloat(prop) : fallback;
        }

        static bool RequiresBaseAlpha(Material material)
        {
            return material != null && (SafeGetFloat(material, "_AlphaClip", 0f) > 0.5f
                || material.IsKeywordEnabled("_ALPHATEST_ON")
                || SafeGetFloat(material, "_Surface", 0f) > 0.5f
                || material.GetTag("RenderType", false) == "Transparent"
                || SafeGetFloat(material, "_SmoothnessTextureChannel", 0f) > 0.5f
                || material.IsKeywordEnabled("_SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A"));
        }

        static string PbrFallbackNote(Material source, NtcPbrChannelLayout layout)
        {
            bool baseFallback = RequiresBaseAlpha(source) && !layout.HasAlpha;
            bool maskFallback = layout.HasMask && (layout.Metal < 0 || layout.Smooth < 0);
            if (baseFallback && maskFallback) return "; original BaseMap (alpha absent), original mask (partial bake)";
            if (baseFallback) return "; original BaseMap (alpha absent)";
            if (maskFallback) return "; original mask (partial bake)";
            return "";
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
            if (g.onLoadTargets == null)
            {
                if (!EnsureGpuData(g) || !g.hasPbrLayout)
                    return false;
                int w = Mathf.Max(g.assets.Constants.imageWidth, 1);
                int h = Mathf.Max(g.assets.Constants.imageHeight, 1);
                NtcDecompressTargets pending = null;
                try
                {
                    pending = _decompressor.AllocateTargets(w, h, g.pbrLayout, true);
                    _decompressor.DecompressFull(g.assets, g.pbrLayout, pending, 0);
                    NtcDecompressor.GenerateMips(pending);
                    g.onLoadTargets = pending;
                    pending = null;
                }
                finally
                {
                    if (pending != null) pending.Release();
                }
            }

            Material src = g.originalMaterials != null && g.originalMaterials.Length > 0 ? g.originalMaterials[0] : null;
            if (src == null) return false;

            if (g.onLoadMaterial == null)
                g.onLoadMaterial = new Material(src) { name = src.name + " (NTC on-Load compute)" };
            BindPbrTargetsToMaterial(g.onLoadMaterial, src, g.onLoadTargets, g.pbrLayout);

            AssignGroupMaterial(g, GetViewMaterial(g, g.onLoadMaterial));

            g.status = "on-Load active (compute)" + PbrFallbackNote(src, g.pbrLayout);
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

            // Preserve opacity/cutout/albedo-alpha smoothness when an older bake lacks alpha.
            if (t.BaseColor != null && mat.HasProperty(kBaseMap) && (layout.HasAlpha || !RequiresBaseAlpha(src)))
                mat.SetTexture(kBaseMap, t.BaseColor);

            if (onLoadSwapAllMaps)
            {
                if (t.Normal != null && mat.HasProperty(kBumpMap))
                {
                    mat.SetTexture(kBumpMap, t.Normal);
                    if (mat.HasProperty("_BumpScale")) mat.SetFloat("_BumpScale", SafeGetFloat(src, "_BumpScale", 1f));
                }
                if (t.AO != null && mat.HasProperty(kOcclusionMap))
                {
                    mat.SetTexture(kOcclusionMap, t.AO);
                }
                // URP ignores the metallic scalar once a packed map is enabled. A cache
                // shared by different materials cannot encode their missing scalar defaults.
                if (t.Mask != null && layout.Metal >= 0 && layout.Smooth >= 0 && mat.HasProperty(kMetallicGlossMap))
                {
                    mat.SetTexture(kMetallicGlossMap, t.Mask);
                    if (mat.HasProperty(kMetallic)) mat.SetFloat(kMetallic, SafeGetFloat(src, kMetallic, 0f));
                    // Baking stores the raw map; URP still applies the source multiplier.
                    if (mat.HasProperty(kSmoothness)) mat.SetFloat(kSmoothness, SafeGetFloat(src, kSmoothness, 0.5f));
                }
            }
        }

        Material GetViewMaterial(MaterialGroup g, Material source)
        {
            if (source != null && source.HasProperty("_NtcDebugView"))
            {
                source.SetFloat("_NtcDebugView", (float)viewMode);
                return source;
            }
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
                else if (mode == NtcMode.InferenceOnSampleDirect && g.onSampleMaterial != null)
                    AssignGroupMaterial(g, GetViewMaterial(g, g.onSampleMaterial));
                else if (mode == NtcMode.InferenceOnSample)
                {
                    string id = string.IsNullOrEmpty(g.ntcFilePath) ? g.name : g.ntcFilePath;
                    NtcTileStreamer.Entry e = _streamer != null ? _streamer.Get(id) : null;
                    if (g.streamActive && g.streamMaterial != null && e != null && e.IsComplete)
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
            if (g == null || material == null) return;
            NtcMaterialSlotBinding[] bindings = GetOrCreateSlotBindings(g);
            for (int i = 0; i < bindings.Length; i++)
                SetSlotMaterial(bindings[i], material);
        }

        /// <summary>Ensure the .ntc container + GPU assets + PBR layout are loaded for this group.</summary>
        bool EnsureGpuData(MaterialGroup g)
        {
            return EnsureOnSampleData(g);
        }

        void EnsureOriginalMaterials(MaterialGroup g)
        {
            if (g == null || (g.originalMaterials != null && g.originalMaterials.Length > 0)) return;
            if (g.textureSet == null || string.IsNullOrEmpty(g.textureSet.originalResourcePath)) return;
            Material original = Resources.Load<Material>(g.textureSet.originalResourcePath);
            if (original == null) throw new InvalidOperationException("Cannot restore original material " + g.textureSet.originalResourcePath);
            var bindings = GetOrCreateSlotBindings(g);
            g.originalMaterials = new Material[bindings.Length];
            for (int i = 0; i < bindings.Length; i++) g.originalMaterials[i] = original;
            OriginalsReleased = false;
            NtcMemoryAccounting.RememberTextures(g.originalMaterials, _originalTextureIds);
        }

        void ApplyOnSampleDirect(MaterialGroup g)
        {
            Material source = g.textureSet != null ? g.textureSet.texturelessTemplate : null;
            if (source == null) throw new InvalidOperationException("Direct NTC requires a generated texture-set binding and template: " + g.name);
            if (!string.IsNullOrEmpty(g.textureSet.directUnsupportedReason))
            {
                ApplyOriginal(g);
                ReleaseNtcInputAssets(g);
                DestroyUnityObject(g.onSampleMaterial); g.onSampleMaterial = null;
                g.status = "direct original fallback: " + g.textureSet.directUnsupportedReason;
                return;
            }
            if (!EnsureGpuData(g)) throw new InvalidOperationException("Cannot load direct NTC data: " + g.name);
            if (directSampleShader == null) directSampleShader = Shader.Find("NTC/LitDirect_URP");
            if (directSampleShader == null || !directSampleShader.isSupported) throw new InvalidOperationException("NTC direct Lit shader is unavailable.");
            NtcMaterialSampling.ValidateUrpMaterial(source);
            if (g.onSampleMaterial == null || g.onSampleMaterial.shader != directSampleShader)
            {
                DestroyUnityObject(g.onSampleMaterial);
                g.onSampleMaterial = g.textureSet.directTemplate != null ? new Material(g.textureSet.directTemplate) : new Material(directSampleShader);
                g.onSampleMaterial.name = g.name + " (NTC Direct Lit)";
            }
            NtcMaterialSampling.Bind(g.onSampleMaterial, source, g.assets, g.pbrLayout, g.textureSet);
            AssignGroupMaterial(g, GetViewMaterial(g, g.onSampleMaterial));
            g.status = "direct NTC PBR ready";
        }

        IEnumerator ReleaseOriginalsAfterDirectReady()
        {
            if (!releaseOriginalTextures || mode != NtcMode.InferenceOnSampleDirect || groups == null) yield break;
            bool released = false;
            foreach (var g in groups)
            {
                if (g == null || g.textureSet == null || g.onSampleMaterial == null || g.status != "direct NTC PBR ready"
                    || string.IsNullOrEmpty(g.textureSet.originalResourcePath)) continue;
                if (!CanRecoverOriginal(g.textureSet.originalResourcePath))
                {
                    g.status = "direct NTC PBR ready; originals retained (recovery unavailable)";
                    continue;
                }
                g.originalMaterials = null;
                DestroyUnityObject(g.debugViewMaterial);
                g.debugViewMaterial = null;
                released = true;
            }
            if (!released) yield break;
            // Never destroy imported/shared textures: live consumers remain roots in Unity's graph.
            yield return Resources.UnloadUnusedAssets();
            OriginalsReleased = true;
        }

        static bool CanRecoverOriginal(string path) => Resources.Load<Material>(path) != null;

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
            Material display = GetViewMaterial(g, g.onSampleMaterial);
            AssignGroupMaterial(g, display);
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
            // Register is idempotent and unions renderer consumers when groups share a cache id.
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
            _streamer.MaxCacheBytes = (long)Mathf.Max(streamMaxCacheMB, 0) * 1024L * 1024L;
            _streamer.Tick(cam);

            for (int gi = 0; gi < groups.Length; gi++)
            {
                MaterialGroup g = groups[gi];
                if (g == null || !g.streamActive || g.streamMaterial == null) continue;

                string id = string.IsNullOrEmpty(g.ntcFilePath) ? g.name : g.ntcFilePath;
                NtcTileStreamer.Entry e = _streamer.Get(id);
                if (e == null) continue;

                // Atomic presentation: an allocated/partially decoded cache is never bound.
                // The original material stays visible until every tile and mip is ready.
                bool resident = e.IsComplete;
                if (resident)
                {
                    if (e.version != g.streamBoundVersion)
                    {
                        Material src = g.originalMaterials != null && g.originalMaterials.Length > 0 ? g.originalMaterials[0] : null;
                        BindPbrTargetsToMaterial(g.streamMaterial, src, e.targets, g.pbrLayout);
                        g.streamBoundVersion = e.version;
                        AssignGroupMaterial(g, GetViewMaterial(g, g.streamMaterial));
                        g.status = "on-Sample ready" + PbrFallbackNote(src, g.pbrLayout);
                    }
                }
                else
                {
                    // Cache was evicted (hidden too long) or not yet streamed: show original.
                    if (g.streamBoundVersion >= 0)
                    {
                        NtcMaterialSlotBinding[] bindings = GetOrCreateSlotBindings(g);
                        if (g.originalMaterials != null)
                            for (int i = 0; i < bindings.Length && i < g.originalMaterials.Length; i++)
                            {
                                Material current = GetSlotMaterial(bindings[i]);
                                if (current == g.streamMaterial || current == g.debugViewMaterial)
                                    SetSlotMaterial(bindings[i], GetViewMaterial(g, g.originalMaterials[i]));
                            }
                    }
                    g.streamBoundVersion = -1;
                    g.status = e.budgetBlocked ? "on-Sample deferred (VRAM budget)" : "on-Sample streaming";
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
                g.container = NtcContainerReader.LoadMetadata(ntcAbsPath);
                g.assets = NtcAssetBuilder.Build(g.container, 0);
                g.hasNtcData = g.assets != null && g.assets.LatentTexture != null && g.assets.WeightBuffer != null;
                if (g.hasNtcData)
                {
                    g.pbrLayout = NtcAssetBuilder.BuildPbrLayout(g.container);
                    g.hasPbrLayout = true;
                }
                if (!g.hasNtcData)
                    ReleaseNtcInputAssets(g);
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
            if (GUILayout.Button(mode == NtcMode.InferenceOnSampleDirect ? "▶ Direct Lit" : "  Direct Lit")) SetMode(NtcMode.InferenceOnSampleDirect);
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
                case NtcMode.InferenceOnSampleDirect: return "Direct Lit: shader material access decodes the PBR texture set. Filtering may require multiple neural texel loads; no decompressed cache.";
                default: return m.ToString();
            }
        }

        void DrawStats()
        {
            if (_displayMemory == null || Time.realtimeSinceStartup >= _nextMemorySample)
            {
                _displayMemory = MemorySnapshot;
                _nextMemorySample = Time.realtimeSinceStartup + 1f;
            }
            GUILayout.Label("Loaded texture + NTC buffer estimate (deduplicated): " + MB(_displayMemory.estimatedTextureAndNtcBufferBytes), EditorGUIStyleLabel());
            GUILayout.Label("  Originals " + MB(_displayMemory.originalTextureBytes) + " | Latents " + MB(_displayMemory.latentTextureBytes)
                + " | Weights " + MB(_displayMemory.weightBufferBytes) + " | Decoded " + MB(_displayMemory.decodedCacheBytes), EditorGUIStyleLabel());
            GUILayout.Label("  Other loaded textures " + MB(_displayMemory.otherLoadedTextureBytes) + " | Graphics driver allocation (separate): "
                + (_displayMemory.graphicsDriverAllocationAvailable ? MB(_displayMemory.graphicsDriverAllocatedBytes) : "unavailable")
                + " | Original references detached: " + OriginalsReleased, EditorGUIStyleLabel());
            long totalNtc = 0, totalRaw = 0, totalLatent = 0, totalWeights = 0, totalOnLoad = 0;
            int groupsBaked = 0, groupsOnLoadLoaded = 0, groupsOnSampleReady = 0;

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

                    // Actual runtime allocations owned by on-Load (legacy PNG or compute targets).
                    if (g.onLoadTargets != null)
                    {
                        groupsOnLoadLoaded++;
                        totalOnLoad += g.onLoadTargets.VramBytes;
                    }
                    else if (g.ntcColor != null || g.ntcNormal != null || g.ntcMetalness != null || g.ntcAO != null)
                    {
                        groupsOnLoadLoaded++;
                        totalOnLoad += TexVram(g.ntcColor) + TexVram(g.ntcNormal) + TexVram(g.ntcMetalness)
                            + TexVram(g.ntcAO) + TexVram(g.ntcSmoothness) + TexVram(g.ntcAlpha)
                            + TexVram(g.metallicGlossMap) + TexVram(g.colorWithAlphaMap);
                    }

                    // on-Sample side: only groups whose GPU latent+weights are actually resident right now.
                    if (!g.hasNtcData || g.container == null || g.assets == null) continue;
                    groupsOnSampleReady++;
                    var doc = g.container.Document;
                    totalRaw += (long)doc.Width * doc.Height * Math.Max(doc.NumChannels, 1);

                    if (g.assets.LatentTexture != null)
                    {
                        totalLatent += TexArrayVram(g.assets.LatentTexture);
                    }
                    if (g.assets.WeightBuffer != null)
                    {
                        long wBytes = (long)g.assets.WeightBuffer.count * 4;
                        totalWeights += wBytes;
                    }
                }
            }

            GUILayout.Label(string.Format("  {0} baked (.ntc on disk) | {1} on-Load caches allocated | {2} NTC input sets GPU-loaded",
                groupsBaked, groupsOnLoadLoaded, groupsOnSampleReady), EditorGUIStyleLabel());

            bool computeReady = _decompressor != null && _decompressor.IsValid;
            long streamVram = _streamer != null ? _streamer.TotalCacheVram() : 0;
            GUI.color = computeReady ? new Color(0.7f, 1f, 0.7f) : new Color(1f, 0.7f, 0.4f);
            GUILayout.Label(string.Format("  Compute decompress: {0} | actual path: {1} | stream cache: {2}",
                computeReady ? "ready" : "unavailable", NtcDecompressor.ActualComputePath, MB(streamVram)), EditorGUIStyleLabel());
            if (_streamer != null)
                GUILayout.Label(string.Format("  Stream budget: {0} MB | visible eligible/resident/deferred: {1}/{2}/{3}",
                    Mathf.Max(streamMaxCacheMB, 0), _streamer.EligibleVisibleEntryCount,
                    _streamer.ResidentVisibleEntryCount, _streamer.DeferredVisibleEntryCount), EditorGUIStyleLabel());
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
                GUILayout.Label(string.Format("Disk estimate: base-level channel bytes {0} | .ntc files {1} | ratio {2:0.0}:1",
                    MB(totalRaw), MB(totalNtc), diskRatio), EditorGUIStyleLabel());
            }

            GUILayout.Space(2);
            GUILayout.Label("--- Runtime allocation accounting (static estimate; verify with Unity Profiler) ---", EditorGUIStyleLabel());

            long onSampleVram = totalLatent + totalWeights + streamVram;
            GUILayout.Label(string.Format("  on-Load owned cache: {0} | on-Sample input+stream cache: {1}",
                MB(totalOnLoad), groupsOnSampleReady > 0 ? MB(onSampleVram) : "(not resident)"), EditorGUIStyleLabel());
            GUILayout.Label("  Original texture residency and allocator overhead: pending Unity Memory Profiler capture.", EditorGUIStyleLabel());
            GUILayout.Label("  No savings percentage is reported until all three modes are measured with the same Profiler protocol.", EditorGUIStyleLabel());

            GUILayout.Space(2);
            GUILayout.Label("  Frame time / bandwidth: pending GPU Profiler or Nsight capture (not inferred from allocation size).", EditorGUIStyleLabel());
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
            if (Path.IsPathRooted(assetsRelative)) return assetsRelative.Replace('\\', '/');

            string relative = assetsRelative.Replace('\\', '/').TrimStart('/');
#if UNITY_EDITOR
            return Path.Combine(Application.dataPath, relative).Replace('\\', '/');
#else
            // Raw .ntc files must be shipped through StreamingAssets in a Player build.
            // Keep the Editor-facing default (NtcPlugin/NtcData) useful by mapping it to
            // StreamingAssets/NtcData at runtime. Explicit StreamingAssets paths and
            // custom relative paths are supported as well.
            const string streamingPrefix = "StreamingAssets/";
            const string pluginPrefix = "NtcPlugin/";
            if (relative.StartsWith(streamingPrefix, StringComparison.OrdinalIgnoreCase))
                relative = relative.Substring(streamingPrefix.Length);
            else if (relative.StartsWith(pluginPrefix, StringComparison.OrdinalIgnoreCase))
                relative = relative.Substring(pluginPrefix.Length);
            return Path.Combine(Application.streamingAssetsPath, relative).Replace('\\', '/');
#endif
        }

        static long TexArrayVram(Texture2DArray t)
        {
            if (t == null) return 0;
            long bytes = 0;
            int w = t.width, h = t.height;
            for (int mip = 0; mip < t.mipmapCount; mip++)
            {
                bytes += (long)Mathf.Max(w, 1) * Mathf.Max(h, 1) * t.depth * 4;
                w >>= 1;
                h >>= 1;
            }
            return bytes;
        }

        static string MakeRelative(string absPath)
        {
            string dataPath = Application.dataPath.Replace('\\', '/');
            string ap = absPath.Replace('\\', '/');
            if (ap.StartsWith(dataPath, StringComparison.OrdinalIgnoreCase))
                return ap.Substring(dataPath.Length).TrimStart('/');
            return absPath;
        }

        void ReleaseResourcesForMode(NtcMode destination)
        {
            if (destination != NtcMode.InferenceOnSample && _streamer != null)
                _streamer.ReleaseAll();

            if (groups == null) return;
            foreach (MaterialGroup g in groups)
            {
                if (g == null) continue;
                if (destination != NtcMode.InferenceOnSampleDirect && g.onSampleMaterial != null
                    && g.onSampleMaterial.shader == directSampleShader)
                {
                    DestroyUnityObject(g.onSampleMaterial);
                    g.onSampleMaterial = null;
                }
                if (destination != NtcMode.InferenceOnSample)
                {
                    g.streamActive = false;
                    g.streamBoundVersion = -1;
                    DestroyUnityObject(g.streamMaterial);
                    g.streamMaterial = null;
                }
                if (destination != NtcMode.InferenceOnLoad)
                    ReleaseOnLoadCache(g);

                // Original owns no NTC runtime allocation. On-Load and on-Sample may reuse
                // latent/weight inputs while switching directly between those two modes.
                if (destination == NtcMode.Original)
                {
                    ReleaseNtcInputAssets(g);
                    DestroyUnityObject(g.onSampleMaterial);
                    g.onSampleMaterial = null;
                }
            }
        }

        static void ReleaseOwnedTexture(ref Texture2D texture, ref bool owned)
        {
            if (owned) DestroyUnityObject(texture);
            if (owned) texture = null;
            owned = false;
        }

        static void ReleaseOnLoadCache(MaterialGroup g)
        {
            if (g == null) return;
            if (g.onLoadTargets != null)
            {
                g.onLoadTargets.Release();
                g.onLoadTargets = null;
            }
            ReleaseOwnedTexture(ref g.ntcColor, ref g.ownsNtcColor);
            ReleaseOwnedTexture(ref g.ntcNormal, ref g.ownsNtcNormal);
            ReleaseOwnedTexture(ref g.ntcMetalness, ref g.ownsNtcMetalness);
            ReleaseOwnedTexture(ref g.ntcAO, ref g.ownsNtcAO);
            ReleaseOwnedTexture(ref g.ntcSmoothness, ref g.ownsNtcSmoothness);
            ReleaseOwnedTexture(ref g.ntcAlpha, ref g.ownsNtcAlpha);
            DestroyUnityObject(g.colorWithAlphaMap);
            DestroyUnityObject(g.metallicGlossMap);
            DestroyUnityObject(g.onLoadMaterial);
            g.colorWithAlphaMap = null;
            g.metallicGlossMap = null;
            g.onLoadMaterial = null;
        }

        static void ReleaseNtcInputAssets(MaterialGroup g)
        {
            if (g == null) return;
            if (g.assets != null) g.assets.Release();
            g.assets = null;
            g.container = null;
            g.hasNtcData = false;
        }

        static void ReleaseGroupGpuAssets(MaterialGroup g)
        {
            if (g == null) return;
            ReleaseNtcInputAssets(g);
            ReleaseOnLoadCache(g);
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
            // A GameObject deactivation stops coroutines, while disabling only this
            // component does not. Cancel explicitly in both cases so re-enable can
            // restart an interrupted scan instead of retaining a stuck initializing flag.
            if (_initializeRoutine != null)
            {
                StopCoroutine(_initializeRoutine);
                _initializeRoutine = null;
            }
            if (_initializing)
            {
                _initializing = false;
                // Initialization can already own the compute decompressor's dummy UAVs.
                // No material modes have been applied yet, so these are its only GPU owners.
                if (_streamer != null) _streamer.ReleaseAll();
                _streamer = null;
                if (_decompressor != null) _decompressor.Dispose();
                _decompressor = null;
            }
            if (!_initialized || groups == null) return;
            if (_applyRoutine != null)
            {
                StopCoroutine(_applyRoutine);
                _applyRoutine = null;
            }
            _applyingMode = false;
            foreach (var g in groups)
                if (g != null)
                    RestoreOriginalMaterialRefs(g);
            ReleaseResourcesForMode(NtcMode.Original);
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
                DestroyUnityObject(g.colorWithAlphaMap);
                DestroyUnityObject(g.metallicGlossMap);
                g.onLoadMaterial = null;
                g.onSampleMaterial = null;
                g.streamMaterial = null;
                g.debugViewMaterial = null;
                g.colorWithAlphaMap = null;
                g.metallicGlossMap = null;
                NtcMaterialSlotRegistry.Remove(g);
            }
        }
    }
}
