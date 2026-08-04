// NtcBakeWindow.cs
// Editor window for offline NTC baking. It scans materials in the currently opened
// scene, shows which material bundles already have .ntc data, and can bake/rebake
// selected materials via ntc-cli without blocking the Unity editor UI.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Process = System.Diagnostics.Process;
using ProcessStartInfo = System.Diagnostics.ProcessStartInfo;
using UnityEditor;
using UnityEngine;
using Ntc.Packing;

namespace Ntc.UnityEditorTools
{
    public class NtcBakeWindow : EditorWindow
    {
        class MaterialEntry
        {
            public string materialName;
            public readonly List<TextureRef> textures = new List<TextureRef>();
            public bool selected = true;
            public bool hasNtc;
            public string ntcPath;
            public long ntcSize;
            public string psnr;
            public string status;
        }

        class TextureRef
        {
            public string sourcePath;
            public string outName;
            public bool isSrgb;
            public string bcFormat;
            public string semantic;
            public string channelSwizzle;
        }

        struct TextureSpec
        {
            public string Suffix;
            public string OutName;
            public bool IsSrgb;
            public string BcFormat;
            public string Semantic;
            public string ChannelSwizzle;

            public TextureSpec(string suffix, string outName, bool isSrgb, string bcFormat, string semantic, string channelSwizzle)
            {
                Suffix = suffix;
                OutName = outName;
                IsSrgb = isSrgb;
                BcFormat = bcFormat;
                Semantic = semantic;
                ChannelSwizzle = channelSwizzle;
            }
        }

        static readonly TextureSpec[] kTextureSpecs =
        {
            new TextureSpec("Diffuse",    "Color",      true,  "BC7", "Albedo",    "RGB"),
            new TextureSpec("Normal",     "Normal",     false, "BC7", "Normal",    "RGB"),
            new TextureSpec("AO",         "AO",         false, "BC4", "Occlusion", "R"),
            new TextureSpec("Metallic",   "Metalness",  false, "BC4", "Metalness", "R"),
            // NOTE: semantic is "Glossiness", NOT "Roughness" -- the source data (*_Smoothness.tif,
            // or the alpha channel of URP's packed _MetallicGlossMap) is smoothness/glossiness
            // (1 - roughness), never literal roughness. Mislabeling this as "Roughness" in the
            // manifest was a bug found 2026-07-09: it doesn't affect Unity's own runtime rendering
            // (NtcAssetBuilder.BuildPbrLayout keys off the texture's *name* field, "Smoothness", not
            // its semantics), but it is wrong metadata that would mislead any other manifest/semantic
            // consumer (e.g. NVIDIA's Explorer tool) into treating smoothness values as roughness
            // (visually inverted shininess). See docs/验证记录.md 2026-07-09 entry.
            new TextureSpec("Smoothness", "Smoothness", false, "BC4", "Glossiness", "R"),
            // Alpha/cutout mask, extracted from the SAME source file as Diffuse (its alpha
            // channel), not a separate "*_Alpha.tif" convention file -- see
            // AddAlphaCutoutFromBaseMap(). Semantic "AlphaMask" matches RTXNTC's special-cased
            // label (docs/SettingsAndQuality.md): NTC preserves 0.0/1.0 with extra accuracy for
            // whichever texture declares this semantic, which is exactly what a binary/soft
            // alpha-test edge (billboard vegetation, Ivy, Vase_Round_Plants, ...) needs. Fixes
            // the "exposed billboard rectangle" bug (docs/验证记录.md 2026-07-06 entry): the BaseColor
            // RT's alpha was hardcoded to 1.0 end-to-end because no stage of the pipeline ever
            // captured/transmitted the source alpha channel.
            new TextureSpec("Alpha",      "Alpha",      false, "BC4", "AlphaMask", "A"),
        };

        const string kPrefNtcCli = "NTC_Bake_ntcCliPath";
        const string kPrefPython = "NTC_Bake_pythonPath";
        const string kPrefBpp = "NTC_Bake_bpp";
        const string kPrefMetalLossScale = "NTC_Bake_metalLossScale";
        const string kPrefSmoothLossScale = "NTC_Bake_smoothLossScale";
        const string kPrefTextureSourcePath = "NTC_Bake_textureSourcePath";
        const string kPrefNtcFilePrefix = "NTC_Bake_ntcFilePrefix";

        string _ntcCliPath;
        string _pythonPath;
        string _outputDir;
        float _bpp;
        float _metalLossScale;
        float _smoothLossScale;
        // Convention-based texture lookup root (relative to Assets/), used by
        // TryFindTexturesByConvention when a material has no _BaseMap/_MainTex assigned. Empty =
        // skip convention lookup entirely. Was hardcoded "ClassicSponza/Art/Sponza/Textures".
        string _textureSourcePath;
        // Prefix prepended to {materialName}.ntc in the output dir, e.g. "Sponza" -> SponzaBricks.ntc.
        // Empty = no prefix. Was hardcoded "Sponza".
        string _ntcFilePrefix;

        readonly List<MaterialEntry> _entries = new List<MaterialEntry>();
        readonly object _stateLock = new object();
        Vector2 _scroll;
        bool _scanned;
        volatile bool _baking;
        volatile bool _pendingRefresh;
        float _bakeProgress;
        string _bakeStatus = "";
        string _bakeLog = "";
        string _bakeError = "";
        Task _bakeTask;

        // GUI-thread-owned snapshot of the volatile bake state above. IMGUI calls OnGUI at
        // least twice per visual frame (Layout, then Repaint, plus extra calls for input
        // events); if the background bake thread mutates _bakeLog/_bakeError/etc. between
        // those calls, the number of controls drawn by DrawBakeControls can differ between
        // the Layout pass and the Repaint pass, which throws
        // "Getting control X's position in a group with only Y controls" and aborts the
        // window's GUI. To avoid that, the snapshot below is refreshed ONLY on the Layout
        // event and then reused unchanged for every other event type in the same visual
        // frame, guaranteeing Layout and Repaint always see identical values.
        bool _uiBaking;
        float _uiBakeProgress;
        string _uiBakeStatus = "";
        string _uiBakeError = "";
        string _uiBakeLog = "";

        public static void ShowWindow()
        {
            var window = GetWindow<NtcBakeWindow>("NTC Bake");
            window.minSize = new Vector2(560, 440);
        }

        void OnEnable()
        {
            _ntcCliPath = EditorPrefs.GetString(kPrefNtcCli, AutoDetectNtcCli());
            _pythonPath = EditorPrefs.GetString(kPrefPython, "python");
            _bpp = EditorPrefs.GetFloat(kPrefBpp, 16f);
            _metalLossScale = EditorPrefs.GetFloat(kPrefMetalLossScale, 24f);
            _smoothLossScale = EditorPrefs.GetFloat(kPrefSmoothLossScale, 16f);
            _textureSourcePath = EditorPrefs.GetString(kPrefTextureSourcePath, "");
            _ntcFilePrefix = EditorPrefs.GetString(kPrefNtcFilePrefix, "");
            _outputDir = Path.Combine(Application.dataPath, "NtcPlugin/NtcData").Replace('\\', '/');
            EditorApplication.update += OnEditorUpdate;
        }

        void OnDisable()
        {
            EditorPrefs.SetString(kPrefNtcCli, _ntcCliPath ?? "");
            EditorPrefs.SetString(kPrefPython, _pythonPath ?? "python");
            EditorPrefs.SetFloat(kPrefBpp, _bpp);
            EditorPrefs.SetFloat(kPrefMetalLossScale, _metalLossScale);
            EditorPrefs.SetFloat(kPrefSmoothLossScale, _smoothLossScale);
            EditorPrefs.SetString(kPrefTextureSourcePath, _textureSourcePath ?? "");
            EditorPrefs.SetString(kPrefNtcFilePrefix, _ntcFilePrefix ?? "");
            EditorApplication.update -= OnEditorUpdate;
        }

        static string AutoDetectNtcCli()
        {
            // Probe common locations; fall back to PATH lookup so the plugin doesn't assume a
            // specific sibling-repo layout (was hardcoded "../../RTXNTC/bin/windows-x64/ntc-cli.exe").
            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string hit = Path.Combine(projectRoot, "ntc-cli.exe");
            if (File.Exists(hit)) return hit.Replace('\\', '/');

            string packagesDir = Path.Combine(projectRoot, "Packages");
            if (Directory.Exists(packagesDir))
            {
                foreach (string dir in Directory.GetDirectories(packagesDir))
                {
                    hit = Path.Combine(dir, "ntc-cli.exe");
                    if (File.Exists(hit)) return hit.Replace('\\', '/');
                    hit = Path.Combine(dir, "bin/windows-x64/ntc-cli.exe");
                    if (File.Exists(hit)) return hit.Replace('\\', '/');
                }
            }
            return "ntc-cli.exe";
        }

        void OnEditorUpdate()
        {
            if (_pendingRefresh)
            {
                _pendingRefresh = false;
                AssetDatabase.Refresh();
                if (_scanned)
                    RefreshBakeStatuses();
                Repaint();
            }

            if (_baking)
                Repaint();
        }

        void OnGUI()
        {
            // Freeze the bake-state snapshot exactly once per visual frame (on Layout),
            // so the Repaint pass (and any input-event passes) that follow use the same
            // values -- see the comment on the _uiBake* fields above.
            if (Event.current.type == EventType.Layout)
            {
                lock (_stateLock)
                {
                    _uiBaking = _baking;
                    _uiBakeProgress = _bakeProgress;
                    _uiBakeStatus = _bakeStatus;
                    _uiBakeError = _bakeError;
                    _uiBakeLog = _bakeLog;
                }
            }

            GUILayout.Label("NTC Material Bake", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Bake = offline asset preparation, like Lighting bake. It scans current scene materials, compresses PBR textures into .ntc files, and writes preview PNGs for on-Load.\n\n" +
                "Streaming/runtime mode is NOT switched here. Add NTC / Add Scene Controller, enter Play, then use its on-screen buttons: Original / on-Load / on-Sample.",
                MessageType.Info);

            DrawConfig();
            EditorGUILayout.Space();

            GUI.enabled = !_uiBaking;
            if (GUILayout.Button("Scan Scene Materials", GUILayout.Height(28)))
                ScanScene();
            GUI.enabled = true;

            if (!_scanned)
            {
                EditorGUILayout.HelpBox("Click 'Scan Scene Materials' to find all materials in the currently opened scene.", MessageType.None);
                return;
            }

            DrawMaterialList();
            EditorGUILayout.Space();
            DrawBakeControls();
        }

        // Renders the raw slider float as "Nx vs. other channels" so the number has an explicit,
        // self-contained meaning (relative training-loss weight against every other channel's
        // implicit 1x) instead of being an opaque unitless value.
        static string LossScaleLabel(float scale)
        {
            return scale.ToString("0.#", CultureInfo.InvariantCulture) + "x vs others";
        }

        void DrawConfig()
        {
            EditorGUILayout.LabelField("Settings", EditorStyles.miniBoldLabel);
            _ntcCliPath = EditorGUILayout.TextField("ntc-cli", _ntcCliPath);
            _pythonPath = EditorGUILayout.TextField("Python", _pythonPath);
            _bpp = EditorGUILayout.Slider("Bits per pixel", _bpp, 1f, 20f);
            EditorGUILayout.HelpBox("ntc-cli only supports a fixed set of latent shapes topping out at 20 bpp (KnownLatentShapes table); values above 20 fail with 'Cannot select a latent shape'. Use 15-20 bpp for high-frequency fabrics/vegetation or metal masks; 16 bpp is a reasonable default.", MessageType.None);
            _metalLossScale = EditorGUILayout.Slider("Metalness loss scale (" + LossScaleLabel(_metalLossScale) + ")", _metalLossScale, 1f, 32f);
            EditorGUILayout.HelpBox("Meaning of the number: every OTHER channel (Color/Normal/AO/Smoothness) trains with an implicit weight of 1x -- this is how many times more training-loss weight Metalness gets relative to them, i.e. \"how hard the network is punished for getting Metalness wrong\" vs. the rest. It does NOT change bpp or file size, only how the SAME fixed budget is allocated. NTC shares that fixed bpp budget across all channels of a material, so binary/high-contrast masks (e.g. a metal/non-metal mask) can get crushed toward a constant value when other channels dominate. Verified fix: Flagpoles metal mask mean 201.84 (src) collapsed to 27.46 at scale=8/16bpp (8x); scale=24 (24x, same 16bpp, same file size) restored it to 201.80. Prefer raising this scale over raising bpp when only a mask channel is broken. See RTXNTC docs/SettingsAndQuality.md.", MessageType.None);
            _smoothLossScale = EditorGUILayout.Slider("Smoothness/Roughness loss scale (" + LossScaleLabel(_smoothLossScale) + ")", _smoothLossScale, 1f, 32f);
            EditorGUILayout.HelpBox("Same meaning and mechanism as Metalness loss scale above (relative weight vs. the other channels' implicit 1x, same fixed bpp budget), applied to the Smoothness/Roughness channel instead. Measured 2026-07-10 across all 17 baked Sponza materials at the previous default of 1x: decompressed-vs-source pixel stddev ratio for Smoothness averages ~0.94 (worst case Ceiling 0.824), noticeably worse than Color (~0.999), Normal (~0.98) and AO (~0.987) in the SAME materials -- i.e. this channel was losing more fine detail/contrast than the others. Rebake affected materials and re-check the pixel comparison after changing this; if 16x is not enough, try stepping up toward 24x (the value that fixed Metalness) before going higher.", MessageType.None);
            _outputDir = EditorGUILayout.TextField("Output dir", _outputDir);
            _textureSourcePath = EditorGUILayout.TextField("Texture source (Assets/ subpath)", _textureSourcePath);
            EditorGUILayout.HelpBox("Convention-based texture lookup root (relative to Assets/), used only when a material has no _BaseMap/_MainTex assigned. Empty = skip. Was hardcoded to the Sponza art folder.", MessageType.None);
            _ntcFilePrefix = EditorGUILayout.TextField(".ntc file name prefix", _ntcFilePrefix);
            EditorGUILayout.HelpBox("Prefix prepended to {materialName}.ntc in the output dir, e.g. \"Sponza\" -> SponzaBricks.ntc. Empty = no prefix.", MessageType.None);

            if (!File.Exists(_ntcCliPath))
                EditorGUILayout.HelpBox("ntc-cli not found at the specified path.", MessageType.Warning);
        }

        void DrawMaterialList()
        {
            GUILayout.Label("Materials (" + _entries.Count + ")", EditorStyles.miniBoldLabel);
            _scroll = EditorGUILayout.BeginScrollView(_scroll, GUILayout.MinHeight(210));
            for (int i = 0; i < _entries.Count; i++)
                DrawEntry(_entries[i]);
            EditorGUILayout.EndScrollView();
        }

        void DrawEntry(MaterialEntry e)
        {
            EditorGUILayout.BeginHorizontal("box");
            e.selected = EditorGUILayout.Toggle(e.selected, GUILayout.Width(20));

            Color prev = GUI.color;
            GUI.color = e.hasNtc ? Color.green : new Color(0.7f, 0.7f, 0.7f);
            GUILayout.Label(e.hasNtc ? "Baked" : "--", GUILayout.Width(52));
            GUI.color = prev;

            GUILayout.Label(e.materialName, GUILayout.Width(140));
            GUILayout.Label(e.textures.Count + " tex", GUILayout.Width(48));
            GUILayout.Label(string.IsNullOrEmpty(e.psnr) ? "" : e.psnr + " dB", GUILayout.Width(72));
            GUILayout.Label(e.hasNtc ? FormatMB(e.ntcSize) : "", GUILayout.Width(72));
            GUILayout.Label(e.status ?? "", GUILayout.Width(110));

            GUILayout.FlexibleSpace();
            GUI.enabled = !_uiBaking && e.textures.Count >= 2;
            if (GUILayout.Button(e.hasNtc ? "Rebake" : "Bake", GUILayout.Width(64)))
            {
                StartBake(new List<MaterialEntry> { e });
                GUIUtility.ExitGUI();
            }
            GUI.enabled = true;
            EditorGUILayout.EndHorizontal();
        }

        void DrawBakeControls()
        {
            EditorGUILayout.BeginHorizontal();
            GUI.enabled = !_uiBaking;
            if (GUILayout.Button("Bake All Selected", GUILayout.Height(28)))
                StartBake(_entries.FindAll(e => e.selected && e.textures.Count >= 2));
            if (GUILayout.Button("Select All", GUILayout.Width(86)))
                foreach (MaterialEntry e in _entries) e.selected = true;
            if (GUILayout.Button("Select None", GUILayout.Width(96)))
                foreach (MaterialEntry e in _entries) e.selected = false;
            // Convenience selectors for batch bake/rebake workflows: pick only the
            // materials that still need a first bake, or only the ones that already
            // have a .ntc and would be REBAKED (e.g. after changing loss-scale sliders
            // or the texture map, to force existing files to regenerate instead of
            // silently keeping stale data -- see docs/验证记录.md 2026-07-10 batch script bug).
            if (GUILayout.Button("Select Unbaked", GUILayout.Width(104)))
                foreach (MaterialEntry e in _entries) e.selected = !e.hasNtc;
            if (GUILayout.Button("Select Baked (Rebake)", GUILayout.Width(148)))
                foreach (MaterialEntry e in _entries) e.selected = e.hasNtc;
            GUI.enabled = true;
            EditorGUILayout.EndHorizontal();

            if (_uiBaking || !string.IsNullOrEmpty(_uiBakeStatus))
            {
                Rect rect = EditorGUILayout.GetControlRect(false, 18f);
                EditorGUI.ProgressBar(rect, _uiBakeProgress, _uiBakeStatus ?? "");
            }

            if (!string.IsNullOrEmpty(_uiBakeError))
                EditorGUILayout.HelpBox(_uiBakeError, MessageType.Error);

            if (!string.IsNullOrEmpty(_uiBakeLog))
            {
                EditorGUILayout.LabelField("ntc-cli output", EditorStyles.miniBoldLabel);
                EditorGUILayout.TextArea(_uiBakeLog, GUILayout.MinHeight(80));
            }
        }

        void ScanScene()
        {
            _entries.Clear();

            var byMaterial = new Dictionary<string, Material>();
#if UNITY_2022_2_OR_NEWER
            Renderer[] renderers = UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None);
#else
            Renderer[] renderers = UnityEngine.Object.FindObjectsOfType<Renderer>();
#endif
            foreach (Renderer r in renderers)
            {
                Material m = r.sharedMaterial;
                if (m == null) continue;
                if (!byMaterial.ContainsKey(m.name))
                    byMaterial.Add(m.name, m);
            }

            foreach (KeyValuePair<string, Material> pair in byMaterial)
            {
                var entry = new MaterialEntry { materialName = pair.Key };
                CollectTextures(pair.Value, entry);
                CheckBakeStatus(entry);
                _entries.Add(entry);
            }

            _scanned = true;
            UnityEngine.Debug.Log("[NTC Bake] Scanned " + _entries.Count + " materials, " + _entries.FindAll(e => e.hasNtc).Count + " baked.");
        }

        void RefreshBakeStatuses()
        {
            foreach (MaterialEntry entry in _entries)
                CheckBakeStatus(entry);
        }

        void CollectTextures(Material mat, MaterialEntry entry)
        {
            AddTextureFromProperty(mat, entry, "_BaseMap", "Diffuse");
            AddTextureFromProperty(mat, entry, "_MainTex", "Diffuse");
            AddTextureFromProperty(mat, entry, "_BumpMap", "Normal");
            AddTextureFromProperty(mat, entry, "_NormalMap", "Normal");
            AddTextureFromProperty(mat, entry, "_OcclusionMap", "AO");

            // URP Lit packs metallic in R and smoothness in A of _MetallicGlossMap.
            // Bake them as two scalar NTC textures instead of wasting RGB channels on the same map.
            AddPackedMetallicGlossFromProperty(mat, entry, "_MetallicGlossMap");
            AddTextureFromProperty(mat, entry, "_SmoothnessMap", "Smoothness");

            // Alpha-test cutout mask (billboard vegetation, Ivy, Vase_Round_Plants, ...): pull
            // the alpha channel of the SAME BaseMap/MainTex source image (not a separate file)
            // as its own scalar NTC texture. See AddAlphaCutoutFromBaseMap for why this is
            // gated on the material actually alpha-testing, and docs/验证记录.md 2026-07-06/07-13
            // entries for the bug this fixes.
            AddAlphaCutoutFromBaseMap(mat, entry, "_BaseMap");
            if (entry.textures.Exists(t => t.outName == "Alpha") == false)
                AddAlphaCutoutFromBaseMap(mat, entry, "_MainTex");

            if (entry.textures.Count == 0)
                TryFindTexturesByConvention(entry);
        }

        // Only materials that actually alpha-test (URP Lit's "_ALPHATEST_ON" keyword +
        // "_AlphaClip"=1, i.e. RenderType TransparentCutout) get their BaseMap alpha baked
        // as a channel -- opaque materials' BaseMap alpha is typically unused padding (often
        // flat 255) and would just burn bpp budget for nothing. If a later material needs the
        // alpha channel for a different reason (e.g. detail blending), relax this gate.
        void AddAlphaCutoutFromBaseMap(Material mat, MaterialEntry entry, string property)
        {
            if (!mat.HasProperty(property)) return;
            if (!mat.HasProperty("_AlphaClip") || mat.GetFloat("_AlphaClip") <= 0.5f)
                if (!mat.IsKeywordEnabled("_ALPHATEST_ON"))
                    return;

            Texture texture = mat.GetTexture(property);
            if (texture == null) return;
            string assetPath = AssetDatabase.GetAssetPath(texture);
            if (string.IsNullOrEmpty(assetPath)) return;
            if (!SourceTextureHasAlpha(assetPath)) return;

            TextureSpec alpha = SpecForSuffix("Alpha");
            if (entry.textures.Exists(t => t.outName == alpha.OutName)) return;
            AddTextureRef(entry, assetPath, alpha, "A");
        }

        void AddTextureFromProperty(Material mat, MaterialEntry entry, string property, string suffix)
        {
            TextureSpec spec = SpecForSuffix(suffix);
            AddTextureFromProperty(mat, entry, property, spec, spec.ChannelSwizzle);
        }

        void AddPackedMetallicGlossFromProperty(Material mat, MaterialEntry entry, string property)
        {
            if (!mat.HasProperty(property)) return;
            Texture texture = mat.GetTexture(property);
            if (texture == null) return;
            string assetPath = AssetDatabase.GetAssetPath(texture);
            if (string.IsNullOrEmpty(assetPath)) return;

            TextureSpec metal = SpecForSuffix("Metallic");
            if (!entry.textures.Exists(t => t.outName == metal.OutName))
                AddTextureRef(entry, assetPath, metal, "R");

            TextureSpec smooth = SpecForSuffix("Smoothness");
            if (!entry.textures.Exists(t => t.outName == smooth.OutName) && SourceTextureHasAlpha(assetPath))
                AddTextureRef(entry, assetPath, smooth, "A");
        }

        static bool SourceTextureHasAlpha(string assetPath)
        {
            TextureImporter importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
            return importer != null && importer.DoesSourceTextureHaveAlpha();
        }

        void AddTextureFromProperty(Material mat, MaterialEntry entry, string property, TextureSpec spec, string channelSwizzle)
        {
            if (!mat.HasProperty(property)) return;
            Texture texture = mat.GetTexture(property);
            if (texture == null) return;
            if (entry.textures.Exists(t => t.outName == spec.OutName)) return;

            string assetPath = AssetDatabase.GetAssetPath(texture);
            if (string.IsNullOrEmpty(assetPath)) return;
            AddTextureRef(entry, assetPath, spec, channelSwizzle);
        }

        static void AddTextureRef(MaterialEntry entry, string assetPath, TextureSpec spec, string channelSwizzle)
        {
            entry.textures.Add(new TextureRef
            {
                sourcePath = Path.GetFullPath(assetPath),
                outName = spec.OutName,
                isSrgb = spec.IsSrgb,
                bcFormat = spec.BcFormat,
                semantic = spec.Semantic,
                channelSwizzle = channelSwizzle
            });
        }

        void TryFindTexturesByConvention(MaterialEntry entry)
        {
            if (string.IsNullOrEmpty(_textureSourcePath)) return;
            string texRoot = Path.Combine(Application.dataPath, _textureSourcePath);
            string matDir = Path.Combine(texRoot, entry.materialName);
            if (!Directory.Exists(matDir))
                matDir = Path.Combine(texRoot, entry.materialName.Replace(" ", ""));
            if (!Directory.Exists(matDir)) return;

            foreach (TextureSpec spec in kTextureSpecs)
            {
                foreach (string ext in new[] { ".tif", ".tiff", ".png", ".psd" })
                {
                    string candidate = Path.Combine(matDir, entry.materialName + "_" + spec.Suffix + ext);
                    if (!File.Exists(candidate)) continue;
                    entry.textures.Add(new TextureRef
                    {
                        sourcePath = candidate,
                        outName = spec.OutName,
                        isSrgb = spec.IsSrgb,
                        bcFormat = spec.BcFormat,
                        semantic = spec.Semantic,
                        channelSwizzle = spec.ChannelSwizzle
                    });
                    break;
                }
            }
        }

        static TextureSpec SpecForSuffix(string suffix)
        {
            foreach (TextureSpec spec in kTextureSpecs)
                if (spec.Suffix == suffix)
                    return spec;
            return new TextureSpec(suffix, suffix, false, "BC7", suffix, "RGB");
        }

        void CheckBakeStatus(MaterialEntry entry)
        {
            entry.ntcPath = Path.Combine(_outputDir, entry.materialName + ".ntc");
            if (!File.Exists(entry.ntcPath) && !string.IsNullOrEmpty(_ntcFilePrefix))
                entry.ntcPath = Path.Combine(_outputDir, _ntcFilePrefix + entry.materialName + ".ntc");

            entry.hasNtc = File.Exists(entry.ntcPath);
            entry.ntcSize = entry.hasNtc ? new FileInfo(entry.ntcPath).Length : 0;
            if (!entry.hasNtc)
            {
                entry.status = "not baked";
                return;
            }

            try
            {
                NtcContainer container = NtcContainerReader.Load(entry.ntcPath);
                bool hasCompressedViews = false;
                foreach (NtcBufferView view in container.Document.Views)
                {
                    if (view.IsCompressed)
                    {
                        hasCompressedViews = true;
                        break;
                    }
                }
                entry.status = hasCompressedViews ? "GDeflate: rebake" : "OK";
            }
            catch (Exception e)
            {
                entry.status = "parse error";
                UnityEngine.Debug.LogError("[NTC Bake] Failed to parse " + entry.ntcPath + ": " + e.Message);
            }
        }

        void StartBake(List<MaterialEntry> toBake)
        {
            if (_baking) return;
            if (toBake == null || toBake.Count == 0)
            {
                EditorUtility.DisplayDialog("NTC Bake", "No materials selected (or selected materials have fewer than 2 source textures).", "OK");
                return;
            }
            if (!File.Exists(_ntcCliPath))
            {
                EditorUtility.DisplayDialog("NTC Bake", "ntc-cli not found:\n" + _ntcCliPath, "OK");
                return;
            }

            string ntcCli = _ntcCliPath;
            string python = string.IsNullOrEmpty(_pythonPath) ? "python" : _pythonPath;
            string outputDir = _outputDir;
            float bpp = _bpp;
            float metalLossScale = _metalLossScale;
            float smoothLossScale = _smoothLossScale;

            lock (_stateLock)
            {
                _bakeProgress = 0f;
                _bakeStatus = "Starting bake...";
                _bakeLog = "";
                _bakeError = "";
                _baking = true;
            }

            _bakeTask = Task.Run(() => BakeQueueWorker(toBake, ntcCli, python, outputDir, bpp, metalLossScale, smoothLossScale));
        }

        void BakeQueueWorker(List<MaterialEntry> toBake, string ntcCli, string python, string outputDir, float bpp, float metalLossScale, float smoothLossScale)
        {
            int done = 0;
            try
            {
                for (int i = 0; i < toBake.Count; i++)
                {
                    BakeSingleWorker(toBake[i], i, toBake.Count, ntcCli, python, outputDir, bpp, metalLossScale, smoothLossScale);
                    done++;
                    SetBakeProgress((float)done / Math.Max(toBake.Count, 1), "Completed " + done + "/" + toBake.Count);
                }
                SetBakeProgress(1f, "Done. Baked " + done + "/" + toBake.Count + " materials.");
            }
            catch (Exception e)
            {
                SetBakeError(e.Message);
                SetBakeProgress((float)done / Math.Max(toBake.Count, 1), "Stopped after " + done + "/" + toBake.Count);
            }
            finally
            {
                _baking = false;
                _pendingRefresh = true;
            }
        }

        void BakeSingleWorker(MaterialEntry entry, int index, int total, string ntcCli, string python, string outputDir, float bpp, float metalLossScale, float smoothLossScale)
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "ntc_bake_" + SanitizeFileName(entry.materialName));
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
            Directory.CreateDirectory(tempDir);

            try
            {
                entry.status = "converting";
                UpdateMaterialProgress(index, total, 0.02f, entry.materialName + ": converting source textures...");
                var convertedTextures = new List<TextureRef>();
                foreach (TextureRef tex in entry.textures)
                {
                    if (ConvertToPng(tex.sourcePath, Path.Combine(tempDir, tex.outName + ".png"), python, tex.channelSwizzle))
                        convertedTextures.Add(tex);
                    else
                        AppendBakeLog("WARNING: " + entry.materialName + " skipped missing channel for " + tex.outName + " (" + tex.channelSwizzle + ")");
                }
                if (convertedTextures.Count < 2)
                {
                    entry.status = "skipped: not enough channels";
                    UpdateMaterialProgress(index, total, 1f, entry.materialName + ": skipped (not enough source textures after missing-channel filtering)");
                    return;
                }

                entry.status = "manifest";
                string manifestPath = Path.Combine(tempDir, "Manifest.json");
                WriteManifestFile(manifestPath, convertedTextures, metalLossScale, smoothLossScale);

                entry.status = "compressing";
                string ntcOutput = Path.Combine(outputDir, (_ntcFilePrefix ?? "") + entry.materialName + ".ntc");
                Directory.CreateDirectory(outputDir);
                string args = "--loadManifest \"" + manifestPath + "\" -g -c -b " + bpp.ToString(CultureInfo.InvariantCulture) + " --gdeflate off -D -o \"" + ntcOutput + "\"";
                string stdout = RunProcessStreaming(ntcCli, args, line =>
                {
                    AppendBakeLog(line);
                    int step = TryParseTrainingStep(line);
                    if (step >= 0)
                    {
                        float local = Clamp01(step / 100000f) * 0.80f + 0.10f;
                        UpdateMaterialProgress(index, total, local, entry.materialName + ": training " + step + "/100000...");
                    }
                });
                entry.psnr = ExtractPsnr(stdout);

                entry.status = "decompressing";
                UpdateMaterialProgress(index, total, 0.92f, entry.materialName + ": decompressing preview PNGs...");
                string decompDir = Path.Combine(outputDir, "Decompressed", Path.GetFileNameWithoutExtension(ntcOutput));
                Directory.CreateDirectory(decompDir);
                args = "--loadCompressed \"" + ntcOutput + "\" -D -i \"" + decompDir + "\" -F png -B none";
                RunProcessStreaming(ntcCli, args, AppendBakeLog);

                CheckBakeStatus(entry);
                UpdateMaterialProgress(index, total, 1f, entry.materialName + ": baked OK (" + FormatMB(entry.ntcSize) + ")");
                UnityEngine.Debug.Log("[NTC Bake] " + entry.materialName + ": " + entry.psnr + " dB, " + FormatMB(entry.ntcSize));
            }
            catch (Exception e)
            {
                entry.status = "ERROR";
                SetBakeError(entry.materialName + ": " + e.Message);
                UnityEngine.Debug.LogError("[NTC Bake] " + entry.materialName + " failed: " + e);
            }
            finally
            {
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
            }
        }

        void UpdateMaterialProgress(int materialIndex, int total, float localProgress, string status)
        {
            float progress = (materialIndex + Clamp01(localProgress)) / Math.Max(total, 1);
            SetBakeProgress(Clamp01(progress), status);
        }

        void SetBakeProgress(float progress, string status)
        {
            lock (_stateLock)
            {
                _bakeProgress = Clamp01(progress);
                _bakeStatus = status;
            }
        }

        void SetBakeError(string error)
        {
            lock (_stateLock)
                _bakeError = error;
        }

        static void WriteManifestFile(string path, List<TextureRef> textures, float metalLossScale, float smoothLossScale)
        {
            var sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine("  \"textures\": [");
            for (int i = 0; i < textures.Count; i++)
            {
                TextureRef t = textures[i];
                string channel = string.IsNullOrEmpty(t.channelSwizzle) ? ((t.semantic == "Albedo" || t.semantic == "Normal") ? "RGB" : "R") : t.channelSwizzle;
                bool isMetalness = !string.IsNullOrEmpty(t.semantic) && t.semantic.IndexOf("Metal", StringComparison.OrdinalIgnoreCase) >= 0;
                bool isSmoothOrRough = !string.IsNullOrEmpty(t.semantic) &&
                    (t.semantic.IndexOf("Glossiness", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     t.semantic.IndexOf("Roughness", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     t.semantic.IndexOf("Smooth", StringComparison.OrdinalIgnoreCase) >= 0);
                sb.AppendLine("    {");
                sb.AppendLine("      \"fileName\": \"" + JsonEscape(t.outName) + ".png\",");
                sb.AppendLine("      \"name\": \"" + JsonEscape(t.outName) + "\",");
                sb.AppendLine("      \"channelSwizzle\": \"" + JsonEscape(channel) + "\",");
                sb.AppendLine("      \"isSRGB\": " + (t.isSrgb ? "true" : "false") + ",");
                sb.AppendLine("      \"bcFormat\": \"" + JsonEscape(t.bcFormat) + "\",");
                if (isMetalness && Math.Abs(metalLossScale - 1f) > 0.001f)
                    sb.AppendLine("      \"lossFunctionScale\": " + metalLossScale.ToString(CultureInfo.InvariantCulture) + ",");
                else if (isSmoothOrRough && Math.Abs(smoothLossScale - 1f) > 0.001f)
                    sb.AppendLine("      \"lossFunctionScale\": " + smoothLossScale.ToString(CultureInfo.InvariantCulture) + ",");
                sb.AppendLine("      \"semantics\": {");
                sb.AppendLine("        \"" + JsonEscape(t.semantic) + "\": \"" + channel + "\"");
                sb.AppendLine("      }");
                sb.Append("    }");
                if (i + 1 < textures.Count) sb.Append(",");
                sb.AppendLine();
            }
            sb.AppendLine("  ]");
            sb.AppendLine("}");
            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        }

        static string JsonEscape(string value)
        {
            return (value ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        bool ConvertToPng(string srcPath, string outPath, string python, string channelSwizzle)
        {
            bool needsAlpha = !string.IsNullOrEmpty(channelSwizzle) && channelSwizzle.IndexOf('A') >= 0;

            string script =
                "from PIL import Image\n" +
                "src = r'" + EscapePythonPath(srcPath) + "'\n" +
                "dst = r'" + EscapePythonPath(outPath) + "'\n" +
                "needs_alpha = " + (needsAlpha ? "True" : "False") + "\n" +
                "img = Image.open(src)\n" +
                // BUG FIX (2026-07-10): Photoshop-exported *_MetallicSmoothness.tif files are
                // physically 4-sample-per-pixel (RGB + Smoothness packed in the 4th sample), but
                // Photoshop tags that 4th sample's TIFF ExtraSamples field as UNSPECIFIED (0)
                // instead of ASSOCALPHA/UNASSALPHA (1/2). PIL's TIFF plugin is conservative about
                // this and only exposes 'RGB' bands in that case -- img.getbands() NEVER contains
                // 'A' even though the 4th sample's data is present and valid (verified: real
                // smoothness values, e.g. Flagpoles alpha mean=48.7, Ceiling alpha mean=36.3).
                // This silently skipped Smoothness for 100% of the 24 Sponza materials on every
                // single bake/rebake (via this window) since the feature was added -- the debug
                // view's Roughness mode had nothing real to show. Work around it by manually
                // reinterpreting the raw decoder tile as RGBA before falling back to the
                // legitimate "really has no alpha" skip path.
                "if needs_alpha and 'A' not in img.getbands() and img.mode == 'RGB' and img.tile and img.tile[0][3][0] in ('RGBX', 'RGBXX'):\n" +
                "    img._mode = 'RGBA'\n" +
                "    img.tile = [(t[0], t[1], t[2], ('RGBA', t[3][1], t[3][2])) for t in img.tile]\n" +
                "    img.load()\n" +
                "if needs_alpha and 'A' not in img.getbands():\n" +
                "    print('NTC_SKIP_TEXTURE requested A channel but source image has mode %s' % img.mode)\n" +
                "else:\n" +
                "    if needs_alpha and img.mode != 'RGBA':\n" +
                "        img = img.convert('RGBA')\n" +
                "    elif not needs_alpha:\n" +
                "        if img.mode == 'RGBA':\n" +
                "            img = img.convert('RGB')\n" +
                "        elif img.mode not in ('RGB', 'L'):\n" +
                "            img = img.convert('RGB')\n" +
                "    img.save(dst)\n" +
                "    print('Converted %dx%d %s' % (img.size[0], img.size[1], img.mode))\n";
            string stdout = RunProcessStreaming(python, "-c \"" + script.Replace("\"", "\\\"") + "\"", AppendBakeLog);
            return stdout.IndexOf("NTC_SKIP_TEXTURE", StringComparison.Ordinal) < 0;
        }

        static string EscapePythonPath(string path)
        {
            return path.Replace("'", "\\'");
        }

        static string RunProcessStreaming(string exe, string args, Action<string> onLine)
        {
            var output = new StringBuilder();
            var error = new StringBuilder();
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using (var process = new Process { StartInfo = psi, EnableRaisingEvents = true })
            {
                process.OutputDataReceived += delegate(object sender, System.Diagnostics.DataReceivedEventArgs e)
                {
                    if (e.Data == null) return;
                    lock (output) output.AppendLine(e.Data);
                    if (onLine != null) onLine(e.Data);
                };
                process.ErrorDataReceived += delegate(object sender, System.Diagnostics.DataReceivedEventArgs e)
                {
                    if (e.Data == null) return;
                    lock (error) error.AppendLine(e.Data);
                    if (onLine != null) onLine(e.Data);
                };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                process.WaitForExit();
                process.WaitForExit();
                if (process.ExitCode != 0)
                    throw new Exception("Process failed (rc=" + process.ExitCode + "): " + error);
            }

            lock (output) return output.ToString();
        }

        void AppendBakeLog(string line)
        {
            if (string.IsNullOrEmpty(line)) return;
            lock (_stateLock)
            {
                string text = (_bakeLog ?? "") + line + "\n";
                string[] lines = text.Split('\n');
                int keep = Math.Min(lines.Length, 10);
                var sb = new StringBuilder();
                for (int i = lines.Length - keep; i < lines.Length; i++)
                    if (i >= 0 && !string.IsNullOrEmpty(lines[i])) sb.AppendLine(lines[i]);
                _bakeLog = sb.ToString();
            }
        }

        static int TryParseTrainingStep(string line)
        {
            if (string.IsNullOrEmpty(line) || !line.StartsWith("Training:")) return -1;
            int start = "Training:".Length;
            int end = line.IndexOf("steps", start, StringComparison.OrdinalIgnoreCase);
            if (end <= start) return -1;
            string num = line.Substring(start, end - start).Trim().Replace(",", "");
            int step;
            return int.TryParse(num, out step) ? step : -1;
        }

        static float Clamp01(float value)
        {
            if (value < 0f) return 0f;
            if (value > 1f) return 1f;
            return value;
        }

        static string SanitizeFileName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "Material";
            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name;
        }

        static string ExtractPsnr(string output)
        {
            if (string.IsNullOrEmpty(output)) return "";
            string[] lines = output.Split('\n');
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                int idx = lines[i].IndexOf("PSNR:", StringComparison.OrdinalIgnoreCase);
                if (idx < 0) continue;
                int start = idx + 5;
                int end = lines[i].IndexOf("dB", start, StringComparison.OrdinalIgnoreCase);
                if (end > start) return lines[i].Substring(start, end - start).Trim();
            }
            return "";
        }

        static string FormatMB(long bytes)
        {
            return (bytes / 1024.0 / 1024.0).ToString("0.00") + " MB";
        }
    }
}
