using UnityEngine;

namespace CustomTools.TerrainPainting
{
    /// <summary>
    /// Turns analysed terrain and a set of rules into terrain layer weights.
    ///
    /// Takes an analysis and rules rather than a settings object, so the same code
    /// serves the Inspector preview, the paint operation and any future caller without
    /// one of them having to fabricate a settings asset to ask a question.
    /// </summary>
    public static class SplatmapPainter
    {
        /// <summary>
        /// Fills <paramref name="weights"/> with the biome weights at a world position,
        /// normalised to sum to 1.
        ///
        /// A delegate rather than a BiomeMap parameter, so this assembly does not depend
        /// on the biome tool: the painter works copied on its own, and gains biome
        /// support when the biome folder is present to supply one of these.
        /// </summary>
        public delegate void BiomeSampler(
            float worldX, float worldZ, float heightMetres, float[] weights);

        /// <summary>
        /// Builds the weight map Unity expects: indexed [y, x, layer], with the weights
        /// at each cell summing to 1.
        /// </summary>
        /// <param name="rules">
        /// Only rules that will paint. Disabled ones must be filtered out beforehand,
        /// since a layer's index here has to line up with the terrain's layer array.
        /// </param>
        public static float[,,] Build(
            TerrainAnalysis analysis,
            PaintRule[] rules,
            int alphamapResolution,
            int smoothing = 0,
            float macroSize = 0f,
            float macroStrength = 0f,
            BiomeSampler biomes = null,
            int biomeCount = 0)
        {
            if (analysis == null) throw new System.ArgumentNullException(nameof(analysis));
            if (rules == null || rules.Length == 0) return null;

            int res = Mathf.Max(2, alphamapResolution);
            int count = rules.Length;
            var map = new float[res, res, count];

            // Scratch buffer for one cell's weights, reused across cells.
            var weights = new float[count];

            // One buffer for the biome weights too, for the same reason: this is sampled
            // once per alphamap cell, and allocating there would mean a collection every
            // few thousand cells.
            bool useBiomes = biomes != null && biomeCount > 0;
            var biomeWeights = useBiomes ? new float[biomeCount] : null;

            for (int y = 0; y < res; y++)
            {
                for (int x = 0; x < res; x++)
                {
                    // The alphamap rarely matches the heightmap resolution, so work in
                    // normalized space and let the analysis interpolate.
                    float nx = (float)x / (res - 1);
                    float ny = (float)y / (res - 1);

                    analysis.Sample(nx, ny, out float height, out float slope, out float curvature, out float flow);

                    // Noise is a function of world position, not cell index, so a rule's
                    // break-up pattern stays put when the alphamap resolution changes.
                    float worldX = nx * analysis.WorldSize;
                    float worldZ = ny * analysis.WorldSize;

                    // Biomes are sampled in world space, including the terrain's own
                    // position, so a biome region crosses tile boundaries instead of
                    // restarting at each terrain - which would draw the tile grid into
                    // the result on a multi-terrain world.
                    if (useBiomes)
                    {
                        biomes(
                            analysis.WorldOrigin.x + worldX,
                            analysis.WorldOrigin.z + worldZ,
                            analysis.WorldOrigin.y + height * analysis.WorldHeight,
                            biomeWeights);
                    }

                    float total = 0f;

                    // One macro value per cell, shared by every rule. Scaling all the
                    // weights together would do nothing - normalisation divides it
                    // straight back out - so each rule takes the value differently and
                    // what shifts is the balance between them.
                    bool useMacro = macroStrength > 0f && macroSize > 0f;
                    float macro = useMacro ? Macro(worldX, worldZ, macroSize) : 0.5f;

                    for (int i = 0; i < count; i++)
                    {
                        float w = rules[i].Evaluate(height, slope, curvature, flow);
                        if (w > 0f) w *= rules[i].NoiseMask(worldX, worldZ);

                        // Applied after the bands so a rule confined to a biome still
                        // honours its own height and slope within it: a biome selects
                        // where a rule may paint, it does not paint on the rule's behalf.
                        if (w > 0f && useBiomes) w *= rules[i].BiomeMask(biomeWeights);

                        // Alternating rules favour opposite ends of the macro value, so
                        // regions emerge where one layer leads and its neighbour recedes
                        // - lusher here, barer there - rather than the whole map shifting
                        // together. A base layer is left out: it is the floor under the
                        // others, and varying it would reintroduce the gaps it prevents.
                        if (useMacro && w > 0f && !rules[i].isBaseLayer)
                        {
                            float favour = (i & 1) == 0 ? macro : 1f - macro;
                            w *= Mathf.Lerp(1f, favour * 2f, macroStrength);
                        }

                        // The noise mask must not cut a base layer below its floor:
                        // that floor is what guarantees every cell has a layer, and
                        // noise eating into it would put the holes back.
                        if (rules[i].isBaseLayer)
                            w = Mathf.Max(w, PaintRule.BaseLayerFloor * rules[i].strength);

                        weights[i] = w;
                        total += w;
                    }

                    if (total > 0.0001f)
                    {
                        for (int i = 0; i < count; i++)
                            map[y, x, i] = weights[i] / total;
                    }
                    else
                    {
                        // Nothing claimed this cell at all, which means no rule is
                        // marked as the base - a base layer's floor would have caught
                        // it. Falling back to the first layer keeps the terrain
                        // textured, since an all-zero cell renders as an untextured
                        // hole that reads as a bug rather than a gap in the rules.
                        map[y, x, 0] = 1f;
                    }
                }
            }

            if (smoothing > 0) Smooth(map, smoothing);

            return map;
        }

        /// <summary>
        /// Large-scale 0..1 variation across the terrain. Two octaves rather than one,
        /// so regions have some internal shape instead of reading as a smooth gradient
        /// from one corner of the map to the other.
        /// </summary>
        static float Macro(float worldX, float worldZ, float size)
        {
            // The offsets keep this away from Perlin's zero at integer coordinates,
            // which would otherwise put a predictable seam through the map origin.
            float a = Mathf.PerlinNoise(worldX / size + 31.7f, worldZ / size + 17.3f);
            float b = Mathf.PerlinNoise(worldX / (size * 0.45f) + 5.1f, worldZ / (size * 0.45f) + 23.9f);

            return Mathf.Clamp01(a * 0.7f + b * 0.3f);
        }

        /// <summary>
        /// Box-blurs each layer's weights, renormalising afterwards so the cells still
        /// sum to 1. Softens edges the rules' own blending leaves behind, particularly
        /// where a narrow band meets a hard slope cutoff.
        /// </summary>
        static void Smooth(float[,,] map, int passes)
        {
            int res = map.GetLength(0);
            int count = map.GetLength(2);

            var scratch = new float[res, res];

            for (int pass = 0; pass < passes; pass++)
            {
                for (int layer = 0; layer < count; layer++)
                {
                    for (int y = 0; y < res; y++)
                    {
                        for (int x = 0; x < res; x++)
                        {
                            float sum = 0f;
                            int taps = 0;

                            for (int oy = -1; oy <= 1; oy++)
                            {
                                int ny = y + oy;
                                if (ny < 0 || ny >= res) continue;

                                for (int ox = -1; ox <= 1; ox++)
                                {
                                    int nx = x + ox;
                                    if (nx < 0 || nx >= res) continue;

                                    sum += map[ny, nx, layer];
                                    taps++;
                                }
                            }

                            scratch[y, x] = sum / taps;
                        }
                    }

                    for (int y = 0; y < res; y++)
                        for (int x = 0; x < res; x++)
                            map[y, x, layer] = scratch[y, x];
                }

                // Blurring each layer independently breaks the sum-to-1 invariant.
                for (int y = 0; y < res; y++)
                {
                    for (int x = 0; x < res; x++)
                    {
                        float total = 0f;
                        for (int i = 0; i < count; i++) total += map[y, x, i];

                        if (total > 0.0001f)
                        {
                            for (int i = 0; i < count; i++) map[y, x, i] /= total;
                        }
                        else
                        {
                            map[y, x, 0] = 1f;
                        }
                    }
                }
            }
        }
    }
}
