using UnityEditor;
using UnityEngine;

namespace CustomTools.TerrainGeneration.EditorTools
{
    /// <summary>
    /// Creates a starting set of preset assets. Presets are generated in code rather
    /// than shipped as .asset files because an asset carries the script GUID of the
    /// project it was made in, which would not resolve in anyone else's project.
    /// </summary>
    public static class TerrainPresetLibrary
    {
        const string MenuPath = "Assets/Create/Custom Tools/Terrain Preset Library";

        [MenuItem(MenuPath, priority = 1)]
        public static void CreateStarterPresets()
        {
            string folder = ChooseFolder();
            if (string.IsNullOrEmpty(folder)) return;

            Create(folder, "Alpine Ridges", Alpine);
            Create(folder, "Tropical Island", Island);
            Create(folder, "Desert Canyon", Canyon);
            Create(folder, "Rolling Plains", Plains);
            Create(folder, "Impact Crater", Crater);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            var first = AssetDatabase.LoadAssetAtPath<TerrainMapSettings>($"{folder}/Alpine Ridges.asset");
            if (first != null)
            {
                Selection.activeObject = first;
                EditorGUIUtility.PingObject(first);
            }

            Debug.Log($"Created 5 terrain presets in {folder}.");
        }

        static string ChooseFolder()
        {
            string selected = AssetDatabase.GetAssetPath(Selection.activeObject);

            string parent = string.IsNullOrEmpty(selected) || !AssetDatabase.IsValidFolder(selected)
                ? "Assets"
                : selected;

            string folder = $"{parent}/Presets";
            if (!AssetDatabase.IsValidFolder(folder))
                AssetDatabase.CreateFolder(parent, "Presets");

            return folder;
        }

        static void Create(string folder, string name, System.Action<TerrainMapSettings> configure)
        {
            string path = $"{folder}/{name}.asset";

            // Never clobber a preset the user may have edited.
            if (AssetDatabase.LoadAssetAtPath<TerrainMapSettings>(path) != null)
            {
                Debug.Log($"Preset '{name}' already exists; left unchanged.");
                return;
            }

            var preset = ScriptableObject.CreateInstance<TerrainMapSettings>();
            configure(preset);
            AssetDatabase.CreateAsset(preset, path);
        }

        // --- the presets ---------------------------------------------------
        //
        // Values chosen to be visibly different from one another rather than subtly
        // tuned: a preset's job is to land you somewhere recognisable that you then
        // adjust, so each one should read as a distinct kind of place.

        static void Alpine(TerrainMapSettings s)
        {
            s.seed = 20261028;
            s.worldSize = 1200f;
            s.worldHeight = 600f;
            s.noise.noiseType = NoiseType.Ridged;
            s.noise.scale = 2f;
            s.noise.octaves = 6;
            s.noise.persistence = 0.5f;
            s.seaLevel = 0.12f;
            s.falloffMode = FalloffMode.None;
            s.erosion.enabled = true;
            s.erosion.iterations = 80000;
            s.erosion.erosionRadius = 3;
        }

        static void Island(TerrainMapSettings s)
        {
            s.seed = 7781;
            s.worldSize = 900f;
            s.worldHeight = 260f;
            s.noise.noiseType = NoiseType.FBM;
            s.noise.scale = 2.4f;
            s.noise.octaves = 5;
            s.seaLevel = 0.32f;
            s.falloffMode = FalloffMode.Radial;
            s.falloffStrength = 3.2f;
            s.falloffSharpness = 2.4f;
            s.erosion.enabled = true;
            s.erosion.iterations = 50000;
            s.erosion.erosionRadius = 4;
        }

        static void Canyon(TerrainMapSettings s)
        {
            s.seed = 44120;
            s.worldSize = 1000f;
            s.worldHeight = 340f;
            s.noise.noiseType = NoiseType.Billow;
            s.noise.scale = 1.8f;
            s.noise.octaves = 5;
            s.noise.persistence = 0.42f;
            s.seaLevel = 0.06f;
            s.falloffMode = FalloffMode.None;
            // Low inertia and a tight radius cut steep-walled channels rather than
            // the broad valleys the alpine preset wants.
            s.erosion.enabled = true;
            s.erosion.iterations = 90000;
            s.erosion.erosionRadius = 2;
            s.erosion.inertia = 0.02f;
            s.erosion.sedimentCapacityFactor = 6f;
        }

        static void Plains(TerrainMapSettings s)
        {
            s.seed = 1024;
            s.worldSize = 1500f;
            s.worldHeight = 120f;
            s.noise.noiseType = NoiseType.FBM;
            s.noise.scale = 1.4f;
            s.noise.octaves = 4;
            s.noise.persistence = 0.42f;
            s.seaLevel = 0.18f;
            s.falloffMode = FalloffMode.None;

            // Flat across the low two-thirds, rising at the top: occasional hills
            // standing out of otherwise level ground.
            s.heightCurve = new AnimationCurve(
                new Keyframe(0f, 0f),
                new Keyframe(0.62f, 0.18f),
                new Keyframe(1f, 1f));

            s.erosion.enabled = true;
            s.erosion.iterations = 35000;
            s.erosion.erosionRadius = 5;
        }

        static void Crater(TerrainMapSettings s)
        {
            s.seed = 99;
            s.worldSize = 800f;
            s.worldHeight = 300f;
            s.noise.noiseType = NoiseType.FBM;
            s.noise.scale = 3f;
            s.noise.octaves = 5;
            s.seaLevel = 0.1f;
            s.falloffMode = FalloffMode.Basin;
            s.falloffStrength = 3.6f;
            s.falloffSharpness = 3.2f;
            s.erosion.enabled = true;
            s.erosion.iterations = 45000;
            s.erosion.erosionRadius = 3;
        }
    }
}
