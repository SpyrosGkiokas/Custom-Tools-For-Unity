using UnityEditor;
using UnityEngine;

namespace CustomTools.TerrainGeneration.EditorTools
{
    /// <summary>
    /// Inspector for a settings asset: shows a live map preview that refreshes as
    /// values change, so the terrain can be dialled in without touching the scene.
    /// Offers a flat colored map and an orbitable 3D mesh of the same heightmap.
    /// </summary>
    [CustomEditor(typeof(TerrainMapSettings))]
    public class TerrainMapSettingsEditor : Editor
    {
        enum PreviewMode { Map2D, Mesh3D }

        TerrainMapSettings _settings;

        Texture2D _preview;
        float[,] _heights;

        PreviewMode _mode = PreviewMode.Map2D;
        bool _autoRefresh = true;
        bool _dirty = true;

        // Debounce state. A slider drag fires a change per mouse move, and each one
        // would otherwise run the whole pipeline: warp, erosion, thermal, rivers,
        // strata and micro-relief, which is ~365ms at 513px and over a second at
        // 1025px. So a pending change waits for the drag to finish instead.
        bool _wasDragging;
        bool _rebuildImmediately;
        double _settledAt = -1d;

        /// <summary>
        /// Idle wait for edits that have no release to detect, such as typing into a
        /// float field, where every keystroke is its own change. A drag does not wait
        /// this out: releasing the mouse is an explicit "this is the value I want".
        /// </summary>
        const double IdleDebounceSeconds = 1d;

        // 3D preview state.
        PreviewRenderUtility _previewUtility;
        Mesh _previewMesh;
        Material _previewMaterial;
        Shader _previewShader;
        Vector2 _orbit = new Vector2(135f, 35f);
        float _zoom = 1.45f;

        /// <summary>Resolutions offered for the preview, independent of the built terrain.</summary>
        static readonly int[] PreviewResolutions = { 129, 257, 513, 1025 };
        static readonly string[] PreviewResolutionLabels = { "129", "257", "513", "1025" };

        /// <summary>
        /// Above this, a rebuild is slow enough to feel laggy while dragging a slider.
        /// </summary>
        const int HeavyPreviewResolution = 1025;

        /// <summary>
        /// Mesh density cap for the 3D preview. The panel is roughly 500x320 px, so a
        /// 257² mesh already puts more than one vertex per pixel: 513² would be 263k
        /// vertices and 524k triangles of detail the panel cannot resolve, rebuilt every
        /// time a setting changes. The 2D map still shows the full preview resolution.
        /// </summary>
        const int MaxMeshResolution = 257;

        int _previewResolution = 513;

        // Mesh buffers, reused across rebuilds rather than reallocated.
        Vector3[] _meshVertices;
        Vector2[] _meshUvs;
        Color[] _meshColors;
        int[] _meshTriangles;
        int _meshTriangleRes = -1;

        // Cached preset list: scanning the AssetDatabase is far too slow to do per frame.
        System.Collections.Generic.List<TerrainMapSettings> _presets;
        double _presetsCachedAt = -999d;
        const double PresetCacheSeconds = 2d;

        static readonly System.Collections.Generic.List<TerrainMapSettings> EmptyPresets =
            new System.Collections.Generic.List<TerrainMapSettings>();

        // Styles built once on demand. Allocating a GUIStyle per repaint churns the GC
        // while orbiting, and EditorStyles is not available during static init.
        static GUIStyle _hintStyle, _captionStyle, _axisStyle, _laneStyle;

        static GUIStyle HintStyle => _hintStyle ??= new GUIStyle(EditorStyles.miniLabel)
        {
            fontStyle = FontStyle.Italic,
            normal = { textColor = new Color(0.5f, 0.5f, 0.5f) }
        };

        static GUIStyle CaptionStyle => _captionStyle ??= new GUIStyle(EditorStyles.miniLabel)
        {
            normal = { textColor = new Color(0.62f, 0.72f, 0.80f) }
        };

        static GUIStyle AxisStyle => _axisStyle ??= new GUIStyle(EditorStyles.miniLabel)
        {
            normal = { textColor = new Color(0.6f, 0.6f, 0.6f) },
            fontSize = 9
        };

        static GUIStyle LaneStyle => _laneStyle ??= new GUIStyle(EditorStyles.miniLabel)
        {
            normal = { textColor = new Color(0.9f, 0.9f, 0.9f) },
            fontSize = 9
        };

        void OnEnable()
        {
            _settings = (TerrainMapSettings)target;
            _previewResolution = EditorPrefs.GetInt(PrefKey("res"), 513);
            _mode = (PreviewMode)EditorPrefs.GetInt(PrefKey("mode"), 0);
            _dirty = true;
        }

        void OnDisable()
        {
            // None of these are garbage collected by Unity; leaking them survives
            // domain reloads and shows up as climbing memory while editing.
            if (_preview != null) DestroyImmediate(_preview);
            _preview = null;

            if (_previewMesh != null) DestroyImmediate(_previewMesh);
            _previewMesh = null;

            if (_previewMaterial != null) DestroyImmediate(_previewMaterial);
            _previewMaterial = null;

            if (_previewShader != null) DestroyImmediate(_previewShader);
            _previewShader = null;

            if (_previewUtility != null)
            {
                _previewUtility.Cleanup();
                _previewUtility = null;
            }
        }

        static string PrefKey(string suffix) => $"CustomTools.TerrainGeneration.preview.{suffix}";

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            EditorGUI.BeginChangeCheck();

            DrawPresetBar();
            DrawSections();

            if (EditorGUI.EndChangeCheck())
            {
                serializedObject.ApplyModifiedProperties();
                _dirty = true;
            }

            EditorGUILayout.Space();
            DrawPreviewSection();
        }

        // --- preset bar ----------------------------------------------------

        void DrawPresetBar()
        {
            // FindAll scans the whole AssetDatabase, so it must not run per repaint:
            // orbiting the 3D preview repaints continuously. Refreshed on Layout only,
            // and at most a few times a second.
            if (Event.current.type == EventType.Layout &&
                EditorApplication.timeSinceStartup - _presetsCachedAt > PresetCacheSeconds)
            {
                _presets = TerrainPresets.FindAll();
                _presetsCachedAt = EditorApplication.timeSinceStartup;
            }

            var presets = _presets ?? EmptyPresets;

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("Preset", GUILayout.Width(44f));

                if (presets.Count == 0)
                {
                    using (new EditorGUI.DisabledScope(true))
                        EditorGUILayout.LabelField("none found", EditorStyles.miniLabel);
                }
                else
                {
                    // Index 0 is a prompt rather than a preset: applying one is an action,
                    // not a state, so the popup should not claim a current selection.
                    var names = new string[presets.Count + 1];
                    names[0] = "Load a preset…";
                    for (int i = 0; i < presets.Count; i++) names[i + 1] = presets[i].name;

                    int picked = EditorGUILayout.Popup(0, names);
                    if (picked > 0)
                    {
                        TerrainPresets.Apply(presets[picked - 1], _settings);
                        serializedObject.Update();
                        RebuildNow();
                    }
                }

                if (GUILayout.Button("Save As…", GUILayout.Width(74f)))
                {
                    var saved = TerrainPresets.SaveAsNew(_settings);
                    if (saved != null) EditorGUIUtility.PingObject(saved);
                }
            }

            EditorGUILayout.Space(2f);
        }

        // --- sections ------------------------------------------------------

        void DrawSections()
        {
            DrawShapeSection();
            DrawErosionSection();
            DrawThermalSection();
            DrawDetailSection();
            DrawEdgesSection();
            DrawWorldSection();
            DrawLayersSection();
        }

        void DrawThermalSection()
        {
            var thermal = serializedObject.FindProperty("thermal");
            var enabled = thermal.FindPropertyRelative("enabled");
            var iterations = thermal.FindPropertyRelative("iterations");
            var angle = thermal.FindPropertyRelative("talusAngle");

            string summary = enabled.boolValue ? $"on · {angle.floatValue:0}°" : "off";

            if (!InspectorSections.Foldout("thermal", "Thermal Erosion", summary, true)) return;

            using (new EditorGUI.IndentLevelScope())
            {
                EditorGUILayout.PropertyField(enabled, new GUIContent("Enabled"));

                using (new EditorGUI.DisabledScope(!enabled.boolValue))
                {
                    EditorGUILayout.PropertyField(iterations, new GUIContent("Sweeps"));

                    InspectorSections.SliderWithHint(
                        angle, "Slope Target",
                        "rounded hills", "sheer cliffs");

                    EditorGUILayout.PropertyField(thermal.FindPropertyRelative("rate"), new GUIContent("Settle Rate"));

                    EditorGUILayout.HelpBox(
                        "Slopes approach the target angle gradually rather than being clamped to it, " +
                        "so the steepest ground stays above it unless Sweeps is high. 40 softens " +
                        "cliffs noticeably; 100+ settles the terrain properly.",
                        MessageType.None);
                }
            }
        }

        void DrawDetailSection()
        {
            var detail = serializedObject.FindProperty("detail");
            var rivers = serializedObject.FindProperty("rivers");
            var strata = serializedObject.FindProperty("strata");

            var parts = new System.Collections.Generic.List<string>();
            if (detail.FindPropertyRelative("enabled").boolValue) parts.Add("detail");
            if (rivers.FindPropertyRelative("enabled").boolValue) parts.Add("rivers");
            if (strata.FindPropertyRelative("enabled").boolValue) parts.Add("strata");
            string summary = parts.Count == 0 ? "off" : string.Join(" · ", parts);

            if (!InspectorSections.Foldout("detail", "Detail & Rivers", summary, true)) return;

            using (new EditorGUI.IndentLevelScope())
            {
                // --- micro-relief
                var detailEnabled = detail.FindPropertyRelative("enabled");
                EditorGUILayout.LabelField("Micro-relief", EditorStyles.miniBoldLabel);
                EditorGUILayout.PropertyField(detailEnabled, new GUIContent("Enabled"));

                using (new EditorGUI.DisabledScope(!detailEnabled.boolValue))
                {
                    EditorGUILayout.PropertyField(detail.FindPropertyRelative("amplitude"), new GUIContent("Amount"));
                    EditorGUILayout.PropertyField(detail.FindPropertyRelative("detailScale"), new GUIContent("Grain Size"));

                    InspectorSections.SliderWithHint(
                        detail.FindPropertyRelative("flatRoughness"), "Flat Roughness",
                        "smooth flats", "uniform detail");

                    EditorGUILayout.PropertyField(detail.FindPropertyRelative("wetSmoothing"), new GUIContent("Water Smoothing"));
                    EditorGUILayout.PropertyField(detail.FindPropertyRelative("steepAngle"), new GUIContent("Steep Angle"));
                }

                EditorGUILayout.Space(4f);

                // --- rivers
                var riversEnabled = rivers.FindPropertyRelative("enabled");
                EditorGUILayout.LabelField("River Channels", EditorStyles.miniBoldLabel);
                EditorGUILayout.PropertyField(riversEnabled, new GUIContent("Enabled"));

                using (new EditorGUI.DisabledScope(!riversEnabled.boolValue))
                {
                    EditorGUILayout.PropertyField(rivers.FindPropertyRelative("depth"), new GUIContent("Depth"));
                    EditorGUILayout.PropertyField(rivers.FindPropertyRelative("width"), new GUIContent("Width"));

                    InspectorSections.SliderWithHint(
                        rivers.FindPropertyRelative("threshold"), "Min Flow",
                        "every trickle", "main rivers only");
                }

                var erosionOn = serializedObject.FindProperty("erosion").FindPropertyRelative("enabled");
                if (riversEnabled.boolValue && !erosionOn.boolValue)
                {
                    // River carving reads the flow map, which only erosion produces.
                    EditorGUILayout.HelpBox(
                        "River channels need Erosion enabled — the water flow they follow comes from it. " +
                        "With erosion off this stage does nothing.",
                        MessageType.Warning);
                }

                EditorGUILayout.Space(4f);

                // --- strata
                var strataEnabled = strata.FindPropertyRelative("enabled");
                EditorGUILayout.LabelField("Strata", EditorStyles.miniBoldLabel);
                EditorGUILayout.PropertyField(strataEnabled, new GUIContent("Enabled"));

                using (new EditorGUI.DisabledScope(!strataEnabled.boolValue))
                {
                    EditorGUILayout.PropertyField(strata.FindPropertyRelative("strength"), new GUIContent("Strength"));
                    EditorGUILayout.PropertyField(strata.FindPropertyRelative("bands"), new GUIContent("Layers"));
                    EditorGUILayout.PropertyField(strata.FindPropertyRelative("sharpness"), new GUIContent("Edge Hardness"));
                }
            }
        }

        void DrawShapeSection()
        {
            var noise = serializedObject.FindProperty("noise");
            var noiseType = noise.FindPropertyRelative("noiseType");

            if (!InspectorSections.Foldout("shape", "Shape", noiseType.enumDisplayNames[noiseType.enumValueIndex], true))
                return;

            using (new EditorGUI.IndentLevelScope())
            {
                EditorGUILayout.PropertyField(noiseType, new GUIContent("Noise Type"));

                InspectorSections.SliderWithHint(
                    noise.FindPropertyRelative("scale"), "Feature Size",
                    "larger features", "finer detail");

                EditorGUILayout.PropertyField(
                    noise.FindPropertyRelative("octaves"), new GUIContent("Detail Levels"));

                InspectorSections.SliderWithHint(
                    noise.FindPropertyRelative("warpStrength"), "Warp",
                    "axis-aligned", "meandering");

                EditorGUILayout.PropertyField(
                    noise.FindPropertyRelative("warpScale"), new GUIContent("Warp Size"));

                EditorGUILayout.PropertyField(
                    serializedObject.FindProperty("heightCurve"), new GUIContent("Height Curve"));

                DrawCurveStrip();

                EditorGUILayout.PropertyField(
                    serializedObject.FindProperty("normalizeHeights"), new GUIContent("Normalize Heights"));

                if (InspectorSections.Advanced("shape", "persistence, lacunarity, offset"))
                {
                    using (new EditorGUI.IndentLevelScope())
                    {
                        EditorGUILayout.PropertyField(noise.FindPropertyRelative("persistence"));
                        EditorGUILayout.PropertyField(noise.FindPropertyRelative("lacunarity"));
                        EditorGUILayout.PropertyField(noise.FindPropertyRelative("offset"));
                    }
                }
            }
        }

        void DrawErosionSection()
        {
            var erosion = serializedObject.FindProperty("erosion");
            var enabled = erosion.FindPropertyRelative("enabled");
            var iterations = erosion.FindPropertyRelative("iterations");

            string summary = enabled.boolValue
                ? $"on · {FormatCount(iterations.intValue)}"
                : "off";

            if (!InspectorSections.Foldout("erosion", "Erosion", summary, true)) return;

            using (new EditorGUI.IndentLevelScope())
            {
                EditorGUILayout.PropertyField(enabled, new GUIContent("Enabled"));

                using (new EditorGUI.DisabledScope(!enabled.boolValue))
                {
                    EditorGUILayout.PropertyField(iterations, new GUIContent("Strength"));

                    var hintRect = InspectorSections.ValueColumn(
                        EditorGUILayout.GetControlRect(false, 13f));
                    GUI.Label(hintRect, $"droplets simulated · about {EstimateErosionMs(iterations.intValue)} ms", HintStyle);

                    InspectorSections.SliderWithHint(
                        erosion.FindPropertyRelative("erosionRadius"), "Valley Width",
                        "sharp gullies", "broad valleys");

                    EditorGUILayout.PropertyField(
                        erosion.FindPropertyRelative("sedimentCapacityFactor"), new GUIContent("Carve Depth"));

                    if (InspectorSections.Advanced("erosion", "9 physics dials"))
                    {
                        using (new EditorGUI.IndentLevelScope())
                        {
                            EditorGUILayout.PropertyField(erosion.FindPropertyRelative("maxDropletLifetime"));
                            EditorGUILayout.PropertyField(erosion.FindPropertyRelative("inertia"));
                            EditorGUILayout.PropertyField(erosion.FindPropertyRelative("minSedimentCapacity"));
                            EditorGUILayout.PropertyField(erosion.FindPropertyRelative("erodeSpeed"));
                            EditorGUILayout.PropertyField(erosion.FindPropertyRelative("depositSpeed"));
                            EditorGUILayout.PropertyField(erosion.FindPropertyRelative("evaporateSpeed"));
                            EditorGUILayout.PropertyField(erosion.FindPropertyRelative("gravity"));
                            EditorGUILayout.PropertyField(erosion.FindPropertyRelative("initialSpeed"));
                            EditorGUILayout.PropertyField(erosion.FindPropertyRelative("initialWater"));
                        }
                    }
                }
            }
        }

        void DrawEdgesSection()
        {
            var falloffMode = serializedObject.FindProperty("falloffMode");
            var seaLevel = serializedObject.FindProperty("seaLevel");

            string summary = falloffMode.enumValueIndex == 0
                ? "no falloff"
                : falloffMode.enumDisplayNames[falloffMode.enumValueIndex].ToLowerInvariant();

            if (!InspectorSections.Foldout("edges", "Edges & Water", summary, true)) return;

            using (new EditorGUI.IndentLevelScope())
            {
                EditorGUILayout.PropertyField(falloffMode, new GUIContent("Edge Shape"));

                using (new EditorGUI.DisabledScope(falloffMode.enumValueIndex == 0))
                {
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("falloffStrength"), new GUIContent("Edge Reach"));
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("falloffSharpness"), new GUIContent("Edge Hardness"));
                }

                EditorGUILayout.PropertyField(seaLevel, new GUIContent("Sea Level"));

                // Sea Level is the control people most often expect to create water.
                var worldHeight = serializedObject.FindProperty("worldHeight").floatValue;
                EditorGUILayout.HelpBox(
                    $"Shades the preview and gives layer rules a reference. It does not create water — " +
                    $"put your own water surface at Y = {worldHeight * seaLevel.floatValue:0.#}.",
                    MessageType.None);
            }
        }

        void DrawWorldSection()
        {
            var resolution = serializedObject.FindProperty("resolution");
            var worldSize = serializedObject.FindProperty("worldSize");

            if (!InspectorSections.Foldout("world", "World & Resolution",
                    $"{worldSize.floatValue:0}m · {_settings.Resolution}")) return;

            using (new EditorGUI.IndentLevelScope())
            {
                EditorGUILayout.PropertyField(serializedObject.FindProperty("seed"));
                EditorGUILayout.PropertyField(worldSize, new GUIContent("World Size (m)"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("worldHeight"), new GUIContent("Max Height (m)"));
                EditorGUILayout.PropertyField(resolution);

                if (_settings.Resolution >= 2049)
                {
                    EditorGUILayout.HelpBox(
                        "2049 is memory-hungry and slow to generate. 513 or 1025 suits most terrain.",
                        MessageType.Warning);
                }
            }
        }

        void DrawLayersSection()
        {
            var layerRules = serializedObject.FindProperty("layerRules");
            int count = layerRules.arraySize;

            string summary = count == 0 ? "placeholder" : $"{count} rule{(count == 1 ? "" : "s")}";

            if (!InspectorSections.Foldout("layers", "Terrain Layers", summary)) return;

            using (new EditorGUI.IndentLevelScope())
            {
                EditorGUILayout.PropertyField(layerRules, new GUIContent("Layer Rules"), true);

                if (count == 0)
                {
                    EditorGUILayout.HelpBox(
                        "With no rules the terrain shows a grey checker. Add rules with a TerrainLayer " +
                        "each to texture it by height and slope.",
                        MessageType.Info);
                }
                else
                {
                    DrawLayerBandStrip();
                }
            }
        }

        // --- inline visual feedback ----------------------------------------

        /// <summary>
        /// Draws the terrain's elevation profile along one line of the current preview,
        /// coloured by the same palette as the map. The Height Curve is the hardest
        /// control here to predict from its own graph, because what matters is the shape
        /// it produces, not the curve itself.
        /// </summary>
        void DrawCurveStrip()
        {
            if (_heights == null) return;

            var rect = InspectorSections.ValueColumn(
                EditorGUILayout.GetControlRect(false, 54f));

            if (Event.current.type != EventType.Repaint) return;

            EditorGUI.DrawRect(rect, new Color(0.13f, 0.19f, 0.24f));

            int res = _heights.GetLength(0);
            int row = res / 2;
            float sea = _settings.seaLevel;

            // One vertical bar per pixel column, coloured by elevation: cheap to draw
            // and it reads as a cross-section without needing a mesh.
            int columns = Mathf.Min((int)rect.width, res);
            for (int i = 0; i < columns; i++)
            {
                float t = columns > 1 ? (float)i / (columns - 1) : 0f;
                int sampleX = Mathf.Clamp(Mathf.RoundToInt(t * (res - 1)), 0, res - 1);
                float h = _heights[row, sampleX];

                float barHeight = Mathf.Max(1f, h * rect.height);
                var bar = new Rect(
                    rect.x + i * (rect.width / columns),
                    rect.yMax - barHeight,
                    Mathf.Ceil(rect.width / columns),
                    barHeight);

                EditorGUI.DrawRect(bar, SurfaceColor(h, sea));
            }

            // Sea level line, so the water threshold is visible against the profile.
            var seaY = rect.yMax - sea * rect.height;
            EditorGUI.DrawRect(new Rect(rect.x, seaY, rect.width, 1f), new Color(0.45f, 0.75f, 0.95f, 0.6f));

            GUI.Label(new Rect(rect.x + 3f, rect.yMax - 15f, rect.width, 14f), "cross-section", CaptionStyle);
        }

        /// <summary>
        /// Draws each layer rule as a band over the 0..1 height range, so overlaps and
        /// gaps in coverage are visible instead of having to be worked out from numbers.
        /// </summary>
        void DrawLayerBandStrip()
        {
            var rules = _settings.layerRules;
            if (rules == null || rules.Length == 0) return;

            var rect = InspectorSections.ValueColumn(
                EditorGUILayout.GetControlRect(false, 18f + rules.Length * 14f));

            if (Event.current.type != EventType.Repaint) return;

            // Axis strip showing the height range the bands are measured against.
            var axis = new Rect(rect.x, rect.y, rect.width, 12f);
            EditorGUI.DrawRect(axis, new Color(0.16f, 0.16f, 0.16f));

            float sea = _settings.seaLevel;
            EditorGUI.DrawRect(
                new Rect(axis.x, axis.y, axis.width * sea, axis.height),
                new Color(0.16f, 0.28f, 0.42f));

            GUI.Label(new Rect(axis.x + 2f, axis.y - 1f, 60f, 12f), "sea", AxisStyle);
            GUI.Label(new Rect(axis.xMax - 26f, axis.y - 1f, 26f, 12f), "peak", AxisStyle);

            for (int i = 0; i < rules.Length; i++)
            {
                var rule = rules[i];
                if (rule == null) continue;

                float y = rect.y + 14f + i * 14f;
                var lane = new Rect(rect.x, y, rect.width, 11f);
                EditorGUI.DrawRect(lane, new Color(0.19f, 0.19f, 0.19f));

                float min = Mathf.Clamp01(Mathf.Min(rule.heightRange.x, rule.heightRange.y));
                float max = Mathf.Clamp01(Mathf.Max(rule.heightRange.x, rule.heightRange.y));

                var band = new Rect(
                    lane.x + min * lane.width,
                    lane.y,
                    Mathf.Max(2f, (max - min) * lane.width),
                    lane.height);

                // Distinct hue per lane: these identify rules, they do not encode height.
                var hue = Mathf.Repeat(0.33f + i * 0.17f, 1f);
                EditorGUI.DrawRect(band, Color.HSVToRGB(hue, 0.45f, 0.72f));

                string label = string.IsNullOrEmpty(rule.name) ? $"Layer {i}" : rule.name;
                if (rule.layer == null) label += "  (no layer)";

                GUI.Label(new Rect(band.x + 3f, band.y - 1f, lane.width, 12f), label, LaneStyle);
            }
        }

        static string FormatCount(int value)
        {
            if (value >= 1000000) return $"{value / 1000000f:0.#}M";
            if (value >= 1000) return $"{value / 1000f:0.#}k";
            return value.ToString();
        }

        /// <summary>
        /// Rough wall-clock estimate so Strength reads as a time cost, not just a count.
        /// Calibrated against measured runs: ~50k droplets at 513 took about 365ms.
        /// </summary>
        int EstimateErosionMs(int iterations)
        {
            float perDroplet = 365f / 50000f;
            float resolutionFactor = Mathf.Max(1f, _settings.Resolution / 513f);
            return Mathf.Max(1, Mathf.RoundToInt(iterations * perDroplet * resolutionFactor));
        }

        /// <summary>
        /// Marks the preview stale from a click rather than an edit: a button or a
        /// toolbar choice is already a deliberate action, so it skips the idle wait.
        /// </summary>
        void RebuildNow()
        {
            _dirty = true;
            _wasDragging = false;
            _settledAt = -1d;
            _rebuildImmediately = true;
        }

        /// <summary>
        /// True once a pending change has settled. While a control is being dragged the
        /// rebuild is held off entirely, then runs the moment the mouse is released, so
        /// a slider sweep costs one generation instead of one per mouse move. Edits with
        /// no release to detect, such as typing a number, fall back to an idle timer.
        /// </summary>
        bool ReadyToRebuild()
        {
            if (_rebuildImmediately)
            {
                _rebuildImmediately = false;
                return true;
            }

            // A first build has nothing to debounce: without it the panel is empty.
            if (_heights == null) return true;

            bool dragging = GUIUtility.hotControl != 0;

            if (dragging)
            {
                _wasDragging = true;
                // Keep repainting so the release is noticed promptly even if the
                // pointer then sits still over the Inspector.
                Repaint();
                return false;
            }

            if (_wasDragging)
            {
                // Released. This is a deliberate choice of value, so build now rather
                // than making the user wait out a timer on top of the generation itself.
                _wasDragging = false;
                _settledAt = -1d;
                return true;
            }

            // Typed or stepped change: wait for a quiet period, restarting on each edit.
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

            // Only regenerate on a Layout event. Orbiting the 3D view calls Repaint
            // continuously, and generation now runs a multi-stage pipeline, so
            // rebuilding per repaint would re-run seconds of simulation mid-drag.
            bool mayRebuild = Event.current.type == EventType.Layout && ReadyToRebuild();

            if (_dirty && mayRebuild && (_autoRefresh || _heights == null))
                RebuildPreview();

            if (_mode == PreviewMode.Map2D) DrawMapPreview();
            else DrawMeshPreview();

            DrawPreviewFootnote();

            EditorGUILayout.Space();
            if (GUILayout.Button("Create Terrain In Scene", GUILayout.Height(28)))
                CreateTerrainInScene();
        }

        void DrawPreviewToolbar()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUI.BeginChangeCheck();
                _mode = (PreviewMode)GUILayout.Toolbar(
                    (int)_mode, new[] { "2D Map", "3D Mesh" }, GUILayout.Height(22));
                if (EditorGUI.EndChangeCheck())
                {
                    EditorPrefs.SetInt(PrefKey("mode"), (int)_mode);
                    // The mesh is built lazily, so switching to 3D first time needs a build.
                    if (_mode == PreviewMode.Mesh3D && _previewMesh == null) RebuildNow();
                }
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("Resolution", GUILayout.Width(70));

                EditorGUI.BeginChangeCheck();
                int index = Mathf.Max(0, System.Array.IndexOf(PreviewResolutions, _previewResolution));
                index = GUILayout.Toolbar(index, PreviewResolutionLabels);
                if (EditorGUI.EndChangeCheck())
                {
                    _previewResolution = PreviewResolutions[index];
                    EditorPrefs.SetInt(PrefKey("res"), _previewResolution);
                    RebuildNow();
                }
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                _autoRefresh = EditorGUILayout.ToggleLeft("Auto refresh", _autoRefresh, GUILayout.Width(110));

                if (GUILayout.Button("Refresh")) RebuildNow();

                if (GUILayout.Button("New Seed"))
                {
                    Undo.RecordObject(_settings, "New Seed");
                    _settings.seed = Random.Range(int.MinValue, int.MaxValue);
                    EditorUtility.SetDirty(_settings);
                    RebuildNow();
                }
            }

            if (_previewResolution >= HeavyPreviewResolution && _autoRefresh)
            {
                EditorGUILayout.HelpBox(
                    "At this resolution the preview rebuilds slowly. Turn off Auto refresh " +
                    "and use Refresh if dragging a slider feels sluggish.",
                    MessageType.Warning);
            }
        }

        void DrawMapPreview()
        {
            if (_preview == null) return;

            // Square, full inspector width, so the aspect always matches the terrain.
            float size = Mathf.Min(EditorGUIUtility.currentViewWidth - 40f, 512f);
            var rect = GUILayoutUtility.GetRect(size, size, GUILayout.ExpandWidth(false));
            EditorGUI.DrawPreviewTexture(rect, _preview, null, ScaleMode.ScaleToFit);
        }

        void DrawMeshPreview()
        {
            if (_previewMesh == null) return;

            float width = EditorGUIUtility.currentViewWidth - 40f;
            var rect = GUILayoutUtility.GetRect(width, 320f, GUILayout.ExpandWidth(true));

            HandleOrbitInput(rect);

            if (Event.current.type != EventType.Repaint)
            {
                EditorGUILayout.LabelField("Drag to orbit · scroll to zoom", EditorStyles.miniLabel);
                return;
            }

            var utility = GetPreviewUtility();
            utility.BeginPreview(rect, GUIStyle.none);

            var camera = utility.camera;
            var rotation = Quaternion.Euler(_orbit.y, _orbit.x, 0f);

            // Frame the unit-sized mesh: distance scales with zoom so the terrain
            // stays centred whatever angle it is viewed from.
            camera.transform.position = rotation * new Vector3(0f, 0f, -_zoom * 2.4f);
            camera.transform.rotation = rotation;
            camera.nearClipPlane = 0.05f;
            camera.farClipPlane = 50f;
            camera.backgroundColor = new Color(0.17f, 0.18f, 0.20f);

            utility.lights[0].intensity = 1.1f;
            utility.lights[0].transform.rotation = Quaternion.Euler(45f, 125f, 0f);
            utility.lights[1].intensity = 0.45f;

            utility.DrawMesh(_previewMesh, Matrix4x4.identity, _previewMaterial, 0);
            camera.Render();

            var texture = utility.EndPreview();
            GUI.DrawTexture(rect, texture, ScaleMode.StretchToFill, false);

            EditorGUILayout.LabelField("Drag to orbit · scroll to zoom", EditorStyles.miniLabel);
        }

        void HandleOrbitInput(Rect rect)
        {
            var e = Event.current;
            if (!rect.Contains(e.mousePosition)) return;

            if (e.type == EventType.MouseDrag && e.button == 0)
            {
                _orbit.x += e.delta.x;
                // Clamp pitch so the camera cannot flip over the top of the terrain.
                _orbit.y = Mathf.Clamp(_orbit.y + e.delta.y, 5f, 85f);
                e.Use();
                Repaint();
            }
            else if (e.type == EventType.ScrollWheel)
            {
                _zoom = Mathf.Clamp(_zoom + e.delta.y * 0.035f, 0.6f, 3f);
                e.Use();
                Repaint();
            }
        }

        PreviewRenderUtility GetPreviewUtility()
        {
            if (_previewUtility == null)
            {
                _previewUtility = new PreviewRenderUtility();
                _previewUtility.camera.fieldOfView = 32f;
                _previewUtility.camera.clearFlags = CameraClearFlags.SolidColor;
            }
            return _previewUtility;
        }

        void DrawPreviewFootnote()
        {
            string note =
                $"Preview renders at {_previewResolution}px. The built terrain uses " +
                $"{_settings.Resolution}px, so the real thing carries more fine detail.";

            if (_mode == PreviewMode.Mesh3D && _previewResolution > MaxMeshResolution)
            {
                note += $" The 3D mesh is capped at {MaxMeshResolution}px to stay responsive; " +
                        "the 2D map shows the full preview resolution.";
            }

            EditorGUILayout.HelpBox(note, MessageType.None);
        }

        void RebuildPreview()
        {
            _dirty = false;

            // Generate at the preview resolution, not the asset's, then put the
            // asset's value back so the built terrain is unaffected.
            var originalResolution = _settings.resolution;
            _settings.resolution = (TerrainMapSettings.TerrainResolution)_previewResolution;

            // Droplet counts are tuned against the real resolution. Reused unchanged at
            // a smaller preview they would over-erode, so scale by the area ratio and
            // the preview reflects what the built terrain will actually look like.
            int originalIterations = _settings.erosion != null ? _settings.erosion.iterations : 0;
            if (_settings.erosion != null && _settings.erosion.enabled)
            {
                int full = (int)originalResolution;
                float areaRatio = (float)(_previewResolution * _previewResolution) / (full * full);
                _settings.erosion.iterations = Mathf.Max(1, (int)(originalIterations * areaRatio));
            }

            try
            {
                _heights = TerrainMapGenerator.GenerateHeights(_settings, out _, null);

                if (_preview != null) DestroyImmediate(_preview);
                _preview = TerrainMapGenerator.GeneratePreview(_settings, _heights);

                if (_mode == PreviewMode.Mesh3D) RebuildMesh();
            }
            finally
            {
                // Restore even if generation throws, or the asset keeps the preview values.
                _settings.resolution = originalResolution;
                if (_settings.erosion != null) _settings.erosion.iterations = originalIterations;
            }
        }

        /// <summary>
        /// Builds a unit-sized mesh from the current heightmap, centred on the origin
        /// so the preview camera can orbit it without recentring.
        /// </summary>
        void RebuildMesh()
        {
            if (_heights == null) return;

            int source = _heights.GetLength(0);

            // Decimate rather than refuse: a 1025px preview still gets a 513px mesh.
            int step = Mathf.Max(1, Mathf.CeilToInt((float)source / MaxMeshResolution));
            int res = (source - 1) / step + 1;

            // Buffers are reused across rebuilds: at the cap this is ~100k vertices,
            // and reallocating them on every settings change churns the GC.
            if (_meshVertices == null || _meshVertices.Length != res * res)
            {
                _meshVertices = new Vector3[res * res];
                _meshUvs = new Vector2[res * res];
                _meshColors = new Color[res * res];
            }

            var vertices = _meshVertices;
            var uvs = _meshUvs;
            var colors = _meshColors;

            // Vertical exaggeration relative to the footprint, so the mesh reads the
            // way the real terrain will instead of looking flat at 1000m across.
            float heightScale = Mathf.Clamp01(_settings.worldHeight / Mathf.Max(1f, _settings.worldSize));
            float sea = _settings.seaLevel;

            for (int y = 0; y < res; y++)
            {
                for (int x = 0; x < res; x++)
                {
                    int sx = Mathf.Min(x * step, source - 1);
                    int sy = Mathf.Min(y * step, source - 1);

                    float h = _heights[sy, sx];
                    float nx = (float)x / (res - 1);
                    float ny = (float)y / (res - 1);

                    int i = y * res + x;
                    vertices[i] = new Vector3(nx - 0.5f, h * heightScale, ny - 0.5f);
                    uvs[i] = new Vector2(nx, ny);
                    colors[i] = SurfaceColor(h, sea);
                }
            }

            // The index pattern depends only on the grid size, so it survives every
            // rebuild at the same resolution — which is the common case, since changing
            // a noise or erosion setting does not change the mesh dimensions.
            bool indicesStale = _meshTriangles == null || _meshTriangleRes != res;
            if (indicesStale)
            {
                _meshTriangles = new int[(res - 1) * (res - 1) * 6];
                _meshTriangleRes = res;

                int t = 0;
                for (int y = 0; y < res - 1; y++)
                {
                    for (int x = 0; x < res - 1; x++)
                    {
                        int i = y * res + x;
                        _meshTriangles[t++] = i;
                        _meshTriangles[t++] = i + res;
                        _meshTriangles[t++] = i + 1;

                        _meshTriangles[t++] = i + 1;
                        _meshTriangles[t++] = i + res;
                        _meshTriangles[t++] = i + res + 1;
                    }
                }
            }

            // A fresh Mesh has no indices of its own, so it needs them uploaded even
            // when the cached buffer is still valid.
            bool meshNeedsIndices = indicesStale;

            if (_previewMesh == null)
            {
                _previewMesh = new Mesh { name = "Terrain Preview Mesh" };
                meshNeedsIndices = true;
            }
            else if (indicesStale)
            {
                // Clear only when the grid size changed: leftover triangles from a
                // denser mesh would index vertices that no longer exist. Clearing on
                // every rebuild would discard the index buffer needlessly.
                _previewMesh.Clear();
            }

            if (meshNeedsIndices)
            {
                // Past 65k vertices the default 16-bit indices cannot address the mesh.
                _previewMesh.indexFormat = res * res > 65000
                    ? UnityEngine.Rendering.IndexFormat.UInt32
                    : UnityEngine.Rendering.IndexFormat.UInt16;
            }

            _previewMesh.vertices = vertices;
            _previewMesh.uv = uvs;
            _previewMesh.colors = colors;

            // Vertices must be assigned before indices that reference them.
            if (meshNeedsIndices) _previewMesh.triangles = _meshTriangles;

            _previewMesh.RecalculateNormals();
            _previewMesh.RecalculateBounds();

            EnsurePreviewMaterial();
        }

        /// <summary>Elevation colouring, matching the 2D preview's palette.</summary>
        static Color SurfaceColor(float h, float sea)
        {
            if (h <= sea)
            {
                float t = sea > 0f ? h / sea : 0f;
                return Color.Lerp(new Color(0.05f, 0.12f, 0.33f), new Color(0.25f, 0.55f, 0.75f), t);
            }

            float e = Mathf.InverseLerp(sea, 1f, h);
            if (e < 0.12f) return Color.Lerp(new Color(0.80f, 0.74f, 0.52f), new Color(0.45f, 0.60f, 0.30f), e / 0.12f);
            if (e < 0.55f) return Color.Lerp(new Color(0.45f, 0.60f, 0.30f), new Color(0.32f, 0.42f, 0.24f), (e - 0.12f) / 0.43f);
            if (e < 0.78f) return Color.Lerp(new Color(0.32f, 0.42f, 0.24f), new Color(0.42f, 0.38f, 0.34f), (e - 0.55f) / 0.23f);
            return Color.Lerp(new Color(0.42f, 0.38f, 0.34f), Color.white, (e - 0.78f) / 0.22f);
        }

        void EnsurePreviewMaterial()
        {
            if (_previewMaterial != null) return;

            // Built-in shaders are unreliable here: pipeline shaders (Lit, TerrainLit)
            // do not render correctly inside PreviewRenderUtility, and the unlit ones
            // that always exist would show flat colour with no visible relief. A small
            // self-contained shader avoids depending on what the project has installed.
            _previewShader = ShaderUtil.CreateShaderAsset(PreviewShaderSource);
            if (_previewShader == null)
            {
                Debug.LogWarning(
                    "Could not compile the 3D terrain preview shader; the preview may render untextured.");
                return;
            }

            _previewShader.hideFlags = HideFlags.HideAndDontSave;

            _previewMaterial = new Material(_previewShader)
            {
                name = "Terrain Preview Material",
                hideFlags = HideFlags.HideAndDontSave
            };
        }

        /// <summary>
        /// Vertex-coloured diffuse shading with a fixed key light, written against the
        /// built-in pipeline because that is what PreviewRenderUtility renders with,
        /// regardless of the pipeline the project itself uses.
        /// </summary>
        const string PreviewShaderSource = @"
Shader ""Hidden/CustomTools/TerrainPreview""
{
    SubShader
    {
        Tags { ""RenderType"" = ""Opaque"" }
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include ""UnityCG.cginc""

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                fixed4 color  : COLOR;
            };

            struct v2f
            {
                float4 pos    : SV_POSITION;
                fixed4 color  : COLOR;
                float3 normal : TEXCOORD0;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.normal = UnityObjectToWorldNormal(v.normal);
                o.color = v.color;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float3 n = normalize(i.normal);
                float3 keyDir = normalize(float3(0.45, 0.85, -0.30));

                // Wrapped diffuse so slopes facing away stay readable rather than black.
                float key = saturate(dot(n, keyDir) * 0.5 + 0.5);
                float fill = saturate(n.y * 0.5 + 0.5);
                float light = 0.30 + key * 0.62 + fill * 0.12;

                return fixed4(i.color.rgb * light, 1);
            }
            ENDCG
        }
    }
}";

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
