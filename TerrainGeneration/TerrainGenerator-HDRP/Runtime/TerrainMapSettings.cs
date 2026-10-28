using UnityEngine;

namespace CustomTools.TerrainGeneration
{
    /// <summary>How the map edges are treated.</summary>
    public enum FalloffMode
    {
        /// <summary>No shaping. Terrain runs to the edge.</summary>
        None,
        /// <summary>Square falloff. Land in the middle, water at every border. Island.</summary>
        Island,
        /// <summary>Radial falloff. Round island.</summary>
        Radial,
        /// <summary>Inverted island: raised rim, basin in the middle. Crater / valley.</summary>
        Basin
    }

    /// <summary>
    /// One terrain recipe. Duplicate the asset to keep presets
    /// (Assets > Create > Custom Tools > Terrain Map Settings).
    /// </summary>
    [CreateAssetMenu(
        fileName = "TerrainMapSettings",
        menuName = "Custom Tools/Terrain Map Settings",
        order = 0)]
    public class TerrainMapSettings : ScriptableObject
    {
        [Header("Seed")]
        public int seed = 0;

        [Tooltip("Resolution of the heightmap. Unity requires 2^n + 1.")]
        public TerrainResolution resolution = TerrainResolution._513;

        [Header("World Size")]
        [Tooltip("Terrain footprint in metres.")]
        [Min(1f)] public float worldSize = 1000f;

        [Tooltip("Height of a fully white heightmap sample, in metres.")]
        [Min(1f)] public float worldHeight = 300f;

        [Header("Shape")]
        public NoiseSettings noise = new NoiseSettings();

        [Tooltip("Remaps raw noise into a final elevation profile. " +
                 "X = noise 0..1, Y = height 0..1. Flatten the left side for plains and lakes.")]
        public AnimationCurve heightCurve = AnimationCurve.Linear(0f, 0f, 1f, 1f);

        [Tooltip("Stretches the generated heights to use the full 0..1 range. " +
                 "Layered noise naturally clusters around the middle, which reads as " +
                 "gentle swells; this restores dramatic relief. Note that it makes the " +
                 "result depend on the map's own extremes, so neighbouring tiles " +
                 "generated separately will not line up.")]
        public bool normalizeHeights = true;

        [Header("Erosion")]
        [Tooltip("Hydraulic erosion. Carves drainage networks and settles sediment, " +
                 "which is what makes terrain read as weathered rather than as noise.")]
        public ErosionSettings erosion = new ErosionSettings();

        [Tooltip("Thermal erosion. Relaxes slopes to an angle rock can actually hold " +
                 "and piles debris at the base of cliffs.")]
        public ThermalSettings thermal = new ThermalSettings();

        [Header("Detail")]
        [Tooltip("Micro-relief that varies by slope and wetness instead of being " +
                 "uniform, which is the usual giveaway of generated terrain.")]
        public DetailSettings detail = new DetailSettings();

        [Tooltip("Carves river channels along the water flow erosion produced.")]
        public RiverSettings rivers = new RiverSettings();

        [Tooltip("Sedimentary banding. A stylistic pass rather than a realism one.")]
        public StrataSettings strata = new StrataSettings();

        [Header("Edges")]
        public FalloffMode falloffMode = FalloffMode.None;

        [Tooltip("How far in from the edge the falloff reaches.")]
        [Range(0.1f, 5f)] public float falloffStrength = 3f;

        [Tooltip("How hard the land-to-edge transition is.")]
        [Range(0.1f, 10f)] public float falloffSharpness = 2.2f;

        [Header("Water")]
        [Tooltip("Normalized height treated as sea level. Used for layer rules and the preview.")]
        [Range(0f, 1f)] public float seaLevel = 0.3f;

        [Header("Terrain Layers")]
        [Tooltip("Splatmap rules, painted in order. Later entries draw over earlier ones.")]
        public TerrainLayerRule[] layerRules = new TerrainLayerRule[0];

        /// <summary>Heightmap resolution. Unity's TerrainData only accepts 2^n + 1.</summary>
        public enum TerrainResolution
        {
            _65 = 65,
            _129 = 129,
            _257 = 257,
            _513 = 513,
            _1025 = 1025,
            _2049 = 2049
        }

        public int Resolution => (int)resolution;

        void OnValidate()
        {
            // An empty curve silently zeroes the whole terrain; that reads as a broken tool.
            if (heightCurve == null || heightCurve.length == 0)
                heightCurve = AnimationCurve.Linear(0f, 0f, 1f, 1f);
        }
    }

    /// <summary>
    /// Assigns one TerrainLayer to a height and slope band.
    /// Weights blend where bands overlap, so order matters.
    /// </summary>
    [System.Serializable]
    public class TerrainLayerRule
    {
        public string name = "Layer";

        [Tooltip("The HDRP terrain layer to paint. Needs a Diffuse texture assigned.")]
        public TerrainLayer layer;

        [Tooltip("Normalized height band this layer covers.")]
        public Vector2 heightRange = new Vector2(0f, 1f);

        [Tooltip("Slope band in degrees. 0 = flat ground, 90 = vertical cliff.")]
        public Vector2 slopeRange = new Vector2(0f, 90f);

        [Tooltip("Softness of the band edges. 0 = hard seam.")]
        [Range(0f, 0.5f)] public float blend = 0.1f;

        [Tooltip("Overall strength of this layer.")]
        [Range(0f, 1f)] public float strength = 1f;
    }
}
