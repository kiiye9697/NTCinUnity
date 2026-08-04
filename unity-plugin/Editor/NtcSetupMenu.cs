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

        static void MarkDirty(GameObject go)
        {
            EditorUtility.SetDirty(go);
            if (!Application.isPlaying)
                EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
        }
    }
}
