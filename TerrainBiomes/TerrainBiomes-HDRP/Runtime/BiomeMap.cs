using UnityEngine;

namespace CustomTools.TerrainBiomes
{
    /// <summary>
    /// Divides a world into regions by climate, so tools can ask "what kind of place
    /// is this?" at any world position and get a consistent answer.
    ///
    /// Sampling is in world space rather than per terrain, which is what makes this
    /// usable across a tiled world: a 20km map is several Unity terrains, and a biome
    /// that restarted at each tile boundary would draw the tile grid into the result.
    /// Nothing here knows or cares which terrain a position belongs to.
    ///
    /// Temperature and moisture are two large noise fields, and each biome claims a
    /// rectangle of that 2D space - the Whittaker model ecology uses. The reason to
    /// prefer it over one noise value split into bands is adjacency: it puts desert
    /// next to savanna and tundra next to taiga, because those genuinely neighbour
    /// each other in climate, where a single band order makes every neighbour
    /// arbitrary.
    /// </summary>
    [CreateAssetMenu(menuName = "Custom Tools/Biome Map", fileName = "BiomeMap")]
    public class BiomeMap : ScriptableObject
    {
        [Header("Climate")]

        [Tooltip(
            "Size of a climate region in metres. This is the scale a biome is felt at, " +
            "so it wants to be large: a few kilometres, not a few hundred metres.")]
        [Min(50f)] public float climateScale = 4000f;

        [Tooltip("Changes the climate layout without changing its character.")]
        public int seed = 0;

        [Tooltip(
            "How much altitude cools the climate. Mountains turn alpine on their own " +
            "rather than needing a biome placed on them by hand.")]
        [Range(0f, 1f)] public float heightCooling = 0.55f;

        [Tooltip(
            "Height in metres at which cooling is fully applied. Above this, " +
            "temperature has dropped by the full amount set above.")]
        [Min(1f)] public float coolingHeight = 2000f;

        [Header("Boundaries")]

        [Tooltip(
            "How wide a transition between biomes is, as a share of the climate space. " +
            "0 switches abruptly, which draws a visible line across the terrain.")]
        [Range(0f, 0.5f)] public float blend = 0.12f;

        [Tooltip(
            "Breaks up the boundaries so they do not read as smooth contours. " +
            "Measured in metres, and wants to be far smaller than the climate scale.")]
        [Min(0f)] public float edgeNoiseSize = 180f;

        [Tooltip("How far the edge noise can push a boundary, as a share of the blend width.")]
        [Range(0f, 1f)] public float edgeNoiseStrength = 0.6f;

        [Header("Biomes")]

        [Tooltip("Each biome claims a region of the temperature and moisture space.")]
        public Biome[] biomes = DefaultBiomes();

        /// <summary>Biomes that will contribute, in the order they are evaluated.</summary>
        public int ActiveCount
        {
            get
            {
                if (biomes == null) return 0;

                int n = 0;
                foreach (var b in biomes)
                    if (b != null && b.enabled) n++;
                return n;
            }
        }

        /// <summary>
        /// Climate at a world position. Height is in metres and lowers temperature, so
        /// the same latitude is alpine on a peak and temperate in the valley below it.
        /// </summary>
        public void SampleClimate(float worldX, float worldZ, float heightMetres,
                                  out float temperature, out float moisture)
        {
            float scale = Mathf.Max(1f, climateScale);

            // Offsetting by seed rather than reseeding keeps this a pure function of
            // position: the same world coordinates always give the same climate, which
            // is what lets neighbouring terrain tiles agree without talking to each other.
            float tx = (worldX / scale) + seed * 11.3f;
            float tz = (worldZ / scale) + seed * 7.9f;

            // Moisture is offset far from temperature in noise space so the two are
            // independent. Sampled at the same coordinates they would correlate, and
            // every biome would end up on one diagonal of the climate space.
            float mx = (worldX / scale) + seed * 5.1f + 311.7f;
            float mz = (worldZ / scale) + seed * 19.7f + 173.3f;

            temperature = Mathf.PerlinNoise(tx, tz);
            moisture = Mathf.PerlinNoise(mx, mz);

            if (heightCooling > 0f)
            {
                float t = Mathf.Clamp01(heightMetres / Mathf.Max(1f, coolingHeight));
                temperature = Mathf.Clamp01(temperature - t * heightCooling);
            }
        }

        /// <summary>
        /// Weight of every biome at a world position, normalised to sum to 1.
        ///
        /// Weights rather than a single winner, because a hard switch at a boundary is
        /// the failure everyone sees: grass becomes sand along a line no landscape
        /// would draw. Consumers blend their own settings by these weights, the same way
        /// the painter blends terrain layers.
        /// </summary>
        /// <param name="weights">
        /// Reused buffer, one entry per entry in <see cref="biomes"/>. Allocating per
        /// sample would mean a garbage collection per thousand points.
        /// </param>
        public void SampleWeights(float worldX, float worldZ, float heightMetres, float[] weights)
        {
            if (weights == null || biomes == null) return;

            SampleClimate(worldX, worldZ, heightMetres, out float temperature, out float moisture);

            // Noise pushes the sample rather than the boundary, which comes to the same
            // thing and costs one lookup instead of one per biome edge.
            float width = blend;
            if (edgeNoiseSize > 0f && edgeNoiseStrength > 0f && width > 0f)
            {
                float nx = Mathf.PerlinNoise(worldX / edgeNoiseSize + 41.7f, worldZ / edgeNoiseSize + 13.1f);
                float nz = Mathf.PerlinNoise(worldX / edgeNoiseSize + 97.3f, worldZ / edgeNoiseSize + 61.9f);

                float push = width * edgeNoiseStrength;
                temperature = Mathf.Clamp01(temperature + (nx - 0.5f) * 2f * push);
                moisture = Mathf.Clamp01(moisture + (nz - 0.5f) * 2f * push);
            }

            float total = 0f;

            for (int i = 0; i < biomes.Length && i < weights.Length; i++)
            {
                var biome = biomes[i];
                float w = biome != null && biome.enabled
                    ? biome.Weight(temperature, moisture, width)
                    : 0f;

                weights[i] = w;
                total += w;
            }

            if (total > 0.0001f)
            {
                for (int i = 0; i < weights.Length; i++) weights[i] /= total;
                return;
            }

            // No biome claims this climate, which means the biomes leave a gap in the
            // temperature/moisture space. Falling back to the nearest one keeps the
            // world continuous rather than leaving a region with no rules at all.
            int nearest = NearestBiome(temperature, moisture);
            for (int i = 0; i < weights.Length; i++) weights[i] = i == nearest ? 1f : 0f;
        }

        /// <summary>
        /// The biome whose climate region is closest, measured from its centre. Used
        /// where nothing claims a point outright.
        /// </summary>
        int NearestBiome(float temperature, float moisture)
        {
            int best = 0;
            float bestDistance = float.MaxValue;

            for (int i = 0; i < biomes.Length; i++)
            {
                var biome = biomes[i];
                if (biome == null || !biome.enabled) continue;

                float dt = temperature - biome.TemperatureCentre;
                float dm = moisture - biome.MoistureCentre;
                float distance = dt * dt + dm * dm;

                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = i;
                }
            }

            return best;
        }

        /// <summary>
        /// Index of the strongest biome at a position, for callers that need one answer
        /// rather than a blend.
        /// </summary>
        public int DominantBiome(float worldX, float worldZ, float heightMetres, float[] scratch)
        {
            SampleWeights(worldX, worldZ, heightMetres, scratch);

            int best = 0;
            float bestWeight = -1f;

            for (int i = 0; i < scratch.Length; i++)
            {
                if (scratch[i] > bestWeight)
                {
                    bestWeight = scratch[i];
                    best = i;
                }
            }

            return best;
        }

        /// <summary>
        /// A spread of biomes covering the climate space, so a new asset describes a
        /// plausible world rather than starting empty.
        /// </summary>
        static Biome[] DefaultBiomes()
        {
            return new[]
            {
                new Biome("Tundra",    new Vector2(0.00f, 0.25f), new Vector2(0.00f, 0.55f),
                          new Color(0.70f, 0.73f, 0.72f)),
                new Biome("Taiga",     new Vector2(0.00f, 0.32f), new Vector2(0.55f, 1.00f),
                          new Color(0.28f, 0.42f, 0.34f)),
                new Biome("Grassland", new Vector2(0.32f, 0.68f), new Vector2(0.00f, 0.45f),
                          new Color(0.62f, 0.68f, 0.34f)),
                new Biome("Forest",    new Vector2(0.32f, 0.72f), new Vector2(0.45f, 1.00f),
                          new Color(0.30f, 0.52f, 0.28f)),
                new Biome("Desert",    new Vector2(0.68f, 1.00f), new Vector2(0.00f, 0.35f),
                          new Color(0.84f, 0.74f, 0.48f)),
                new Biome("Savanna",   new Vector2(0.68f, 1.00f), new Vector2(0.35f, 0.70f),
                          new Color(0.76f, 0.70f, 0.38f)),
                new Biome("Jungle",    new Vector2(0.72f, 1.00f), new Vector2(0.70f, 1.00f),
                          new Color(0.20f, 0.46f, 0.22f)),
            };
        }
    }
}
