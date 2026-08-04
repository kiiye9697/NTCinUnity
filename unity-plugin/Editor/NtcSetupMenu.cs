// NtcSetupMenu.cs
// Minimal editor menu: add the scene controller + open the bake window.
// No hardcoded scene paths — user opens their own scene, then uses these.
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Ntc.Unity;
using Ntc.UnityEditorTools;

namespace Ntc.UnityEditorTools
{
    public static class NtcSetupMenu
    {
        [MenuItem("NTC/Add Scene Controller", false, 0)]
        public static void AddController()
        {
            GameObject go = GameObject.Find("NTC_Controller");
            if (go == null) go = new GameObject("NTC_Controller");

            NtcSceneController ctrl = go.GetComponent<NtcSceneController>();
            if (ctrl == null) ctrl = go.AddComponent<NtcSceneController>();

            if (ctrl.onSampleShader == null)
                ctrl.onSampleShader = Shader.Find("NTC/InferenceOnSample_URP");

            // Scan the scene RIGHT NOW, in Edit mode, and write the result into the component so it
            // actually persists. Scanning only inside Play mode (as the runtime coroutine also does)
            // is not enough: Unity discards any field changes made to scene objects during Play mode
            // as soon as you press Stop, so the Inspector's "groups" array would always look empty
            // afterwards even though the scan technically ran every time you pressed Play.
            ctrl.ScanSceneMaterials();

            Selection.activeGameObject = go;
            MarkDirty(go);
            Debug.Log("[NTC] NtcSceneController added and scanned " + ctrl.groups.Length +
                " material group(s). Press Play, use the on-screen buttons to switch modes. " +
                "If you add/bake more materials later, use NTC/Rescan Scene Materials.");
        }

        [MenuItem("NTC/Rescan Scene Materials", false, 1)]
        public static void RescanController()
        {
            NtcSceneController ctrl = GameObject.FindObjectOfType<NtcSceneController>();
            if (ctrl == null)
            {
                EditorUtility.DisplayDialog("NTC", "No NtcSceneController found in the open scene. Use NTC/Add Scene Controller first.", "OK");
                return;
            }

            ctrl.ScanSceneMaterials();
            Selection.activeGameObject = ctrl.gameObject;
            MarkDirty(ctrl.gameObject);
            Debug.Log("[NTC] Rescanned scene: " + ctrl.groups.Length + " material group(s) found.");
        }

        [MenuItem("NTC/Bake Window", false, 10)]
        public static void OpenBakeWindow()
        {
            NtcBakeWindow.ShowWindow();
        }

        // .ntc has no Unity importer, so Unity treats it as an unknown asset and leaves it out of
        // player builds. StreamingAssets is the one folder whose raw files are copied verbatim, so
        // anything you want a build to load has to be mirrored there before building.
        [MenuItem("NTC/Copy NtcData To StreamingAssets (for builds)", false, 11)]
        public static void CopyNtcDataToStreamingAssets()
        {
            NtcSceneController ctrl = GameObject.FindObjectOfType<NtcSceneController>();
            string rel = ctrl != null && !string.IsNullOrEmpty(ctrl.ntcDataPath)
                ? ctrl.ntcDataPath
                : "NtcPlugin/NtcData";

            string src = System.IO.Path.Combine(Application.dataPath, rel).Replace('\\', '/');
            if (!System.IO.Directory.Exists(src))
            {
                EditorUtility.DisplayDialog("NTC",
                    "No baked data found at:\n" + src +
                    "\n\nBake some materials first (NTC/Bake Window).", "OK");
                return;
            }

            string[] files = System.IO.Directory.GetFiles(src, "*.ntc", System.IO.SearchOption.TopDirectoryOnly);
            if (files.Length == 0)
            {
                EditorUtility.DisplayDialog("NTC", "No .ntc files in:\n" + src, "OK");
                return;
            }

            // Mirror only the leaf folder name; ToAbsolute() looks for StreamingAssets/NtcData.
            string leaf = System.IO.Path.GetFileName(src.TrimEnd('/'));
            string dst = System.IO.Path.Combine(Application.streamingAssetsPath, leaf).Replace('\\', '/');
            System.IO.Directory.CreateDirectory(dst);

            long bytes = 0;
            foreach (string f in files)
            {
                string target = System.IO.Path.Combine(dst, System.IO.Path.GetFileName(f));
                System.IO.File.Copy(f, target, true);
                bytes += new System.IO.FileInfo(f).Length;
            }

            AssetDatabase.Refresh();
            Debug.Log("[NTC] Copied " + files.Length + " .ntc (" +
                (bytes / (1024.0 * 1024.0)).ToString("0.0") + " MB) to " + dst +
                ". These will now be included in player builds.");
        }

        static void MarkDirty(GameObject go)
        {
            EditorUtility.SetDirty(go);
            if (!Application.isPlaying)
                EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
        }
    }
}
