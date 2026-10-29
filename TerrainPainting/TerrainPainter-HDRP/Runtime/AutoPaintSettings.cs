using UnityEngine;

namespace CustomTools.TerrainPainting
{
    /// <summary>
    /// A reusable set of paint rules. Held as an asset rather than on the component so
    /// one look can be applied to several terrains, and so rule sets can be saved and
    /// swapped as presets.
    /// </summary>
    [CreateAssetMenu(
        menuName = "Custom Tools/Terrain Auto Paint Settings",
        fileName = "AutoPaintSettings")]
    public class AutoPaintSettings : ScriptableObject
    {
        [Tooltip("Rules are evaluated independently; where several match, their weights blend.")]
        public PaintRule[] rules = DefaultRules();

        [Tooltip(
            "Resolution of the painted splatmap. Higher resolves finer transitions but " +
            "costs memory on the terrain asset itself.")]
        public int alphamapResolution = 512;

        [Tooltip(
            "Smooths the finished weights to soften any remaining hard edges. " +
            "0 applies the rules exactly as written.")]
        [Range(0, 4)] public int smoothing = 0;

        [Header("Macro Variation")]

        [Tooltip(
            "Varies which layers dominate across the terrain, so the same rule does not " +
            "paint identically from one side of the map to the other.")]
        public bool macroVariation = false;

        [Tooltip(
            "Size of the variation in metres. This should be large - a few hundred metres " +
            "or more - since the point is variation across the landscape rather than " +
            "within a single view.")]
        [Min(10f)] public float macroSize = 600f;

        [Tooltip("How strongly the variation shifts the balance between layers.")]
        [Range(0f, 1f)] public float macroStrength = 0.35f;

        /// <summary>
        /// Rules for a plausible temperate terrain, so a fresh asset paints something
        /// recognisable rather than one flat layer. The textures are left unassigned:
        /// only the author knows which they have.
        /// </summary>
        static PaintRule[] DefaultRules()
        {
            return new[]
            {
                new PaintRule
                {
                    // The base: whatever the rules below do not claim ends up as grass,
                    // which is the usual assumption for temperate ground.
                    name = "Grass",
                    isBaseLayer = true,
                    heightRange = new Vector2(0f, 0.55f),
                    slopeRange = new Vector2(0f, 25f),
                    blend = 0.2f
                },
                new PaintRule
                {
                    name = "Rock (steep)",
                    useHeight = false,
                    slopeRange = new Vector2(32f, 90f),
                    blend = 0.15f
                },
                new PaintRule
                {
                    name = "Snow (peaks)",
                    heightRange = new Vector2(0.72f, 1f),
                    slopeRange = new Vector2(0f, 45f),
                    blend = 0.25f
                },
                new PaintRule
                {
                    // The layer that height and slope alone could not express: wet
                    // ground follows the drainage pattern, not an elevation band.
                    name = "Riverbed (wet)",
                    useHeight = false,
                    useSlope = true,
                    slopeRange = new Vector2(0f, 20f),
                    useFlow = true,
                    flowRange = new Vector2(0.55f, 1f),
                    blend = 0.12f
                }
            };
        }

        /// <summary>Alphamap resolution clamped to what a TerrainData will accept.</summary>
        public int ResolvedAlphamapResolution =>
            Mathf.Clamp(Mathf.ClosestPowerOfTwo(Mathf.Max(16, alphamapResolution)), 16, 2048);

        /// <summary>Rules that will contribute, in the order they are evaluated.</summary>
        public int ActiveRuleCount
        {
            get
            {
                if (rules == null) return 0;

                int n = 0;
                foreach (var rule in rules)
                    if (rule != null && rule.enabled) n++;
                return n;
            }
        }
    }
}
