using System;
using System.Collections.Generic;
using System.IO;
using Ntc.Unity;
using UnityEngine;

namespace Ntc.UnityEditorTools
{
    public static class NtcPlayerData
    {
        public static HashSet<string> RequiredInLoadedScenes()
        {
            var required = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var controller in UnityEngine.Object.FindObjectsOfType<NtcSceneController>(true))
                foreach (var group in controller.groups ?? Array.Empty<NtcSceneController.MaterialGroup>())
                {
                    if (group == null) continue;
                    string path = group.textureSet != null ? group.textureSet.streamingPath : group.ntcFilePath;
                    if (!string.IsNullOrEmpty(path)) required.Add(Path.GetFileName(path));
                }
            return required;
        }

        public static void PruneGeneratedPlayer(string executable, HashSet<string> required)
        {
            // Only newly generated standalone output is touched. Project source data stays intact.
            if (required == null || required.Count == 0) return;
            string directory = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(executable), Path.GetFileNameWithoutExtension(executable) + "_Data", "StreamingAssets", "NtcData"));
            if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("Player is missing required NTC data directory: " + directory);
            long removedBytes = 0; int removed = 0;
            foreach (string file in Directory.GetFiles(directory, "*.ntc", SearchOption.TopDirectoryOnly))
            {
                if (required.Contains(Path.GetFileName(file))) continue;
                string resolved = Path.GetFullPath(file);
                if (!resolved.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Generated player path escaped its NtcData directory.");
                removedBytes += new FileInfo(resolved).Length; File.Delete(resolved); removed++;
            }
            foreach (string name in required)
                if (!File.Exists(Path.Combine(directory,name))) throw new FileNotFoundException("Player is missing required NTC data",name);
            Debug.Log("[NTC Player Data] required=" + required.Count + " removed legacy/unused files=" + removed + " bytes=" + removedBytes);
        }
    }
}
