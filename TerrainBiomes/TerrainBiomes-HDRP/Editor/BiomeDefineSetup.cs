using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;

namespace CustomTools.TerrainBiomes.EditorTools
{
    /// <summary>
    /// Connects the other tools to this one when this folder is present, by adding an
    /// assembly reference to them and defining CUSTOMTOOLS_BIOMES.
    ///
    /// Doing it from here rather than shipping the reference in the painter's own asmdef
    /// is what keeps each tool independently copyable, which is the premise of this repo.
    /// An asmdef reference is a hard requirement: a painter that listed this assembly
    /// would fail to compile for anyone who copied only the painter. Unity's
    /// versionDefines solve exactly this problem but key off packages, and these tools
    /// are plain folders, so the wiring is done here instead - the tool that creates the
    /// dependency is the one that declares it.
    ///
    /// Removing this folder leaves both edits behind, and the painter then fails to
    /// compile on a missing BiomeMap. The fix is in the warning that case logs.
    /// </summary>
    [InitializeOnLoad]
    static class BiomeDefineSetup
    {
        const string Define = "CUSTOMTOOLS_BIOMES";

        /// <summary>This assembly's name, as the painter has to reference it.</summary>
        const string BiomeAssembly = "CustomTools.TerrainBiomes";

        /// <summary>
        /// Assemblies that gain biome support, and so need the reference added. Named
        /// rather than discovered: adding a reference to every assembly in the project
        /// would be a far more invasive thing for a dropped-in folder to do.
        /// </summary>
        static readonly string[] Consumers =
        {
            "CustomTools.TerrainPainting",
            "CustomTools.TerrainPainting.Editor"
        };

        static BiomeDefineSetup()
        {
            // References first: the define is what makes the painter's biome code
            // compile, so setting it before the reference exists would mean one failed
            // compile before the next load fixes it.
            bool changed = AddReferences();

            if (EnsureDefine()) changed = true;

            // Both edits trigger their own recompile. Each is guarded by a check for
            // work actually needed, so a steady state does nothing and this does not
            // become a reload loop.
            if (changed) AssetDatabase.Refresh();
        }

        /// <summary>
        /// Adds this assembly to each consumer's asmdef, if it is in the project and
        /// does not already reference it. Returns whether anything was written.
        /// </summary>
        static bool AddReferences()
        {
            bool changed = false;

            foreach (string consumer in Consumers)
            {
                string path = FindAsmdef(consumer);
                if (path == null) continue;

                string json = System.IO.File.ReadAllText(path);

                // A plain substring check: the reference may be stored by name or by
                // GUID, and either way its presence is all that matters here.
                if (json.Contains(BiomeAssembly)) continue;

                string updated = InsertReference(json, BiomeAssembly);
                if (updated == null)
                {
                    Debug.LogWarning(
                        $"Terrain Biomes could not add its reference to {consumer}. " +
                        $"Add \"{BiomeAssembly}\" to that assembly definition's references by hand " +
                        "to enable biome support in it.");
                    continue;
                }

                System.IO.File.WriteAllText(path, updated);
                changed = true;
            }

            return changed;
        }

        /// <summary>
        /// Path to the asmdef defining <paramref name="assemblyName"/>, or null when it
        /// is not in this project - which is the normal case for a tool the user did not
        /// copy in.
        /// </summary>
        static string FindAsmdef(string assemblyName)
        {
            foreach (string guid in AssetDatabase.FindAssets("t:asmdef"))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (System.IO.Path.GetFileNameWithoutExtension(path) != assemblyName) continue;

                return path;
            }

            return null;
        }

        /// <summary>
        /// Adds a name to an asmdef's references array, editing the text rather than
        /// reparsing it so the file keeps its formatting and any fields this does not
        /// know about. Returns null when the shape is not what is expected, leaving the
        /// file untouched rather than corrupting it.
        /// </summary>
        static string InsertReference(string json, string reference)
        {
            const string key = "\"references\"";

            int keyAt = json.IndexOf(key, System.StringComparison.Ordinal);
            if (keyAt < 0) return null;

            int open = json.IndexOf('[', keyAt);
            int close = json.IndexOf(']', open + 1);
            if (open < 0 || close < 0) return null;

            string inner = json.Substring(open + 1, close - open - 1);
            string entry = $"\"{reference}\"";

            // An empty array has no trailing comma to follow, a populated one does.
            string body = string.IsNullOrWhiteSpace(inner)
                ? $"\n        {entry}\n    "
                : inner.TrimEnd() + $",\n        {entry}\n    ";

            return json.Substring(0, open + 1) + body + json.Substring(close);
        }

        /// <summary>
        /// Defines CUSTOMTOOLS_BIOMES for the active build target, if it is not already.
        /// Returns whether it was added.
        /// </summary>
        static bool EnsureDefine()
        {
            var target = NamedBuildTarget.FromBuildTargetGroup(
                BuildPipeline.GetBuildTargetGroup(EditorUserBuildSettings.activeBuildTarget));

            PlayerSettings.GetScriptingDefineSymbols(target, out string[] defines);

            if (defines != null && defines.Contains(Define)) return false;

            var updated = new List<string>(defines ?? new string[0]) { Define };
            PlayerSettings.SetScriptingDefineSymbols(target, updated.ToArray());

            return true;
        }
    }
}
