using UnityEngine;

namespace CustomTools.TerrainPainting
{
    /// <summary>
    /// One layer and the conditions under which it paints. A rule claims the cells
    /// where every enabled band matches, and its weight falls off smoothly across the
    /// band edges so neighbouring rules blend instead of meeting at a seam.
    ///
    /// Each band can be switched off individually. A rule that only cares about slope
    /// should not have to span the full height range to say so: leaving height enabled
    /// but wide looks the same in the Inspector as a deliberate choice, and reads as
    /// one more thing to tune.
    /// </summary>
    [System.Serializable]
    public class PaintRule
    {
        [Tooltip("Label for this rule in the Inspector. Does not affect painting.")]
        public string name = "Layer";

        [Tooltip("The terrain layer to paint. Assign its textures on the layer asset itself.")]
        public TerrainLayer layer;

        [Tooltip("Paint this rule at all. Off keeps it in the list without affecting the result.")]
        public bool enabled = true;

        [Tooltip(
            "Cover the whole terrain, so anything the other rules do not claim shows " +
            "this layer. Its bands still apply where they match, giving it more weight there.")]
        public bool isBaseLayer = false;

        /// <summary>
        /// Weight a base layer holds on ground its own bands do not match. Low enough
        /// that any rule which does claim a cell wins it outright, but never zero: that
        /// is what stops a gap in the rules becoming an untextured hole.
        /// </summary>
        public const float BaseLayerFloor = 0.02f;

        // --- height ---------------------------------------------------------

        [Tooltip("Restrict this layer to a height band.")]
        public bool useHeight = true;

        [Tooltip("Normalized height band, 0 at the terrain's base and 1 at its maximum.")]
        public Vector2 heightRange = new Vector2(0f, 1f);

        // --- slope ----------------------------------------------------------

        [Tooltip("Restrict this layer to a slope band.")]
        public bool useSlope = true;

        [Tooltip("Slope band in degrees. 0 is flat ground, 90 a vertical face.")]
        public Vector2 slopeRange = new Vector2(0f, 90f);

        // --- curvature ------------------------------------------------------

        [Tooltip("Restrict this layer by shape: ridges, valleys, or flat ground.")]
        public bool useCurvature = false;

        [Tooltip("Curvature band. -1 is a valley or hollow, 0 flat, +1 a ridge or crest.")]
        public Vector2 curvatureRange = new Vector2(-1f, 1f);

        // --- flow -----------------------------------------------------------

        [Tooltip("Restrict this layer by how much water drains through, for riverbeds and damp ground.")]
        public bool useFlow = false;

        [Tooltip("Flow band. 0 is dry ground that sheds water, 1 a channel that collects it.")]
        public Vector2 flowRange = new Vector2(0f, 1f);

        // --- biome ----------------------------------------------------------

        [Tooltip(
            "Restrict this layer to one biome from the painter's Biome Map. Off paints " +
            "the rule in every biome, which is what a shared layer like rock wants.")]
        public bool useBiome = false;

        [Tooltip(
            "Index of the biome in the Biome Map's list. The painter's Inspector shows " +
            "these by name; this is the index so a rule set does not break when a biome " +
            "is renamed.")]
        [Min(0)] public int biomeIndex = 0;

        [Tooltip(
            "How sharply this rule gives way at a biome edge. 1 follows the Biome Map's " +
            "own boundary softness, higher values confine the rule closer to the biome's " +
            "core, leaving a wider band where the neighbouring biome's rules take over.")]
        [Range(1f, 4f)] public float biomeFalloff = 1f;

        // --- shaping --------------------------------------------------------

        [Tooltip("Softness of every band edge. 0 gives a hard line, higher values a wider fade.")]
        [Range(0f, 1f)] public float blend = 0.15f;

        [Tooltip("Overall weight of this layer where its bands match.")]
        [Range(0f, 1f)] public float strength = 1f;

        // --- noise mask -----------------------------------------------------

        [Tooltip("Break up the band edges with noise, so a transition is not a clean contour line.")]
        public bool useNoiseMask = false;

        [Tooltip("Size of the noise features in metres. Smaller values give a finer, patchier break-up.")]
        [Min(1f)] public float noiseSize = 40f;

        [Tooltip("How much the noise can cut into this layer. 0 is no effect, 1 removes it entirely in places.")]
        [Range(0f, 1f)] public float noiseStrength = 0.5f;

        [Tooltip("Offsets this layer's noise so two layers using the same size do not break up identically.")]
        public int noiseSeed = 0;

        /// <summary>
        /// Weight of this rule at one analysed point, before normalisation against the
        /// other rules. Zero means the rule does not claim this cell at all.
        /// </summary>
        public float Evaluate(float height, float slopeDeg, float curvature, float flow)
        {
            if (!enabled || strength <= 0f) return 0f;

            // A base layer falls back to a floor rather than zero wherever its bands do
            // not match, so ground no rule claims still has a layer to show. Its bands
            // are still evaluated: a base layer is the bottom of the stack, not a rule
            // exempt from its own settings.
            float floor = isBaseLayer ? BaseLayerFloor * strength : 0f;

            float weight = strength;

            // Each band multiplies in, and an early exit on zero skips the rest: most
            // cells fail the first band they are tested against, and this runs once
            // per rule per alphamap cell - 512² cells against eight rules is 2M calls.
            if (useHeight)
            {
                weight *= Band(height, heightRange, blend);
                if (weight <= 0f) return floor;
            }

            if (useSlope)
            {
                // The blend is a 0..1 softness control, so scale it into degrees to
                // mean the same proportion of feathering as the other bands.
                weight *= Band(slopeDeg, slopeRange, blend * 90f);
                if (weight <= 0f) return floor;
            }

            if (useCurvature)
            {
                // Curvature runs -1..1, so its blend covers twice the span of height's.
                weight *= Band(curvature, curvatureRange, blend * 2f);
                if (weight <= 0f) return floor;
            }

            if (useFlow)
            {
                weight *= Band(flow, flowRange, blend);
                if (weight <= 0f) return floor;
            }

            return Mathf.Max(weight, floor);
        }

        /// <summary>
        /// 1 inside the band, feathering to 0 across <paramref name="blend"/> either
        /// side. The range is read min-first so dragging a Vector2 field's handles past
        /// each other does not silently zero the rule.
        /// </summary>
        static float Band(float value, Vector2 range, float blend)
        {
            float min = Mathf.Min(range.x, range.y);
            float max = Mathf.Max(range.x, range.y);

            if (blend <= 0f) return (value >= min && value <= max) ? 1f : 0f;

            if (value < min) return Mathf.Clamp01(1f - (min - value) / blend);
            if (value > max) return Mathf.Clamp01(1f - (value - max) / blend);
            return 1f;
        }

        /// <summary>
        /// This rule's share of a cell given the biome weights there. Returns 1 when the
        /// rule is not bound to a biome, so callers need no special case.
        ///
        /// Taking the biome's own weight rather than testing the dominant biome is what
        /// keeps boundaries soft: the Biome Map already blends its biomes across a
        /// transition, so a rule multiplied by that weight fades out exactly where the
        /// neighbouring biome's rules fade in. Picking a winner per cell would throw
        /// that away and draw the biome edge as a hard line through the terrain.
        /// </summary>
        /// <param name="biomeWeights">
        /// Normalised weights, one per biome in the map, or null when no map is in use.
        /// </param>
        public float BiomeMask(float[] biomeWeights)
        {
            if (!useBiome || biomeWeights == null) return 1f;
            if (biomeIndex < 0 || biomeIndex >= biomeWeights.Length) return 1f;

            float w = biomeWeights[biomeIndex];
            if (w <= 0f) return 0f;

            // The falloff is a power rather than a remapped band: weights are already
            // normalised, so raising one to a power narrows where it leads without
            // needing to know what the others are doing.
            return biomeFalloff > 1f ? Mathf.Pow(w, biomeFalloff) : w;
        }

        /// <summary>
        /// Noise multiplier for this rule at a world position. Returns 1 when the mask
        /// is off, so callers need no special case.
        /// </summary>
        public float NoiseMask(float worldX, float worldZ)
        {
            if (!useNoiseMask || noiseStrength <= 0f) return 1f;

            // Offsetting by seed rather than reseeding keeps this a pure function of
            // position: the same rule paints the same pattern every time it runs.
            float n = Mathf.PerlinNoise(
                (worldX / noiseSize) + noiseSeed * 13.7f,
                (worldZ / noiseSize) + noiseSeed * 7.3f);

            return Mathf.Lerp(1f, n, noiseStrength);
        }
    }
}
