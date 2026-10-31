using UnityEditor;
using UnityEngine;

namespace CustomTools.TerrainBiomesURP.EditorTools
{
    /// <summary>
    /// Inspector for a biome map, with two previews.
    ///
    /// The climate chart shows which biome claims which temperature and moisture, and
    /// is where gaps and overlaps in the biome set are visible. The world map shows
    /// where those climates actually land. Neither can be worked out from the numbers:
    /// a biome occupying 0.3-0.6 temperature means nothing until you see how much of
    /// the world that covers.
    /// </summary>
    [CustomEditor(typeof(BiomeMap))]
    public class BiomeMapEditor : Editor
    {
        enum View { World, Climate }

        BiomeMap _map;
        View _view = View.World;

        Texture2D _preview;
        float[] _weights;
        float[] _coverage;

        bool _dirty = true;
        bool _wasDragging;
        bool _rebuildImmediately;
        double _settledAt = -1d;

        const double IdleDebounceSeconds = 1d;
        const int PreviewResolution = 256;

        /// <summary>
        /// How much world the map preview covers, in metres. Large enough that several
        /// climate regions are visible at the default scale, since the point of the
        /// view is the pattern rather than any one place.
        /// </summary>
        float _worldExtent = 20000f;

        void OnEnable()
        {
            _map = (BiomeMap)target;
            _dirty = true;
        }

        void OnDisable()
        {
            if (_preview != null) DestroyImmediate(_preview);
            _preview = null;
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            EditorGUI.BeginChangeCheck();
            DrawDefaultInspector();
            if (EditorGUI.EndChangeCheck())
            {
                serializedObject.ApplyModifiedProperties();
                _dirty = true;
            }

            EditorGUILayout.Space();
            DrawPreviewSection();
        }

        // --- debounce, as in the other tools ---------------------------------

        void RebuildNow()
        {
            _dirty = true;
            _wasDragging = false;
            _settledAt = -1d;
            _rebuildImmediately = true;
        }

        bool ReadyToRebuild()
        {
            if (_rebuildImmediately)
            {
                _rebuildImmediately = false;
                return true;
            }

            if (_preview == null) return true;

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

        // --- preview ---------------------------------------------------------

        void DrawPreviewSection()
        {
            EditorGUILayout.LabelField("Preview", EditorStyles.boldLabel);

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUI.BeginChangeCheck();
                _view = (View)GUILayout.Toolbar((int)_view, new[] { "World", "Climate" }, GUILayout.Height(22));
                if (EditorGUI.EndChangeCheck()) RebuildNow();
            }

            if (_view == View.World)
            {
                EditorGUI.BeginChangeCheck();
                _worldExtent = EditorGUILayout.Slider("Area shown (m)", _worldExtent, 1000f, 60000f);
                if (EditorGUI.EndChangeCheck()) _dirty = true;
            }

            bool mayRebuild = Event.current.type == EventType.Layout && ReadyToRebuild();
            if (_dirty && mayRebuild) RebuildPreview();

            if (_preview != null)
            {
                float size = Mathf.Min(EditorGUIUtility.currentViewWidth - 40f, 440f);
                var rect = GUILayoutUtility.GetRect(size, size, GUILayout.ExpandWidth(false));
                EditorGUI.DrawPreviewTexture(rect, _preview, null, ScaleMode.ScaleToFit);
            }

            EditorGUILayout.LabelField(
                _view == View.World
                    ? $"{_worldExtent / 1000f:0.#}km across, sampled at sea level."
                    : "Temperature left to right, moisture bottom to top. Dark means no biome claims it.",
                EditorStyles.miniLabel);

            DrawLegend();
        }

        /// <summary>
        /// Lists each biome with its colour and share of the world. Coverage is the
        /// number that matters: a biome at 0% is defined but never occurs, which the
        /// chart alone does not make obvious.
        /// </summary>
        void DrawLegend()
        {
            if (_map.biomes == null || _coverage == null) return;

            EditorGUILayout.Space(2f);

            for (int i = 0; i < _map.biomes.Length && i < _coverage.Length; i++)
            {
                var biome = _map.biomes[i];
                if (biome == null) continue;

                var row = EditorGUILayout.GetControlRect(false, 16f);

                var swatch = new Rect(row.x, row.y + 2f, 12f, 12f);
                EditorGUI.DrawRect(swatch, biome.enabled ? biome.previewColor : Color.grey * 0.4f);

                string label = string.IsNullOrEmpty(biome.name) ? $"Biome {i}" : biome.name;
                if (!biome.enabled) label += "  (off)";

                EditorGUI.LabelField(
                    new Rect(swatch.xMax + 6f, row.y, row.width - 80f, row.height),
                    label, EditorStyles.miniLabel);

                float percent = _coverage[i] * 100f;
                var style = new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.MiddleRight };
                if (biome.enabled && percent < 0.5f) style.normal.textColor = new Color(0.90f, 0.55f, 0.35f);

                EditorGUI.LabelField(
                    new Rect(row.xMax - 60f, row.y, 56f, row.height),
                    percent < 0.5f ? "~0%" : $"{percent:0.#}%", style);
            }

            for (int i = 0; i < _map.biomes.Length && i < _coverage.Length; i++)
            {
                var biome = _map.biomes[i];
                if (biome == null || !biome.enabled || _coverage[i] * 100f >= 0.5f) continue;

                EditorGUILayout.HelpBox(
                    $"\"{(string.IsNullOrEmpty(biome.name) ? $"Biome {i}" : biome.name)}\" never occurs. " +
                    "Its climate band may be outside what the noise actually produces, or another " +
                    "biome may be covering the same ground with a higher dominance.",
                    MessageType.Info);
                break;
            }
        }

        void RebuildPreview()
        {
            _dirty = false;

            int count = _map.biomes != null ? _map.biomes.Length : 0;
            if (count == 0) return;

            if (_weights == null || _weights.Length != count) _weights = new float[count];
            if (_coverage == null || _coverage.Length != count) _coverage = new float[count];

            for (int i = 0; i < count; i++) _coverage[i] = 0f;

            int res = PreviewResolution;
            if (_preview == null || _preview.width != res)
            {
                if (_preview != null) DestroyImmediate(_preview);
                _preview = new Texture2D(res, res, TextureFormat.RGBA32, false)
                {
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp
                };
            }

            var pixels = new Color32[res * res];

            for (int y = 0; y < res; y++)
            {
                for (int x = 0; x < res; x++)
                {
                    float u = (float)x / (res - 1);
                    float v = (float)y / (res - 1);

                    Color c = _view == View.World
                        ? WorldPixel(u, v, count)
                        : ClimatePixel(u, v, count);

                    pixels[y * res + x] = c;
                }
            }

            float samples = res * res;
            for (int i = 0; i < count; i++) _coverage[i] /= samples;

            _preview.SetPixels32(pixels);
            _preview.Apply(false);
        }

        /// <summary>Blended biome colours over a square of world.</summary>
        Color WorldPixel(float u, float v, int count)
        {
            float worldX = (u - 0.5f) * _worldExtent;
            float worldZ = (v - 0.5f) * _worldExtent;

            // Sea level: height cooling is a property of terrain this map knows nothing
            // about, and showing it at one altitude keeps the view about climate.
            _map.SampleWeights(worldX, worldZ, 0f, _weights);

            var c = new Color(0f, 0f, 0f);
            for (int i = 0; i < count; i++)
            {
                float w = _weights[i];
                if (w <= 0f) continue;

                _coverage[i] += w;

                var bc = _map.biomes[i] != null ? _map.biomes[i].previewColor : Color.grey;
                c.r += bc.r * w;
                c.g += bc.g * w;
                c.b += bc.b * w;
            }

            return c;
        }

        /// <summary>
        /// The temperature and moisture space itself, which is where gaps between
        /// biomes show up as dark patches.
        /// </summary>
        Color ClimatePixel(float u, float v, int count)
        {
            float total = 0f;
            var c = new Color(0f, 0f, 0f);

            for (int i = 0; i < count; i++)
            {
                var biome = _map.biomes[i];
                if (biome == null || !biome.enabled) continue;

                float w = biome.Weight(u, v, _map.blend);
                if (w <= 0f) continue;

                total += w;
                c.r += biome.previewColor.r * w;
                c.g += biome.previewColor.g * w;
                c.b += biome.previewColor.b * w;
            }

            if (total <= 0.0001f) return new Color(0.10f, 0.10f, 0.12f);

            c.r /= total;
            c.g /= total;
            c.b /= total;
            return c;
        }
    }
}
