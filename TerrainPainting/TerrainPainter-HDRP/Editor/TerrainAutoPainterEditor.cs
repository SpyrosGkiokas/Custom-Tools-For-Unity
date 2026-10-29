using UnityEditor;
using UnityEngine;

namespace CustomTools.TerrainPainting.EditorTools
{
    /// <summary>
    /// Inspector for the auto painter: a live preview of the painted result and of each
    /// input channel, so rules can be dialled in before anything is written to the
    /// terrain.
    /// </summary>
    [CustomEditor(typeof(TerrainAutoPainter))]
    public class TerrainAutoPainterEditor : Editor
    {
        TerrainAutoPainter _painter;

        TerrainAnalysis _analysis;
        float[,,] _splat;
        Texture2D _preview;

        PreviewChannel _channel = PreviewChannel.Layers;
        int _soloLayer;
        bool _autoRefresh = true;
        bool _dirty = true;

        // Debounce state, as in the generator's Inspector: a slider drag fires a change
        // per mouse move, and each one would re-analyse the terrain and rebuild the
        // splatmap. Flow accumulation sorts every cell, so that is not cheap.
        bool _wasDragging;
        bool _rebuildImmediately;
        double _settledAt = -1d;

        /// <summary>
        /// Idle wait for edits with no release to detect, such as typing into a float
        /// field. A drag does not wait this out: releasing the mouse is already an
        /// explicit "this is the value I want".
        /// </summary>
        const double IdleDebounceSeconds = 1d;

        /// <summary>
        /// Analysis resolution for the preview. The panel is around 500px, and flow
        /// accumulation costs O(n² log n) in the sort, so previewing at a 4097²
        /// heightmap's full resolution would stall the Inspector for no visible gain.
        /// </summary>
        const int PreviewResolution = 257;

        void OnEnable()
        {
            _painter = (TerrainAutoPainter)target;
            _dirty = true;
        }

        void OnDisable()
        {
            // Unity does not collect textures created from script; leaking one per
            // selection shows up as climbing memory while editing.
            if (_preview != null) DestroyImmediate(_preview);
            _preview = null;
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            EditorGUI.BeginChangeCheck();
            DrawSettingsFields();
            if (EditorGUI.EndChangeCheck())
            {
                serializedObject.ApplyModifiedProperties();
                _dirty = true;
            }

            // The rules live on the settings asset, so changes there have to be noticed
            // here too: the preview is the main reason to open this Inspector at all.
            DrawEmbeddedRules();

            EditorGUILayout.Space();
            DrawPreviewSection();

            EditorGUILayout.Space();
            DrawPaintButton();
        }

        void DrawSettingsFields()
        {
            EditorGUILayout.PropertyField(serializedObject.FindProperty("settings"));

            var terrain = _painter.Terrain;
            if (terrain == null || terrain.terrainData == null)
            {
                EditorGUILayout.HelpBox(
                    "This component needs a Terrain with TerrainData to paint.",
                    MessageType.Warning);
                return;
            }

            var data = terrain.terrainData;
            var size = data.size;

            EditorGUILayout.LabelField(
                "Terrain",
                $"{size.x:0}×{size.z:0}m, {size.y:0}m tall, {data.heightmapResolution}px heightmap",
                EditorStyles.miniLabel);
        }

        /// <summary>
        /// Draws the settings asset's rules inline. Editing them on the asset's own
        /// Inspector would mean losing sight of the preview, which is where the effect
        /// of a rule change is actually visible.
        /// </summary>
        void DrawEmbeddedRules()
        {
            if (_painter.settings == null)
            {
                EditorGUILayout.HelpBox(
                    "Assign an Auto Paint Settings asset, or create one via " +
                    "Assets > Create > Custom Tools > Terrain Auto Paint Settings.",
                    MessageType.Info);
                return;
            }

            var settingsObject = new SerializedObject(_painter.settings);
            settingsObject.Update();

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Rules", EditorStyles.boldLabel);

            EditorGUI.BeginChangeCheck();

            EditorGUILayout.PropertyField(settingsObject.FindProperty("rules"), true);

            EditorGUILayout.Space();
            EditorGUILayout.PropertyField(settingsObject.FindProperty("alphamapResolution"));
            EditorGUILayout.PropertyField(settingsObject.FindProperty("smoothing"));

            var macro = settingsObject.FindProperty("macroVariation");
            EditorGUILayout.PropertyField(macro, new GUIContent("Macro Variation"));

            if (macro.boolValue)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(settingsObject.FindProperty("macroSize"), new GUIContent("Region Size (m)"));
                EditorGUILayout.PropertyField(settingsObject.FindProperty("macroStrength"), new GUIContent("Amount"));
                EditorGUI.indentLevel--;
            }

            if (EditorGUI.EndChangeCheck())
            {
                settingsObject.ApplyModifiedProperties();
                _dirty = true;
            }
        }

        // --- preview -------------------------------------------------------

        /// <summary>
        /// Marks the preview stale from a click rather than an edit: a button or a
        /// toolbar choice is already deliberate, so it skips the idle wait.
        /// </summary>
        void RebuildNow()
        {
            _dirty = true;
            _wasDragging = false;
            _settledAt = -1d;
            _rebuildImmediately = true;
        }

        /// <summary>
        /// True once a pending change has settled. A rebuild is held off entirely while
        /// a control is being dragged, then runs the moment the mouse is released.
        /// </summary>
        bool ReadyToRebuild()
        {
            if (_rebuildImmediately)
            {
                _rebuildImmediately = false;
                return true;
            }

            // A first build has nothing to debounce: without it the panel is empty.
            if (_analysis == null) return true;

            if (GUIUtility.hotControl != 0)
            {
                _wasDragging = true;
                Repaint();
                return false;
            }

            if (_wasDragging)
            {
                _wasDragging = false;
                _settledAt = -1d;
                return true;
            }

            double now = EditorApplication.timeSinceStartup;
            if (_settledAt < 0d) _settledAt = now;

            if (now - _settledAt < IdleDebounceSeconds)
            {
                Repaint();
                return false;
            }

            _settledAt = -1d;
            return true;
        }

        void DrawPreviewSection()
        {
            EditorGUILayout.LabelField("Preview", EditorStyles.boldLabel);

            DrawPreviewToolbar();

            bool mayRebuild = Event.current.type == EventType.Layout && ReadyToRebuild();

            if (_dirty && mayRebuild && (_autoRefresh || _analysis == null))
                RebuildPreview();

            DrawPreviewTexture();
            DrawChannelLegend();
        }

        void DrawPreviewToolbar()
        {
            var active = _painter.ActiveRules();

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUI.BeginChangeCheck();
                _channel = (PreviewChannel)GUILayout.Toolbar(
                    (int)_channel,
                    new[] { "Layers", "Slope", "Curve", "Flow", "Solo" },
                    GUILayout.Height(22));
                if (EditorGUI.EndChangeCheck()) RebuildNow();
            }

            // Solo needs to know which layer, and the choice is meaningless otherwise.
            if (_channel == PreviewChannel.SoloLayer && active.Count > 0)
            {
                var names = new string[active.Count];
                for (int i = 0; i < active.Count; i++)
                    names[i] = string.IsNullOrEmpty(active[i].name) ? $"Layer {i}" : active[i].name;

                EditorGUI.BeginChangeCheck();
                _soloLayer = EditorGUILayout.Popup("Show layer", Mathf.Clamp(_soloLayer, 0, active.Count - 1), names);
                if (EditorGUI.EndChangeCheck()) RebuildNow();
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                _autoRefresh = EditorGUILayout.ToggleLeft("Auto refresh", _autoRefresh, GUILayout.Width(110));
                if (GUILayout.Button("Refresh")) RebuildNow();
            }
        }

        void DrawPreviewTexture()
        {
            if (_preview == null)
            {
                EditorGUILayout.HelpBox("No preview yet.", MessageType.None);
                return;
            }

            float size = Mathf.Min(EditorGUIUtility.currentViewWidth - 40f, 512f);
            var rect = GUILayoutUtility.GetRect(size, size, GUILayout.ExpandWidth(false));
            EditorGUI.DrawPreviewTexture(rect, _preview, null, ScaleMode.ScaleToFit);
        }

        /// <summary>
        /// Says what the colours mean. Each channel's mapping is arbitrary, so without
        /// this the slope and curvature views are just abstract colour fields.
        /// </summary>
        void DrawChannelLegend()
        {
            string legend = _channel switch
            {
                PreviewChannel.Slope => "Dark is flat, bright is steep (up to 70°).",
                PreviewChannel.Curvature => "Blue is valleys and hollows, orange is ridges and crests, grey is flat.",
                PreviewChannel.Flow => "Blue is where water drains and collects; brown sheds it.",
                PreviewChannel.SoloLayer => "One layer's weight alone. Faded areas are partial coverage at a band edge.",
                _ => "Each rule in its own colour, blended by weight. Colours identify layers, not textures."
            };

            EditorGUILayout.LabelField(legend, EditorStyles.miniLabel);

            if (_analysis != null)
            {
                EditorGUILayout.LabelField(
                    $"Analysed at {_analysis.Resolution}px; painting uses the terrain's full heightmap.",
                    EditorStyles.miniLabel);
            }
        }

        /// <summary>Macro size when the setting is on, 0 when it is off or unassigned.</summary>
        float MacroSize() =>
            _painter.settings != null && _painter.settings.macroVariation
                ? _painter.settings.macroSize
                : 0f;

        /// <summary>Macro strength when the setting is on, 0 when it is off or unassigned.</summary>
        float MacroStrength() =>
            _painter.settings != null && _painter.settings.macroVariation
                ? _painter.settings.macroStrength
                : 0f;

        void RebuildPreview()
        {
            _dirty = false;

            _analysis = _painter.Analyse(PreviewResolution);
            if (_analysis == null)
            {
                _splat = null;
                return;
            }

            var active = _painter.ActiveRules();
            _splat = active.Count > 0
                ? SplatmapPainter.Build(
                    _analysis,
                    active.ToArray(),
                    PreviewResolution,
                    _painter.settings != null ? _painter.settings.smoothing : 0,
                    MacroSize(),
                    MacroStrength())
                : null;

            _preview = PaintPreview.Render(
                _analysis, _splat, _channel, _soloLayer, PreviewResolution, _preview);
        }

        // --- paint ---------------------------------------------------------

        void DrawPaintButton()
        {
            var active = _painter.ActiveRules();

            int missing = 0;
            foreach (var rule in active)
                if (rule.layer == null) missing++;

            if (missing > 0)
            {
                EditorGUILayout.HelpBox(
                    missing == 1
                        ? "One enabled rule has no Terrain Layer assigned."
                        : $"{missing} enabled rules have no Terrain Layer assigned.",
                    MessageType.Warning);
            }

            using (new EditorGUI.DisabledScope(active.Count == 0 || missing > 0))
            {
                if (GUILayout.Button("Paint Terrain", GUILayout.Height(28)))
                    PaintTerrain();
            }

            EditorGUILayout.LabelField(
                "Painting replaces the terrain's layers and splatmap.",
                EditorStyles.miniLabel);
        }

        void PaintTerrain()
        {
            var data = _painter.Terrain != null ? _painter.Terrain.terrainData : null;
            if (data == null) return;

            // TerrainData is an asset: without this the paint cannot be undone, and
            // overwriting someone's hand-painted splatmap with no way back is the one
            // genuinely destructive thing this tool does.
            Undo.RegisterCompleteObjectUndo(data, "Paint Terrain");

            if (_painter.Paint(out string error))
            {
                EditorUtility.SetDirty(data);
                Debug.Log($"Auto Painter: painted {_painter.ActiveRules().Count} layers onto {_painter.Terrain.name}.", _painter);
            }
            else
            {
                EditorUtility.DisplayDialog("Could not paint", error, "OK");
            }
        }
    }
}
