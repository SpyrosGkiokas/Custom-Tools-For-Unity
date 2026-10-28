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

        /// <summary>A 3D preview mesh is capped here: 513² is already 263k verts.</summary>
        const int MaxMeshResolution = 513;

        int _previewResolution = 513;

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
            EditorGUI.BeginChangeCheck();
            DrawDefaultInspector();
            if (EditorGUI.EndChangeCheck()) _dirty = true;

            EditorGUILayout.Space();
            DrawPreviewSection();
        }

        void DrawPreviewSection()
        {
            EditorGUILayout.LabelField("Preview", EditorStyles.boldLabel);

            DrawPreviewToolbar();

            if (_dirty && (_autoRefresh || _heights == null))
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
                    if (_mode == PreviewMode.Mesh3D && _previewMesh == null) _dirty = true;
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
                    _dirty = true;
                }
            }

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
            var original = _settings.resolution;
            _settings.resolution = (TerrainMapSettings.TerrainResolution)_previewResolution;

            try
            {
                _heights = TerrainMapGenerator.GenerateHeights(_settings);

                if (_preview != null) DestroyImmediate(_preview);
                _preview = TerrainMapGenerator.GeneratePreview(_settings, _heights);

                if (_mode == PreviewMode.Mesh3D) RebuildMesh();
            }
            finally
            {
                // Restore even if generation throws, or the asset keeps the preview resolution.
                _settings.resolution = original;
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

            var vertices = new Vector3[res * res];
            var uvs = new Vector2[res * res];
            var colors = new Color[res * res];

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

            var triangles = new int[(res - 1) * (res - 1) * 6];
            int t = 0;
            for (int y = 0; y < res - 1; y++)
            {
                for (int x = 0; x < res - 1; x++)
                {
                    int i = y * res + x;
                    triangles[t++] = i;
                    triangles[t++] = i + res;
                    triangles[t++] = i + 1;

                    triangles[t++] = i + 1;
                    triangles[t++] = i + res;
                    triangles[t++] = i + res + 1;
                }
            }

            if (_previewMesh == null)
            {
                _previewMesh = new Mesh { name = "Terrain Preview Mesh" };
            }
            else
            {
                // Clear first: leftover triangles from a denser mesh would index
                // vertices that no longer exist.
                _previewMesh.Clear();
            }

            // 513² is 263k vertices, well past the 65k limit of 16-bit indices.
            _previewMesh.indexFormat = res * res > 65000
                ? UnityEngine.Rendering.IndexFormat.UInt32
                : UnityEngine.Rendering.IndexFormat.UInt16;

            _previewMesh.vertices = vertices;
            _previewMesh.uv = uvs;
            _previewMesh.colors = colors;
            _previewMesh.triangles = triangles;
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
