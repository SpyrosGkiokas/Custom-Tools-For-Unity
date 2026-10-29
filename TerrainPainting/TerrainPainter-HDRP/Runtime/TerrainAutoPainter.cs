using System.Collections.Generic;
using UnityEngine;

namespace CustomTools.TerrainPainting
{
    /// <summary>
    /// Paints terrain layers onto a Terrain by rule, reading the heights that are
    /// already there. Unlike the terrain generator, this does not create or modify
    /// terrain shape: it works on whatever is in the TerrainData, however that got
    /// there - sculpted by hand, imported, or generated.
    /// </summary>
    [AddComponentMenu("Custom Tools/Terrain Auto Painter")]
    [RequireComponent(typeof(Terrain))]
    public class TerrainAutoPainter : MonoBehaviour
    {
        [Tooltip("The rules to paint with. Create one via Assets > Create > Custom Tools.")]
        public AutoPaintSettings settings;

        /// <summary>The Terrain this component paints.</summary>
        public Terrain Terrain => GetComponent<Terrain>();

        /// <summary>
        /// Analyses the terrain's current heights. Separated from painting so the
        /// Inspector can show the input channels without writing to the terrain.
        /// </summary>
        public TerrainAnalysis Analyse(int resolution = 0)
        {
            var data = Terrain != null ? Terrain.terrainData : null;
            if (data == null) return null;

            int heightRes = data.heightmapResolution;
            int sampleRes = resolution > 0 ? Mathf.Min(resolution, heightRes) : heightRes;

            float[,] heights = ReadHeights(data, sampleRes);

            var size = data.size;
            return TerrainAnalysis.Analyse(heights, size.x, size.y);
        }

        /// <summary>
        /// Reads heights at a chosen resolution. GetHeights always returns the full
        /// heightmap, so a lower resolution is taken by striding over it: the preview
        /// does not need 4097² samples to show where the cliffs are.
        /// </summary>
        static float[,] ReadHeights(TerrainData data, int sampleRes)
        {
            int full = data.heightmapResolution;
            var source = data.GetHeights(0, 0, full, full);

            if (sampleRes >= full) return source;

            var reduced = new float[sampleRes, sampleRes];
            for (int y = 0; y < sampleRes; y++)
            {
                int sy = Mathf.RoundToInt((float)y / (sampleRes - 1) * (full - 1));
                for (int x = 0; x < sampleRes; x++)
                {
                    int sx = Mathf.RoundToInt((float)x / (sampleRes - 1) * (full - 1));
                    reduced[y, x] = source[sy, sx];
                }
            }

            return reduced;
        }

        /// <summary>Rules that will paint, in the order their layers are assigned.</summary>
        public List<PaintRule> ActiveRules()
        {
            var active = new List<PaintRule>();
            if (settings?.rules == null) return active;

            foreach (var rule in settings.rules)
                if (rule != null && rule.enabled) active.Add(rule);

            return active;
        }

        /// <summary>
        /// Paints the terrain. Returns false with a reason when it cannot, rather than
        /// throwing or half-applying: this is driven from an Inspector button, where a
        /// clear message beats an exception in the console.
        /// </summary>
        public bool Paint(out string error)
        {
            error = null;

            var terrain = Terrain;
            var data = terrain != null ? terrain.terrainData : null;

            if (data == null)
            {
                error = "No TerrainData on this Terrain.";
                return false;
            }

            if (settings == null)
            {
                error = "Assign an Auto Paint Settings asset.";
                return false;
            }

            var active = ActiveRules();
            if (active.Count == 0)
            {
                error = "No enabled rules to paint with.";
                return false;
            }

            var missing = new List<string>();
            for (int i = 0; i < active.Count; i++)
                if (active[i].layer == null)
                    missing.Add(string.IsNullOrEmpty(active[i].name) ? $"Rule {i}" : active[i].name);

            // Every rule needs a layer: the weight at index i is meaningless unless the
            // terrain's layer i is the one the rule meant.
            if (missing.Count > 0)
            {
                error = "These rules have no Terrain Layer assigned: " + string.Join(", ", missing);
                return false;
            }

            var analysis = Analyse();
            if (analysis == null)
            {
                error = "Could not read the terrain's heights.";
                return false;
            }

            int alphaRes = settings.ResolvedAlphamapResolution;

            var splat = SplatmapPainter.Build(analysis, active.ToArray(), alphaRes, settings.smoothing);
            if (splat == null)
            {
                error = "Painting produced no weights.";
                return false;
            }

            // Layers must be assigned before the alphamap: SetAlphamaps validates its
            // third dimension against the current layer count and throws on a mismatch.
            var layers = new TerrainLayer[active.Count];
            for (int i = 0; i < active.Count; i++) layers[i] = active[i].layer;

            data.terrainLayers = layers;
            data.alphamapResolution = alphaRes;
            data.SetAlphamaps(0, 0, splat);

            return true;
        }
    }
}
