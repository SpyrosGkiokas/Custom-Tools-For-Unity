using UnityEditor;
using UnityEngine;

namespace CustomTools.TerrainGeneration.EditorTools
{
    /// <summary>
    /// Inspector for a settings asset: shows a live map preview that refreshes as
    /// values change, so the terrain can be dialled in without touching the scene.
    /// </summary>
    [CustomEditor(typeof(TerrainMapSettings))]
    public class TerrainMapSettingsEditor : Editor
    {
        TerrainMapSettings _settings;
        Texture2D _preview;
        bool _autoRefresh = true;
        bool _dirty = true;

        /// <summary>Previews stay small so dragging a slider still feels immediate.</summary>
        const int PreviewResolution = 257;

        void OnEnable()
        {
            _settings = (TerrainMapSettings)target;
            _dirty = true;
        }

        void OnDisable()
        {
            // Textures created with `new` are not garbage collected by Unity.
            if (_preview != null) DestroyImmediate(_preview);
            _preview = null;
        }

        public override void OnInspectorGUI()
        {
            EditorGUI.BeginChangeCheck();
            DrawDefaultInspector();
            if (EditorGUI.EndChangeCheck()) _dirty = true;

            EditorGUILayout.Space();
            DrawPreviewSection();
        }

        void DrawPreviewSection()
        {
            EditorGUILayout.LabelField("Preview", EditorStyles.boldLabel);

            using (new EditorGUILayout.HorizontalScope())
            {
                _autoRefresh = EditorGUILayout.ToggleLeft("Auto refresh", _autoRefresh, GUILayout.Width(110));

                if (GUILayout.Button("Refresh")) _dirty = true;

                if (GUILayout.Button("New Seed"))
                {
                    Undo.RecordObject(_settings, "New Seed");
                    _settings.seed = Random.Range(int.MinValue, int.MaxValue);
                    EditorUtility.SetDirty(_settings);
                    _dirty = true;
                }
            }

            if (_dirty && (_autoRefresh || _preview == null))
                RebuildPreview();

            if (_preview != null)
            {
                // Square, full inspector width, so the aspect always matches the terrain.
                float size = Mathf.Min(EditorGUIUtility.currentViewWidth - 40f, 512f);
                var rect = GUILayoutUtility.GetRect(size, size, GUILayout.ExpandWidth(false));
                EditorGUI.DrawPreviewTexture(rect, _preview, null, ScaleMode.ScaleToFit);
            }

            EditorGUILayout.HelpBox(
                $"Preview renders at {PreviewResolution}px. The built terrain uses {_settings.Resolution}px, " +
                "so the real thing carries more fine detail than you see here.",
                MessageType.None);

            EditorGUILayout.Space();
            if (GUILayout.Button("Create Terrain In Scene", GUILayout.Height(28)))
                CreateTerrainInScene();
        }

        void RebuildPreview()
        {
            _dirty = false;

            // Preview at a fixed low resolution regardless of the asset's setting.
            var original = _settings.resolution;
            _settings.resolution = TerrainMapSettings.TerrainResolution._257;

            try
            {
                var heights = TerrainMapGenerator.GenerateHeights(_settings);
                if (_preview != null) DestroyImmediate(_preview);
                _preview = TerrainMapGenerator.GeneratePreview(_settings, heights);
            }
            finally
            {
                // Restore even if generation throws, or the asset keeps the preview resolution.
                _settings.resolution = original;
            }
        }

        void CreateTerrainInScene()
        {
            var go = new GameObject($"Terrain Builder ({_settings.name})");
            var builder = go.AddComponent<TerrainMapBuilder>();
            builder.settings = _settings;

            Undo.RegisterCreatedObjectUndo(go, "Create Terrain Builder");
            Selection.activeGameObject = go;

            builder.Build();
            EditorGUIUtility.PingObject(go);
        }
    }
}
