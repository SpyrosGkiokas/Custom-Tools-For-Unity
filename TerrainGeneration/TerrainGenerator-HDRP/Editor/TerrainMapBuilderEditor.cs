using System.IO;
using UnityEditor;
using UnityEngine;

namespace CustomTools.TerrainGeneration.EditorTools
{
    /// <summary>
    /// Inspector for the scene builder. Generates in place and can save the
    /// resulting TerrainData as a project asset so it survives editor reloads.
    /// </summary>
    [CustomEditor(typeof(TerrainMapBuilder))]
    public class TerrainMapBuilderEditor : Editor
    {
        TerrainMapBuilder _builder;

        void OnEnable() => _builder = (TerrainMapBuilder)target;

        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            EditorGUILayout.Space();

            if (_builder.settings == null)
            {
                EditorGUILayout.HelpBox(
                    "Assign a Terrain Map Settings asset to generate. " +
                    "Create one via Assets > Create > Custom Tools > Terrain Map Settings.",
                    MessageType.Warning);
                return;
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Generate", GUILayout.Height(30)))
                {
                    Undo.RegisterFullObjectHierarchyUndo(_builder.gameObject, "Generate Terrain");
                    BuildWithProgress();
                    MarkSceneDirty();
                }

                if (GUILayout.Button("New Seed + Generate", GUILayout.Height(30)))
                {
                    Undo.RecordObject(_builder.settings, "New Seed");
                    _builder.settings.seed = Random.Range(int.MinValue, int.MaxValue);
                    EditorUtility.SetDirty(_builder.settings);

                    Undo.RegisterFullObjectHierarchyUndo(_builder.gameObject, "Generate Terrain");
                    BuildWithProgress();
                    MarkSceneDirty();
                }
            }

            if (GUILayout.Button("Select Settings Asset"))
                Selection.activeObject = _builder.settings;

            EditorGUILayout.Space();
            DrawSaveSection();
        }

        /// <summary>
        /// Builds with a cancellable progress bar. Erosion at high droplet counts takes
        /// long enough that a frozen editor would look like a hang.
        /// </summary>
        void BuildWithProgress()
        {
            bool cancelled = false;

            _builder.ProgressHandler = progress =>
            {
                // The bar is only worth showing for the erosion pass; everything else
                // finishes before a single repaint.
                cancelled = EditorUtility.DisplayCancelableProgressBar(
                    "Generating Terrain",
                    $"Eroding… {progress * 100f:F0}%",
                    progress);
                return !cancelled;
            };

            try
            {
                _builder.Build();
            }
            finally
            {
                // Leaving the bar up would lock the editor behind an invisible modal.
                EditorUtility.ClearProgressBar();
                _builder.ProgressHandler = null;
            }

            if (cancelled)
                Debug.Log("Terrain generation cancelled; the terrain shows partial erosion.", _builder);
        }

        void DrawSaveSection()
        {
            var terrain = _builder.targetTerrain;
            if (terrain == null || terrain.terrainData == null) return;

            bool isSavedAsset = AssetDatabase.Contains(terrain.terrainData);

            if (isSavedAsset)
            {
                EditorGUILayout.HelpBox(
                    "TerrainData is saved at " + AssetDatabase.GetAssetPath(terrain.terrainData),
                    MessageType.Info);
                return;
            }

            // Generated TerrainData lives only in memory; a scene reload or
            // script recompile would drop it and leave a blank terrain behind.
            EditorGUILayout.HelpBox(
                "This terrain's data only exists in memory and will be lost when the editor reloads. " +
                "Save it as an asset to keep it.",
                MessageType.Warning);

            if (GUILayout.Button("Save TerrainData As Asset..."))
                SaveTerrainData(terrain);
        }

        void SaveTerrainData(Terrain terrain)
        {
            string path = EditorUtility.SaveFilePanelInProject(
                "Save TerrainData",
                $"{_builder.settings.name}_Data",
                "asset",
                "Choose where to store the generated terrain data.");

            if (string.IsNullOrEmpty(path)) return;

            AssetDatabase.CreateAsset(terrain.terrainData, path);
            AssetDatabase.SaveAssets();

            // Re-point the collider: CreateAsset can replace the instance it is given.
            terrain.terrainData = AssetDatabase.LoadAssetAtPath<TerrainData>(path);
            var collider = terrain.GetComponent<TerrainCollider>();
            if (collider != null) collider.terrainData = terrain.terrainData;

            MarkSceneDirty();
            Debug.Log($"Saved TerrainData to {path}", terrain.terrainData);
        }

        void MarkSceneDirty()
        {
            if (!Application.isPlaying)
                UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(
                    _builder.gameObject.scene);
        }
    }
}
