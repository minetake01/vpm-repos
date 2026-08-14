using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Minetake.FolderAlias.Editor
{
    [FilePath("ProjectSettings/FolderAliases.asset", FilePathAttribute.Location.ProjectFolder)]
    internal sealed class FolderAliasSettings : ScriptableSingleton<FolderAliasSettings>
    {
        [Serializable]
        internal sealed class Entry
        {
            [SerializeField] private string guid;
            [SerializeField] private string displayName;

            internal string Guid => guid;
            internal string DisplayName => displayName;

            internal Entry(string guid, string displayName)
            {
                this.guid = guid;
                this.displayName = displayName;
            }

            internal void SetDisplayName(string value) => displayName = value;
        }

        [SerializeField] private List<Entry> entries = new List<Entry>();
        internal bool TryGetDisplayName(string guid, out string displayName)
        {
            for (var index = 0; index < entries.Count; index++)
            {
                var entry = entries[index];
                if (!string.Equals(entry.Guid, guid, StringComparison.Ordinal)) continue;
                displayName = entry.DisplayName;
                return !string.IsNullOrWhiteSpace(displayName);
            }

            displayName = null;
            return false;
        }

        internal void SetDisplayName(string guid, string displayName)
        {
            ValidateAssetFolder(guid);
            var normalizedName = NormalizeDisplayName(displayName);
            var path = AssetDatabase.GUIDToAssetPath(guid);
            if (string.Equals(normalizedName, Path.GetFileName(path), StringComparison.Ordinal))
            {
                Remove(guid);
                return;
            }

            var index = FindIndex(guid);
            if (index >= 0)
            {
                if (string.Equals(entries[index].DisplayName, normalizedName, StringComparison.Ordinal)) return;
                entries[index].SetDisplayName(normalizedName);
            }
            else
            {
                entries.Add(new Entry(guid, normalizedName));
            }

            SaveAndRepaint();
        }

        internal void Remove(string guid)
        {
            var index = FindIndex(guid);
            if (index < 0) return;
            entries.RemoveAt(index);
            SaveAndRepaint();
        }

        internal static bool IsAssetFolderGuid(string guid)
        {
            if (string.IsNullOrEmpty(guid)) return false;
            return IsAssetFolderPath(AssetDatabase.GUIDToAssetPath(guid));
        }

        internal static bool IsAssetFolderPath(string path)
        {
            return !string.IsNullOrEmpty(path)
                   && (string.Equals(path, "Assets", StringComparison.Ordinal)
                       || path.StartsWith("Assets/", StringComparison.Ordinal))
                   && AssetDatabase.IsValidFolder(path);
        }

        private static void ValidateAssetFolder(string guid)
        {
            if (!IsAssetFolderGuid(guid))
                throw new ArgumentException("The GUID must identify a folder under Assets.", nameof(guid));
        }

        private static string NormalizeDisplayName(string displayName)
        {
            if (string.IsNullOrWhiteSpace(displayName))
                throw new ArgumentException("Display name must not be empty.", nameof(displayName));
            if (displayName.IndexOf('\n') >= 0 || displayName.IndexOf('\r') >= 0)
                throw new ArgumentException("Display name must be a single line.", nameof(displayName));
            return displayName.Trim();
        }

        private int FindIndex(string guid)
        {
            for (var index = 0; index < entries.Count; index++)
                if (string.Equals(entries[index].Guid, guid, StringComparison.Ordinal)) return index;
            return -1;
        }

        private void SaveAndRepaint()
        {
            Save(true);
            ProjectBrowserPatch.RefreshProjectBrowsers();
        }
    }
}
