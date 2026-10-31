using UnityEngine;

namespace CustomTools.TerrainBiomesURP
{
    /// <summary>
    /// One kind of place, defined by the climate it occupies rather than by where it
    /// sits on the map. Nothing here knows about terrain, position or tiles: a biome
    /// says "I am what cold wet ground looks like", and the map decides where that is.
    ///
    /// That separation is what lets the same biome set describe a 4km test map and a
    /// 20km world, and what lets the painter and the scatter tool agree without
    /// either of them owning the definition.
    /// </summary>
    [System.Serializable]
    public class Biome
    {
        [Tooltip("Name for this biome in the Inspector and previews.")]
        public string name = "Biome";

        [Tooltip("Include this biome. Off leaves it in the list without using it.")]
        public bool enabled = true;

        [Tooltip("Temperature band this biome occupies. 0 is coldest, 1 is hottest.")]
        public Vector2 temperature = new Vector2(0f, 1f);

        [Tooltip("Moisture band this biome occupies. 0 is driest, 1 is wettest.")]
        public Vector2 moisture = new Vector2(0f, 1f);

        [Tooltip(
            "Colour identifying this biome in previews. Does not affect the terrain - " +
            "what a biome looks like comes from the rules the tools attach to it.")]
        public Color previewColor = Color.grey;

        [Tooltip(
            "Weight of this biome relative to the others where their climates overlap. " +
            "Raising it lets a biome win ground it would otherwise share.")]
        [Range(0.1f, 4f)] public float dominance = 1f;

        public Biome() { }

        public Biome(string name, Vector2 temperature, Vector2 moisture, Color previewColor)
        {
            this.name = name;
            this.temperature = temperature;
            this.moisture = moisture;
            this.previewColor = previewColor;
        }

        /// <summary>Middle of this biome's temperature band.</summary>
        public float TemperatureCentre => (Mathf.Min(temperature.x, temperature.y)
                                         + Mathf.Max(temperature.x, temperature.y)) * 0.5f;

        /// <summary>Middle of this biome's moisture band.</summary>
        public float MoistureCentre => (Mathf.Min(moisture.x, moisture.y)
                                      + Mathf.Max(moisture.x, moisture.y)) * 0.5f;

        /// <summary>
        /// How strongly this biome claims a climate, before being normalised against
        /// the others. Zero means it does not apply here at all.
        /// </summary>
        public float Weight(float temperatureValue, float moistureValue, float blend)
        {
            if (!enabled) return 0f;

            float t = Band(temperatureValue, temperature, blend);
            if (t <= 0f) return 0f;

            float m = Band(moistureValue, moisture, blend);
            if (m <= 0f) return 0f;

            return t * m * dominance;
        }

        /// <summary>
        /// 1 inside the band, easing to 0 across <paramref name="blend"/> either side.
        ///
        /// The falloff is smoothstepped rather than linear because these weights are
        /// normalised against each other: a linear ramp leaves a visible crease where
        /// two biomes cross at equal weight, while an eased one meets flat.
        /// </summary>
        static float Band(float value, Vector2 range, float blend)
        {
            float min = Mathf.Min(range.x, range.y);
            float max = Mathf.Max(range.x, range.y);

            if (value >= min && value <= max) return 1f;
            if (blend <= 0f) return 0f;

            float distance = value < min ? min - value : value - max;
            if (distance >= blend) return 0f;

            return Mathf.SmoothStep(1f, 0f, distance / blend);
        }
    }
}
