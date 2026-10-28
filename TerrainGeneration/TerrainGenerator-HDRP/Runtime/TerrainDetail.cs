using UnityEngine;

namespace CustomTools.TerrainGeneration
{
    /// <summary>
    /// Detail passes that run after erosion: micro-relief that varies by context, river
    /// channels carved along the flow map, and sedimentary banding.
    ///
    /// These all read the terrain erosion produced, so they must run after it — the
    /// slope and flow they key off only mean anything once water has shaped the surface.
    /// </summary>
    public static class TerrainDetail
    {
        /// <summary>
        /// Adds fine noise whose amplitude varies with slope and wetness, so exposed
        /// rock reads as rough while sediment flats and river beds stay smooth.
        ///
        /// Uniform detail is the giveaway of generated terrain: real ground is rough
        /// where rock is exposed and smooth where silt has settled. This is about where
        /// detail sits rather than how much of it there is, which is why adding noise
        /// octaves does not achieve the same thing.
        /// </summary>
        /// <param name="heights">Normalized heightmap, modified in place.</param>
        /// <param name="flow">Water flow 0..1 from erosion, or null.</param>
        public static void ApplyContextDetail(
            float[,] heights, float[,] flow, DetailSettings settings,
            int seed, float worldSize, float worldHeight)
        {
            if (heights == null || settings == null || !settings.enabled) return;
            if (settings.amplitude <= 0f) return;

            int res = heights.GetLength(0);
            var prng = new System.Random(seed ^ 0x5EED);
            float offsetX = prng.Next(-100000, 100000) * 0.0001f;
            float offsetY = prng.Next(-100000, 100000) * 0.0001f;

            // Sampled from the original surface so the detail being written does not
            // feed back into the slope that decides how much to write.
            var source = (float[,])heights.Clone();

            for (int y = 0; y < res; y++)
            {
                for (int x = 0; x < res; x++)
                {
                    float nx = (float)x / (res - 1);
                    float ny = (float)y / (res - 1);

                    float slope01 = SlopeNormalized(
                        source, x, y, res, worldSize, worldHeight, settings.steepAngle);

                    // Steep ground is exposed rock: rough. Flat ground has had sediment
                    // settle on it: smooth.
                    float roughness = Mathf.Lerp(settings.flatRoughness, 1f, slope01);

                    // Water smooths what it covers, so river beds and wet valley floors
                    // lose their roughness even where they are steep.
                    if (flow != null)
                    {
                        float wetness = Mathf.Clamp01(flow[y, x] * settings.wetSmoothing);
                        roughness *= 1f - wetness;
                    }

                    if (roughness <= 0.001f) continue;

                    float detail = Mathf.PerlinNoise(
                        nx * settings.detailScale + offsetX,
                        ny * settings.detailScale + offsetY) * 2f - 1f;

                    heights[y, x] = Mathf.Clamp01(
                        heights[y, x] + detail * settings.amplitude * roughness);
                }
            }
        }

        /// <summary>
        /// Cuts channels into the terrain along the flow map. Hydraulic erosion scours
        /// grooves but does not produce the flat-bottomed, banked profile that reads as a
        /// river, because a droplet's own path is narrower than the channel it would cut
        /// over geological time.
        /// </summary>
        public static void ApplyRiverChannels(
            float[,] heights, float[,] flow, RiverSettings settings)
        {
            if (heights == null || flow == null || settings == null || !settings.enabled) return;
            if (settings.depth <= 0f) return;

            int res = heights.GetLength(0);

            // Widen the flow map first: the raw map is as narrow as the droplet paths,
            // so carving it directly gives one-cell slots rather than valleys.
            var channel = Widen(flow, res, settings.width);

            for (int y = 0; y < res; y++)
            {
                for (int x = 0; x < res; x++)
                {
                    float strength = channel[y, x];
                    if (strength <= 0.001f) continue;

                    // Only cells carrying real water get a channel, and the threshold
                    // keeps hillside trickle from being carved into creeks.
                    float t = Mathf.InverseLerp(settings.threshold, 1f, strength);
                    if (t <= 0f) continue;

                    // Smoothstep gives the channel banks rather than a V: the floor
                    // flattens out while the edges stay graded.
                    float profile = t * t * (3f - 2f * t);

                    heights[y, x] = Mathf.Clamp01(heights[y, x] - profile * settings.depth);
                }
            }
        }

        /// <summary>
        /// Quantizes height into bands so sedimentary layers show as terraces, with the
        /// band edges softened so they read as strata rather than as stair steps.
        /// </summary>
        public static void ApplyStrata(float[,] heights, StrataSettings settings)
        {
            if (heights == null || settings == null || !settings.enabled) return;
            if (settings.strength <= 0f || settings.bands < 2) return;

            int res = heights.GetLength(0);
            float bands = settings.bands;

            for (int y = 0; y < res; y++)
            {
                for (int x = 0; x < res; x++)
                {
                    float h = heights[y, x];

                    float scaled = h * bands;
                    float band = Mathf.Floor(scaled);
                    float within = scaled - band;

                    // Sharpness pushes the transition toward a step; at 0 this is the
                    // original height and the whole pass does nothing.
                    float eased = Mathf.Pow(within, Mathf.Lerp(1f, 6f, settings.sharpness));
                    float terraced = (band + eased) / bands;

                    heights[y, x] = Mathf.Clamp01(Mathf.Lerp(h, terraced, settings.strength));
                }
            }
        }

        // --- helpers -------------------------------------------------------

        /// <summary>
        /// Slope at a cell as 0..1, where 1 is <paramref name="steepAngle"/> degrees or
        /// steeper. Uses real metres so the result does not change meaning when the
        /// terrain is resized.
        /// </summary>
        static float SlopeNormalized(
            float[,] heights, int x, int y, int res,
            float worldSize, float worldHeight, float steepAngle)
        {
            int xL = Mathf.Max(0, x - 1);
            int xR = Mathf.Min(res - 1, x + 1);
            int yD = Mathf.Max(0, y - 1);
            int yU = Mathf.Min(res - 1, y + 1);

            float metresPerCell = worldSize / (res - 1);
            float spanX = (xR - xL) * metresPerCell;
            float spanY = (yU - yD) * metresPerCell;

            float dx = spanX > 0f ? (heights[y, xR] - heights[y, xL]) * worldHeight / spanX : 0f;
            float dy = spanY > 0f ? (heights[yU, x] - heights[yD, x]) * worldHeight / spanY : 0f;

            float degrees = Mathf.Atan(Mathf.Sqrt(dx * dx + dy * dy)) * Mathf.Rad2Deg;
            return Mathf.Clamp01(degrees / Mathf.Max(1f, steepAngle));
        }

        /// <summary>
        /// Spreads the flow map outward so a channel covers more than the droplet paths
        /// that made it. A max filter rather than a blur, so a strong channel keeps its
        /// strength instead of being averaged away by the dry ground around it.
        /// </summary>
        static float[,] Widen(float[,] flow, int res, int radius)
        {
            radius = Mathf.Clamp(radius, 0, 8);
            if (radius == 0) return flow;

            var result = new float[res, res];

            for (int y = 0; y < res; y++)
            {
                for (int x = 0; x < res; x++)
                {
                    float best = 0f;

                    for (int oy = -radius; oy <= radius; oy++)
                    {
                        int sy = y + oy;
                        if (sy < 0 || sy >= res) continue;

                        for (int ox = -radius; ox <= radius; ox++)
                        {
                            int sx = x + ox;
                            if (sx < 0 || sx >= res) continue;

                            float distance = Mathf.Sqrt(ox * ox + oy * oy);
                            if (distance > radius) continue;

                            // Fade with distance so the channel's banks slope up to the
                            // surrounding ground instead of ending in a wall.
                            float falloff = 1f - distance / (radius + 1f);
                            float candidate = flow[sy, sx] * falloff;
                            if (candidate > best) best = candidate;
                        }
                    }

                    result[y, x] = best;
                }
            }

            return result;
        }
    }

    /// <summary>Context-driven micro-relief tuning.</summary>
    [System.Serializable]
    public class DetailSettings
    {
        [Tooltip("Add fine detail that varies by slope and wetness: rough on exposed " +
                 "rock, smooth on sediment flats and river beds.")]
        public bool enabled = false;

        [Tooltip("Height of the added detail, as a fraction of total terrain height. " +
                 "Keep this small — 0.002-0.01 is texture, not landscape.")]
        [Range(0f, 0.05f)] public float amplitude = 0.006f;

        [Tooltip("Size of the detail features. Higher is finer.")]
        [Range(10f, 400f)] public float detailScale = 120f;

        [Tooltip("How rough flat ground stays. 0 leaves flats perfectly smooth; " +
                 "1 makes detail uniform everywhere, which is the look this avoids.")]
        [Range(0f, 1f)] public float flatRoughness = 0.15f;

        [Tooltip("How strongly water smooths what it covers. Higher leaves river beds " +
                 "and wet valley floors flatter.")]
        [Range(0f, 4f)] public float wetSmoothing = 1.5f;

        [Tooltip("Slope treated as fully exposed rock, in degrees.")]
        [Range(10f, 80f)] public float steepAngle = 45f;
    }

    /// <summary>River channel carving tuning.</summary>
    [System.Serializable]
    public class RiverSettings
    {
        [Tooltip("Carve channels along the water flow erosion produced. Needs erosion " +
                 "enabled, since the flow map comes from it.")]
        public bool enabled = false;

        [Tooltip("How deep channels cut, as a fraction of terrain height.")]
        [Range(0f, 0.1f)] public float depth = 0.012f;

        [Tooltip("How far a channel spreads from its flow line, in cells. Wider gives " +
                 "valleys; narrower gives creeks.")]
        [Range(0, 8)] public int width = 3;

        [Tooltip("Minimum flow before anything is carved. Raise it to keep only the " +
                 "main rivers and leave hillside trickle alone.")]
        [Range(0f, 1f)] public float threshold = 0.25f;
    }

    /// <summary>Sedimentary banding tuning.</summary>
    [System.Serializable]
    public class StrataSettings
    {
        [Tooltip("Quantize height into visible rock layers. Striking in canyons, " +
                 "and more of a stylistic choice than a realism one.")]
        public bool enabled = false;

        [Tooltip("How strongly the banding applies. 0 is off, 1 is fully terraced.")]
        [Range(0f, 1f)] public float strength = 0.4f;

        [Tooltip("Number of layers across the full height range.")]
        [Range(2, 60)] public int bands = 18;

        [Tooltip("How hard each layer edge is. Low is a gentle undulation, " +
                 "high is a distinct step.")]
        [Range(0f, 1f)] public float sharpness = 0.6f;
    }
}
