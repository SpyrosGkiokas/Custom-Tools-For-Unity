using UnityEngine;

namespace CustomTools.TerrainGeneration
{
    /// <summary>
    /// Thermal erosion, also called talus relaxation. Rock fractures and the debris
    /// slides downhill until the slope settles at the material's angle of repose, piling
    /// scree at the base of cliffs.
    ///
    /// Hydraulic erosion cannot do this: it moves material only where water runs. Noise
    /// alone produces slopes steeper than any real mountainside, and this is what brings
    /// them back to an angle rock can actually hold.
    /// </summary>
    public static class ThermalErosion
    {
        // Four-neighbour transfer. The diagonals are deliberately excluded: including
        // them at equal weight biases material along the diagonals, because they span a
        // longer distance for the same height difference.
        static readonly int[] NeighbourX = { -1, 1, 0, 0 };
        static readonly int[] NeighbourY = { 0, 0, -1, 1 };

        /// <summary>
        /// Relaxes slopes in <paramref name="heights"/> in place.
        /// </summary>
        /// <param name="heights">Normalized heightmap, indexed [y, x].</param>
        /// <param name="settings">Talus tuning.</param>
        /// <param name="worldSize">Terrain footprint in metres, for real slope angles.</param>
        /// <param name="worldHeight">Terrain height in metres, for real slope angles.</param>
        /// <param name="onProgress">0..1 progress; return false to cancel.</param>
        public static void Apply(
            float[,] heights,
            ThermalSettings settings,
            float worldSize,
            float worldHeight,
            System.Func<float, bool> onProgress = null)
        {
            if (heights == null || settings == null || settings.iterations <= 0) return;

            int res = heights.GetLength(0);
            if (res < 4) return;

            // Convert the angle of repose into the height difference that angle allows
            // between adjacent cells, so the setting means the same thing whatever the
            // terrain's resolution or world scale.
            float metresPerCell = worldSize / (res - 1);
            float maxDropMetres = Mathf.Tan(settings.talusAngle * Mathf.Deg2Rad) * metresPerCell;
            float threshold = worldHeight > 0f ? maxDropMetres / worldHeight : 0f;

            // Cells are visited highest first, so material shed from a peak can cascade
            // all the way down a slope within a single sweep. Visiting in grid order
            // instead moves it one cell per sweep, which is why a naive implementation
            // needs hundreds of sweeps to settle and costs seconds at 513.
            var order = BuildHeightOrder(heights, res);

            int reportEvery = Mathf.Max(1, settings.iterations / 20);

            for (int iteration = 0; iteration < settings.iterations; iteration++)
            {
                if (onProgress != null && iteration % reportEvery == 0)
                {
                    if (!onProgress((float)iteration / settings.iterations)) return;
                }

                bool movedAnything = false;

                foreach (int cell in order)
                {
                    int y = cell / res;
                    int x = cell - y * res;

                    float here = heights[y, x];

                    // Find how much this cell is over the stable angle, summed over
                    // every downhill neighbour, so material splits proportionally
                    // rather than all going to the steepest one.
                    float excessSum = 0f;
                    for (int n = 0; n < 4; n++)
                    {
                        int nx = x + NeighbourX[n];
                        int ny = y + NeighbourY[n];
                        if (nx < 0 || ny < 0 || nx >= res || ny >= res) continue;

                        float drop = here - heights[ny, nx];
                        if (drop > threshold) excessSum += drop - threshold;
                    }

                    if (excessSum <= 0f) continue;

                    // Half the excess, so a cell settles toward its neighbours without
                    // overshooting past them and oscillating.
                    float moved = excessSum * settings.rate * 0.5f;
                    if (moved <= 1e-7f) continue;

                    movedAnything = true;
                    heights[y, x] = Mathf.Clamp01(here - moved);

                    for (int n = 0; n < 4; n++)
                    {
                        int nx = x + NeighbourX[n];
                        int ny = y + NeighbourY[n];
                        if (nx < 0 || ny < 0 || nx >= res || ny >= res) continue;

                        float drop = here - heights[ny, nx];
                        if (drop <= threshold) continue;

                        float share = (drop - threshold) / excessSum;
                        heights[ny, nx] = Mathf.Clamp01(heights[ny, nx] + moved * share);
                    }
                }

                // Settled: further sweeps would only burn time.
                if (!movedAnything) return;
            }
        }

        /// <summary>
        /// Cell indices sorted highest first. Sorting once up front is far cheaper than
        /// the extra sweeps an unsorted traversal would need, and the order stays good
        /// enough as the terrain settles, since relaxation only ever lowers peaks.
        /// </summary>
        static int[] BuildHeightOrder(float[,] heights, int res)
        {
            int count = res * res;
            var indices = new int[count];
            var keys = new float[count];

            for (int y = 0; y < res; y++)
            {
                for (int x = 0; x < res; x++)
                {
                    int i = y * res + x;
                    indices[i] = i;

                    // Negated so the ascending sort yields highest-first.
                    keys[i] = -heights[y, x];
                }
            }

            System.Array.Sort(keys, indices);
            return indices;
        }
    }

    /// <summary>Thermal erosion tuning.</summary>
    [System.Serializable]
    public class ThermalSettings
    {
        [Tooltip("Relax slopes to a stable angle, piling debris at the base of cliffs. " +
                 "Noise produces slopes steeper than real rock can hold; this fixes that.")]
        public bool enabled = false;

        [Tooltip("Relaxation sweeps. Slopes approach the target angle gradually rather " +
                 "than being clamped to it, so this is the main quality dial: 20 " +
                 "noticeably softens cliffs, 100 settles the terrain well, and more " +
                 "keeps creeping closer for proportionally more time.")]
        [Min(0)] public int iterations = 40;

        [Tooltip("Slope the material settles toward, in degrees. Loose scree sits near " +
                 "35, consolidated rock near 50. This is a target the relaxation works " +
                 "toward, not a hard ceiling: the steepest slopes end up above it unless " +
                 "Iterations is high. Lower values give softer, more rounded terrain.")]
        [Range(5f, 80f)] public float talusAngle = 38f;

        [Tooltip("How much of the excess moves per sweep. 1 settles fastest and is " +
                 "stable; lower values are gentler but need more iterations.")]
        [Range(0.05f, 1f)] public float rate = 1f;
    }
}
