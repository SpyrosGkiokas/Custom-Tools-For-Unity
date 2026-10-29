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
            int smoothing = 0)
        {
            if (analysis == null) throw new System.ArgumentNullException(nameof(analysis));
            if (rules == null || rules.Length == 0) return null;

            int res = Mathf.Max(2, alphamapResolution);
            int count = rules.Length;
            var map = new float[res, res, count];

            // Scratch buffer for one cell's weights, reused across cells.
            var weights = new float[count];

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

                    float total = 0f;

                    for (int i = 0; i < count; i++)
                    {
                        float w = rules[i].Evaluate(height, slope, curvature, flow);
                        if (w > 0f) w *= rules[i].NoiseMask(worldX, worldZ);

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
                        // No rule claims this cell. Falling back to the first layer
                        // keeps the terrain textured: an all-zero cell renders as an
                        // untextured hole, which reads as a bug rather than a gap in
                        // the rules.
                        map[y, x, 0] = 1f;
                    }
                }
            }

            if (smoothing > 0) Smooth(map, smoothing);

            return map;
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
