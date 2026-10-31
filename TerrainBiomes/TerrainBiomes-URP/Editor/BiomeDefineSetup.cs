using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;

namespace CustomTools.TerrainBiomesURP.EditorTools
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
    /// The define is kept conditional on the references actually being present, in both
    /// directions: if a consumer loses its reference while the define stands, that
    /// consumer stops compiling and cannot put itself right, since the code that would
    /// is in an assembly that no longer builds. Removing this folder takes the whole
    /// check with it, so clearing the define by hand is still the fix there -
    /// Project Settings > Player > Scripting Define Symbols.
    /// </summary>
    [InitializeOnLoad]
    static class BiomeDefineSetup
    {
        const string Define = "CUSTOMTOOLS_BIOMES";

        /// <summary>This assembly's name, as the painter has to reference it.</summary>
        const string BiomeAssembly = "CustomTools.TerrainBiomesURP";

        /// <summary>
        /// Assemblies that gain biome support, and so need the reference added. Named
        /// rather than discovered: adding a reference to every assembly in the project
        /// would be a far more invasive thing for a dropped-in folder to do.
        /// </summary>
        static readonly string[] Consumers =
        {
            "CustomTools.TerrainPaintingURP",
            "CustomTools.TerrainPaintingURP.Editor"
        };

        static BiomeDefineSetup()
        {
            // The define and the references have to agree, and the define is the half
            // that breaks things on its own: it is what makes the painter's biome code
            // compile, so with the define set and the reference missing the painter does
            // not build at all - and a tool that does not build has no Create menu entry
            // and no component, which looks nothing like a missing reference.
            //
            // So the reference is the condition, not just the first step. Every consumer
            // present must have it before the define goes on, and the define comes off
            // again if that stops being true - which is what happens when the reference
            // is reverted by hand or by version control, since an asmdef is a file in
            // the project like any other.
            bool changed = AddReferences();

            if (changed) AssetDatabase.Refresh();

            // Re-read from disk rather than trusting what AddReferences just wrote: a
            // write that failed, or one a hook reverted in between, must not leave the
            // define standing on a reference that is not there.
            if (SetDefine(AllConsumersReference())) AssetDatabase.Refresh();
        }

        /// <summary>
        /// Whether every consumer in the project references this assembly. True when
        /// none are present, since there is then nothing to be out of step with.
        /// </summary>
        static bool AllConsumersReference()
        {
            foreach (string consumer in Consumers)
            {
                string path = FindAsmdef(consumer);
                if (path == null) continue;

                if (!System.IO.File.ReadAllText(path).Contains(BiomeAssembly)) return false;
            }

            return true;
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
        /// Adds or removes CUSTOMTOOLS_BIOMES for the active build target, to match
        /// <paramref name="wanted"/>. Returns whether anything changed, so the caller
        /// only refreshes when there is a reason to.
        ///
        /// Removal is the case that matters: leaving the define set while a consumer has
        /// lost its reference is what stops that consumer compiling, and it cannot fix
        /// itself from there, because the code that would do it lives in an assembly
        /// that no longer builds.
        /// </summary>
        static bool SetDefine(bool wanted)
        {
            var target = NamedBuildTarget.FromBuildTargetGroup(
                BuildPipeline.GetBuildTargetGroup(EditorUserBuildSettings.activeBuildTarget));

            PlayerSettings.GetScriptingDefineSymbols(target, out string[] defines);

            var current = new List<string>(defines ?? new string[0]);
            bool present = current.Contains(Define);

            if (present == wanted) return false;

            if (wanted) current.Add(Define);
            else current.RemoveAll(d => d == Define);

            PlayerSettings.SetScriptingDefineSymbols(target, current.ToArray());

            if (!wanted)
            {
                Debug.LogWarning(
                    $"Terrain Biomes removed {Define}, because an assembly that uses it no " +
                    $"longer references {BiomeAssembly}. Biome support is off until the " +
                    "reference is back. If this folder is still in the project, the " +
                    "reference was most likely reverted outside Unity.");
            }

            return true;
        }
    }
}
