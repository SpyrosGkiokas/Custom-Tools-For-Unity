using UnityEngine;

namespace CustomTools.TerrainGeneration
{
    /// <summary>
    /// Drop this on a GameObject to generate a Unity Terrain from a settings asset.
    /// Works in the editor (via the Generate button) and at runtime (via <see cref="Build"/>).
    /// </summary>
    [AddComponentMenu("Custom Tools/Terrain Map Builder")]
    public class TerrainMapBuilder : MonoBehaviour
    {
        [Header("Recipe")]
        [Tooltip("The settings asset that describes this terrain.")]
        public TerrainMapSettings settings;

        [Header("Target")]
        [Tooltip("Terrain to write into. Left empty, one is created as a child on first build.")]
        public Terrain targetTerrain;

        [Tooltip("Material for the terrain. Use an HDRP/TerrainLit material.")]
        public Material terrainMaterial;

        [Header("Runtime")]
        [Tooltip("Build on Awake. Leave off if game code drives the build itself.")]
        public bool generateOnAwake = false;

        [Tooltip("Pick a fresh random seed on each Awake build instead of using the asset's seed.")]
        public bool randomizeSeedOnAwake = false;

        [Header("Quality")]
        [Tooltip("Splatmap resolution. Lower costs less memory than the heightmap.")]
        public int alphamapResolution = 512;

        [Tooltip("Terrain mesh detail. Lower draws faster.")]
        [Range(1, 200)] public int pixelError = 5;

        /// <summary>The heightmap from the last build. Useful for placing objects on the surface.</summary>
        public float[,] LastHeights { get; private set; }

        void Awake()
        {
            if (generateOnAwake) Build();
        }

        /// <summary>Generates the terrain from the current settings.</summary>
        public void Build()
        {
            if (settings == null)
            {
                Debug.LogError($"{nameof(TerrainMapBuilder)} on '{name}' has no settings asset assigned.", this);
                return;
            }

            if (randomizeSeedOnAwake && Application.isPlaying)
                settings.seed = Random.Range(int.MinValue, int.MaxValue);

            var heights = TerrainMapGenerator.GenerateHeights(settings);
            LastHeights = heights;

            var terrain = EnsureTerrain();
            var data = terrain.terrainData;

            int res = settings.Resolution;
            data.heightmapResolution = res;
            data.size = new Vector3(settings.worldSize, settings.worldHeight, settings.worldSize);
            data.SetHeights(0, 0, heights);

            ApplyLayers(data, heights);

            terrain.heightmapPixelError = pixelError;
            if (terrainMaterial != null) terrain.materialTemplate = terrainMaterial;

            terrain.Flush();
        }

        /// <summary>Generates with an explicit seed. Convenient for per-run maps.</summary>
        public void Build(int seed)
        {
            if (settings == null)
            {
                Debug.LogError($"{nameof(TerrainMapBuilder)} on '{name}' has no settings asset assigned.", this);
                return;
            }

            settings.seed = seed;
            Build();
        }

        /// <summary>
        /// World-space height of the terrain surface at a world X/Z position.
        /// Falls back to the object's own Y before the first build.
        /// </summary>
        public float SampleHeight(float worldX, float worldZ)
        {
            if (targetTerrain == null) return transform.position.y;
            return targetTerrain.SampleHeight(new Vector3(worldX, 0f, worldZ))
                   + targetTerrain.transform.position.y;
        }

        Terrain EnsureTerrain()
        {
            if (targetTerrain != null)
            {
                // A Terrain can share its TerrainData asset with other terrains or scenes;
                // writing into a shared asset would silently edit those too.
                if (targetTerrain.terrainData == null)
                    targetTerrain.terrainData = NewTerrainData();

                var collider = targetTerrain.GetComponent<TerrainCollider>();
                if (collider != null) collider.terrainData = targetTerrain.terrainData;

                return targetTerrain;
            }

            var go = new GameObject("Generated Terrain");
            go.transform.SetParent(transform, false);

            var terrain = go.AddComponent<Terrain>();
            terrain.terrainData = NewTerrainData();

            var col = go.AddComponent<TerrainCollider>();
            col.terrainData = terrain.terrainData;

            targetTerrain = terrain;
            return terrain;
        }

        TerrainData NewTerrainData()
        {
            return new TerrainData
            {
                name = $"{(settings != null ? settings.name : "Terrain")}_Data",
                heightmapResolution = settings != null ? settings.Resolution : 513
            };
        }

        void ApplyLayers(TerrainData data, float[,] heights)
        {
            var rules = settings.layerRules;
            if (rules == null || rules.Length == 0) return;

            // A null layer would offset every later index, mispainting the whole terrain.
            var layers = new System.Collections.Generic.List<TerrainLayer>(rules.Length);
            foreach (var rule in rules)
            {
                if (rule != null && rule.layer != null) layers.Add(rule.layer);
            }

            if (layers.Count != rules.Length)
            {
                Debug.LogWarning(
                    $"{nameof(TerrainMapBuilder)} on '{name}': {rules.Length - layers.Count} layer rule(s) " +
                    "have no TerrainLayer assigned, so texturing was skipped.", this);
                return;
            }

            data.terrainLayers = layers.ToArray();

            int alphaRes = Mathf.Max(16, Mathf.ClosestPowerOfTwo(alphamapResolution));
            data.alphamapResolution = alphaRes;

            var splat = TerrainMapGenerator.GenerateSplatmap(settings, heights, alphaRes);
            if (splat != null) data.SetAlphamaps(0, 0, splat);
        }
    }
}
