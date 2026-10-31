using UnityEngine;

namespace CustomTools.TerrainPaintingURP
{
    /// <summary>
    /// The per-cell terrain properties the paint rules match against, derived from a
    /// heightmap alone so any terrain can be painted, not only one this package built.
    ///
    /// Height and slope come straight from the gradient. Curvature separates ridges
    /// from valleys, which height and slope together cannot express: a ridge crest and
    /// a valley floor can sit at the same elevation and both be flat. Flow approximates
    /// how much water converges on a cell, which is what distinguishes a damp channel
    /// from the dry ground beside it at the same height and angle.
    /// </summary>
    public sealed class TerrainAnalysis
    {
        /// <summary>Normalized 0..1 height, as stored in the heightmap.</summary>
        public float[,] Height { get; private set; }

        /// <summary>Slope in degrees: 0 is flat, 90 is vertical.</summary>
        public float[,] Slope { get; private set; }

        /// <summary>
        /// Curvature, negative in valleys and positive on ridges, normalized to
        /// roughly -1..1 so a rule band is readable without knowing the terrain's scale.
        /// </summary>
        public float[,] Curvature { get; private set; }

        /// <summary>
        /// Flow accumulation normalized to 0..1: how much of the map drains through
        /// each cell. Channels and valley floors run high, exposed ridges near zero.
        /// </summary>
        public float[,] Flow { get; private set; }

        /// <summary>Resolution of every map above (they are all square and equal).</summary>
        public int Resolution { get; private set; }

        /// <summary>Terrain width in metres, needed to read slope in real-world terms.</summary>
        public float WorldSize { get; private set; }

        /// <summary>Terrain height in metres, likewise.</summary>
        public float WorldHeight { get; private set; }

        /// <summary>
        /// Where this terrain sits in the scene, in metres. Only consumers working in
        /// world space need it - biome sampling does, since a climate region has to
        /// continue across a tiled world rather than restart at each terrain.
        /// Defaults to the origin, which is correct for a single terrain placed there.
        /// </summary>
        public Vector3 WorldOrigin { get; private set; }

        /// <summary>
        /// Analyses a heightmap. <paramref name="worldSize"/> and
        /// <paramref name="worldHeight"/> are the terrain's metre dimensions: slope is
        /// meaningless without them, since the same normalized heights describe gentle
        /// hills or sheer cliffs depending on the scale they are stretched over.
        /// </summary>
        /// <param name="externalFlow">
        /// A flow map from erosion, when one is available. Simulated flow follows the
        /// paths that actually carved the terrain, so it beats anything inferred after
        /// the fact; flow is computed from the heightmap when this is null.
        /// </param>
        public static TerrainAnalysis Analyse(
            float[,] heights,
            float worldSize,
            float worldHeight,
            float[,] externalFlow = null,
            Vector3 worldOrigin = default)
        {
            if (heights == null) throw new System.ArgumentNullException(nameof(heights));

            int res = heights.GetLength(0);
            if (res < 2 || heights.GetLength(1) != res)
                throw new System.ArgumentException("Heightmap must be square and at least 2x2.", nameof(heights));

            var analysis = new TerrainAnalysis
            {
                Height = heights,
                Resolution = res,
                WorldSize = Mathf.Max(0.0001f, worldSize),
                WorldHeight = Mathf.Max(0.0001f, worldHeight),
                WorldOrigin = worldOrigin
            };

            analysis.BuildSlopeAndCurvature();

            analysis.Flow = externalFlow != null && externalFlow.GetLength(0) == res
                ? externalFlow
                : analysis.BuildFlow();

            return analysis;
        }

        /// <summary>
        /// Slope and curvature share a pass: both read the same eight neighbours, and
        /// sampling the heightmap twice over is the expensive part at 1025 and above.
        /// </summary>
        void BuildSlopeAndCurvature()
        {
            int res = Resolution;
            Slope = new float[res, res];
            Curvature = new float[res, res];

            // Spacing between adjacent samples, in metres.
            float step = WorldSize / (res - 1);

            // Curvature is a second derivative, so its raw magnitude scales with
            // 1/step² and with the height range. Dividing that out keeps a rule's
            // curvature band meaning the same shape across resolutions and terrain
            // sizes, instead of needing retuning whenever either changes.
            float curvatureScale = (step * step) / WorldHeight;

            for (int y = 0; y < res; y++)
            {
                for (int x = 0; x < res; x++)
                {
                    // Clamped neighbour indices: edge cells reuse themselves rather
                    // than wrapping, so a terrain's border does not sample the far side.
                    int xm = Mathf.Max(x - 1, 0);
                    int xp = Mathf.Min(x + 1, res - 1);
                    int ym = Mathf.Max(y - 1, 0);
                    int yp = Mathf.Min(y + 1, res - 1);

                    float hC = Height[y, x];
                    float hL = Height[y, xm];
                    float hR = Height[y, xp];
                    float hD = Height[ym, x];
                    float hU = Height[yp, x];

                    // Central differences, converted to metres so the angle is real.
                    float dzdx = (hR - hL) * WorldHeight / (2f * step);
                    float dzdy = (hU - hD) * WorldHeight / (2f * step);

                    Slope[y, x] = Mathf.Atan(Mathf.Sqrt(dzdx * dzdx + dzdy * dzdy)) * Mathf.Rad2Deg;

                    // Laplacian: the centre against the mean of its four neighbours.
                    // Positive where the surface falls away on both axes (a ridge or
                    // peak), negative where it rises around the cell (a valley).
                    float laplacian = (hL + hR + hD + hU) - 4f * hC;
                    Curvature[y, x] = Mathf.Clamp(-laplacian / curvatureScale, -1f, 1f);
                }
            }
        }

        /// <summary>
        /// Flow accumulation: every cell starts with one unit of water and passes it
        /// downhill, so a cell's total is the area draining through it. Processing cells
        /// highest-first means a cell's own inflow is final before it is handed on,
        /// which gets the whole map in one pass instead of iterating to convergence.
        ///
        /// Water is split between all lower neighbours in proportion to drop rather
        /// than sent to the steepest one, because a single-neighbour rule produces
        /// one-cell-wide channels with hard edges - visible as thin lines in a painted
        /// result, where a real drainage pattern spreads.
        /// </summary>
        float[,] BuildFlow()
        {
            int res = Resolution;
            int count = res * res;

            var accumulation = new float[res, res];
            for (int y = 0; y < res; y++)
                for (int x = 0; x < res; x++)
                    accumulation[y, x] = 1f;

            // Cell indices sorted by descending height.
            var order = new int[count];
            for (int i = 0; i < count; i++) order[i] = i;

            var keys = new float[count];
            for (int i = 0; i < count; i++) keys[i] = -Height[i / res, i % res];
            System.Array.Sort(keys, order);

            // Reused per cell so the loop allocates nothing.
            var dropX = new int[8];
            var dropY = new int[8];
            var drops = new float[8];

            for (int i = 0; i < count; i++)
            {
                int index = order[i];
                int y = index / res;
                int x = index % res;

                float h = Height[y, x];
                float water = accumulation[y, x];
                if (water <= 0f) continue;

                float totalDrop = 0f;
                int found = 0;

                for (int oy = -1; oy <= 1; oy++)
                {
                    for (int ox = -1; ox <= 1; ox++)
                    {
                        if (ox == 0 && oy == 0) continue;

                        int nx = x + ox;
                        int ny = y + oy;
                        if (nx < 0 || nx >= res || ny < 0 || ny >= res) continue;

                        float drop = h - Height[ny, nx];
                        if (drop <= 0f) continue;

                        // Diagonal neighbours are further away, so the same height
                        // difference is a gentler gradient and takes proportionally
                        // less of the water.
                        if (ox != 0 && oy != 0) drop *= 0.7071f;

                        dropX[found] = nx;
                        dropY[found] = ny;
                        drops[found] = drop;
                        totalDrop += drop;
                        found++;
                    }
                }

                // No lower neighbour: a pit or the map edge. The water stops here,
                // which is correct - a basin is genuinely where water collects.
                if (found == 0 || totalDrop <= 0f) continue;

                for (int d = 0; d < found; d++)
                    accumulation[dropY[d], dropX[d]] += water * (drops[d] / totalDrop);
            }

            NormaliseFlow(accumulation);
            return accumulation;
        }

        /// <summary>
        /// Maps raw accumulation into 0..1. The distribution is extremely skewed - a
        /// main channel can carry thousands of times what a hillside cell does - so a
        /// linear rescale would leave everything but the trunk rivers at almost zero.
        /// A log curve spreads the low end out, where the distinction between dry
        /// ground and a damp hollow actually matters for painting.
        /// </summary>
        static void NormaliseFlow(float[,] flow)
        {
            int res = flow.GetLength(0);

            float max = 0f;
            for (int y = 0; y < res; y++)
                for (int x = 0; x < res; x++)
                    if (flow[y, x] > max) max = flow[y, x];

            if (max <= 1f) return;

            float logMax = Mathf.Log(1f + max);

            for (int y = 0; y < res; y++)
                for (int x = 0; x < res; x++)
                    flow[y, x] = Mathf.Log(1f + flow[y, x]) / logMax;
        }

        /// <summary>
        /// Samples every channel at a normalized position, for consumers working at a
        /// different resolution than the heightmap - the alphamap usually is.
        /// </summary>
        public void Sample(float nx, float ny, out float height, out float slope, out float curvature, out float flow)
        {
            height = Bilinear(Height, nx, ny);
            slope = Bilinear(Slope, nx, ny);
            curvature = Bilinear(Curvature, nx, ny);
            flow = Bilinear(Flow, nx, ny);
        }

        /// <summary>Bilinear sample of a square map in normalized 0..1 coordinates.</summary>
        public static float Bilinear(float[,] map, float nx, float ny)
        {
            int res = map.GetLength(0);

            float fx = Mathf.Clamp01(nx) * (res - 1);
            float fy = Mathf.Clamp01(ny) * (res - 1);

            int x0 = Mathf.FloorToInt(fx);
            int y0 = Mathf.FloorToInt(fy);
            int x1 = Mathf.Min(x0 + 1, res - 1);
            int y1 = Mathf.Min(y0 + 1, res - 1);

            float tx = fx - x0;
            float ty = fy - y0;

            float bottom = Mathf.Lerp(map[y0, x0], map[y0, x1], tx);
            float top = Mathf.Lerp(map[y1, x0], map[y1, x1], tx);

            return Mathf.Lerp(bottom, top, ty);
        }
    }
}
