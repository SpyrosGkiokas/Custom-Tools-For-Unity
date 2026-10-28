using UnityEngine;

namespace CustomTools.TerrainGeneration
{
    /// <summary>Which noise shape drives the base elevation.</summary>
    public enum NoiseType
    {
        /// <summary>Classic fractal Brownian motion. Rolling hills.</summary>
        FBM,
        /// <summary>Absolute-value noise. Sharp mountain ridges.</summary>
        Ridged,
        /// <summary>Inverted ridges. Carved canyons and plateaus.</summary>
        Billow
    }

    /// <summary>
    /// Stateless noise sampling. Everything here is deterministic for a given seed,
    /// so the same settings always rebuild the same terrain.
    /// </summary>
    public static class Noise
    {
        /// <summary>
        /// Samples layered noise at a point in normalized terrain space (0..1).
        /// Returns roughly 0..1, unclamped so callers can shape it further.
        /// </summary>
        public static float Sample(float x, float y, NoiseSettings s, Vector2[] octaveOffsets)
        {
            float amplitude = 1f;
            float frequency = 1f;
            float value = 0f;
            float amplitudeSum = 0f;

            int octaves = Mathf.Min(s.octaves, octaveOffsets.Length);
            for (int i = 0; i < octaves; i++)
            {
                float sx = x * s.scale * frequency + octaveOffsets[i].x;
                float sy = y * s.scale * frequency + octaveOffsets[i].y;

                // Perlin is 0..1; remap to -1..1 so ridged/billow have a real centre.
                float n = Mathf.PerlinNoise(sx, sy) * 2f - 1f;

                switch (s.noiseType)
                {
                    case NoiseType.Ridged:
                        n = 1f - Mathf.Abs(n);
                        n = n * n;          // sharpen the crests
                        n = n * 2f - 1f;
                        break;
                    case NoiseType.Billow:
                        n = Mathf.Abs(n) * 2f - 1f;
                        break;
                }

                value += n * amplitude;
                amplitudeSum += amplitude;

                amplitude *= s.persistence;
                frequency *= s.lacunarity;
            }

            if (amplitudeSum > 0f) value /= amplitudeSum;
            return value * 0.5f + 0.5f;
        }

        /// <summary>
        /// Builds per-octave offsets from the seed. Offsetting each octave separately
        /// stops them lining up into visible grid artifacts.
        /// </summary>
        public static Vector2[] BuildOctaveOffsets(int seed, int octaves, Vector2 offset)
        {
            var prng = new System.Random(seed);
            var offsets = new Vector2[Mathf.Max(1, octaves)];
            for (int i = 0; i < offsets.Length; i++)
            {
                // Keep well inside Perlin's precision-friendly range.
                offsets[i] = new Vector2(
                    prng.Next(-100000, 100000) * 0.0001f + offset.x,
                    prng.Next(-100000, 100000) * 0.0001f + offset.y);
            }
            return offsets;
        }
    }

    /// <summary>Noise shape parameters, shared by the asset and the runtime generator.</summary>
    [System.Serializable]
    public class NoiseSettings
    {
        public NoiseType noiseType = NoiseType.FBM;

        [Tooltip("Zoom. Lower = larger, smoother features.")]
        [Min(0.01f)] public float scale = 3f;

        [Tooltip("How many layers of detail are stacked.")]
        [Range(1, 10)] public int octaves = 5;

        [Tooltip("How fast each octave loses strength. Lower = smoother.")]
        [Range(0f, 1f)] public float persistence = 0.5f;

        [Tooltip("How fast each octave gains frequency. Higher = more fine detail.")]
        [Range(1f, 4f)] public float lacunarity = 2f;

        [Tooltip("Pans the noise field without changing its shape.")]
        public Vector2 offset = Vector2.zero;
    }
}
