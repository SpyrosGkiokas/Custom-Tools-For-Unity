using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace CustomTools.TerrainGeneration.EditorTools
{
    /// <summary>
    /// Presets are ordinary settings assets kept in a Presets folder, so saving one is
    /// just creating an asset and loading one is copying its values across. Nothing here
    /// invents a new file format, and a preset stays editable like any other asset.
    /// </summary>
    public static class TerrainPresets
    {
        /// <summary>
        /// Presets live beside the tool rather than in the user's own folders, so an
        /// install ships with a starting set and saving one puts it with the others.
        /// </summary>
        const string PresetFolderName = "Presets";

        /// <summary>Finds every preset asset that sits in a Presets folder.</summary>
        public static List<TerrainMapSettings> FindAll()
        {
            var results = new List<TerrainMapSettings>();

            foreach (var guid in AssetDatabase.FindAssets($"t:{nameof(TerrainMapSettings)}"))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);

                // Match the folder name anywhere in the path, so presets can live under
                // the tool or under the project's own structure.
                if (!path.Replace('\\', '/').Contains($"/{PresetFolderName}/")) continue;

                var asset = AssetDatabase.LoadAssetAtPath<TerrainMapSettings>(path);
                if (asset != null) results.Add(asset);
            }

            return results.OrderBy(a => a.name).ToList();
        }

        /// <summary>
        /// Copies every generation value from <paramref name="preset"/> onto
        /// <paramref name="target"/>, as one undoable step.
        /// </summary>
        public static void Apply(TerrainMapSettings preset, TerrainMapSettings target)
        {
            if (preset == null || target == null || preset == target) return;

            Undo.RecordObject(target, $"Apply Preset '{preset.name}'");

            // EditorUtility.CopySerialized would also copy the asset's name and file id,
            // so the values are copied through SerializedObject instead.
            var from = new SerializedObject(preset);
            var to = new SerializedObject(target);

            var iterator = from.GetIterator();
            bool enterChildren = true;
            while (iterator.NextVisible(enterChildren))
            {
                enterChildren = false;

                // m_Script would repoint the target at the preset's own type entry.
                if (iterator.propertyPath == "m_Script") continue;

                to.CopyFromSerializedProperty(iterator);
            }

            to.ApplyModifiedProperties();
            EditorUtility.SetDirty(target);
        }

        /// <summary>
        /// Saves <paramref name="source"/>'s values as a new preset asset, asking where
        /// to put it. Returns the created preset, or null if cancelled.
        /// </summary>
        public static TerrainMapSettings SaveAsNew(TerrainMapSettings source)
        {
            if (source == null) return null;

            string defaultDirectory = SuggestPresetFolder(source);

            string path = EditorUtility.SaveFilePanelInProject(
                "Save Terrain Preset",
                $"{source.name} Preset",
                "asset",
                "Presets in a folder named 'Presets' appear in the preset list.",
                defaultDirectory);

            if (string.IsNullOrEmpty(path)) return null;

            var preset = Object.Instantiate(source);
            preset.name = System.IO.Path.GetFileNameWithoutExtension(path);

            AssetDatabase.CreateAsset(preset, path);
            AssetDatabase.SaveAssets();

            var saved = AssetDatabase.LoadAssetAtPath<TerrainMapSettings>(path);

            if (!path.Replace('\\', '/').Contains($"/{PresetFolderName}/"))
            {
                Debug.LogWarning(
                    $"Saved '{preset.name}' outside a '{PresetFolderName}' folder, so it will not " +
                    "appear in the preset list. Move it into one to have it listed.", saved);
            }

            return saved;
        }

        /// <summary>
        /// Suggests a Presets folder next to the settings asset being saved, creating it
        /// if needed, so the save dialog opens somewhere sensible.
        /// </summary>
        static string SuggestPresetFolder(TerrainMapSettings source)
        {
            string sourcePath = AssetDatabase.GetAssetPath(source);
            if (string.IsNullOrEmpty(sourcePath)) return "Assets";

            string directory = System.IO.Path.GetDirectoryName(sourcePath)?.Replace('\\', '/');
            if (string.IsNullOrEmpty(directory)) return "Assets";

            // Already inside a Presets folder: stay there.
            if (directory.EndsWith($"/{PresetFolderName}")) return directory;

            string presetFolder = $"{directory}/{PresetFolderName}";
            if (!AssetDatabase.IsValidFolder(presetFolder))
                AssetDatabase.CreateFolder(directory, PresetFolderName);

            return presetFolder;
        }
    }
}
