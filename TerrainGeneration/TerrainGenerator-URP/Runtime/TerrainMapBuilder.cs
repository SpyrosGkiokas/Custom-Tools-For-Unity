using UnityEngine;

namespace CustomTools.TerrainGenerationURP
{
    /// <summary>
    /// Drop this on a GameObject to generate a Unity Terrain from a settings asset.
    /// Works in the editor (via the Generate button) and at runtime (via <see cref="Build"/>).
    /// </summary>
    [AddComponentMenu("Custom Tools/Terrain Map Builder (URP)")]
    public class TerrainMapBuilder : MonoBehaviour
    {
        [Header("Recipe")]
        [Tooltip("The settings asset that describes this terrain.")]
        public TerrainMapSettings settings;

        [Header("Target")]
        [Tooltip("Terrain to write into. Left empty, one is created as a child on first build.")]
        public Terrain targetTerrain;

        [Tooltip("Material for the terrain. Leave empty to generate a URP Terrain Lit " +
                 "material automatically; assign your own to control its settings.")]
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

        /// <summary>
        /// Water flow from the last build's erosion pass, 0..1 per cell, or null when
        /// erosion is disabled. Useful as a mask for river beds and wet ground.
        /// </summary>
        public float[,] LastFlow { get; private set; }

        /// <summary>
        /// Raised during generation with 0..1 progress. Return false to cancel.
        /// The editor hooks this to show a progress bar; game code can ignore it.
        /// </summary>
        public System.Func<float, bool> ProgressHandler { get; set; }

        bool OnGenerationProgress(float progress)
        {
            return ProgressHandler == null || ProgressHandler(progress);
        }

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

            var heights = TerrainMapGenerator.GenerateHeights(settings, out var flow, OnGenerationProgress);
            LastHeights = heights;
            LastFlow = flow;

            var terrain = EnsureTerrain();
            var data = terrain.terrainData;

            int res = settings.Resolution;
            data.heightmapResolution = res;
            data.size = new Vector3(settings.worldSize, settings.worldHeight, settings.worldSize);
            data.SetHeights(0, 0, heights);

            ApplyLayers(data, heights);

            terrain.heightmapPixelError = pixelError;
            ApplyMaterial(terrain);

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

            // A Terrain with no layers at all renders as nothing, not even a default
            // grey, which reads as "the tool is broken". Give it something visible.
            if (rules == null || rules.Length == 0)
            {
                data.terrainLayers = new[] { GetPlaceholderLayer() };
                return;
            }

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
                    "have no TerrainLayer assigned, so the terrain is showing a placeholder " +
                    "texture instead. Assign a TerrainLayer to every rule to texture it properly.",
                    this);
                data.terrainLayers = new[] { GetPlaceholderLayer() };
                return;
            }

            data.terrainLayers = layers.ToArray();

            int alphaRes = Mathf.Max(16, Mathf.ClosestPowerOfTwo(alphamapResolution));
            data.alphamapResolution = alphaRes;

            var splat = TerrainMapGenerator.GenerateSplatmap(settings, heights, alphaRes);
            if (splat != null) data.SetAlphamaps(0, 0, splat);
        }

        /// <summary>
        /// Assigns the terrain material. Falls back to building a URP Terrain Lit
        /// material in code, since a terrain with no suitable material renders magenta.
        /// </summary>
        void ApplyMaterial(Terrain terrain)
        {
            if (terrainMaterial != null)
            {
                terrain.materialTemplate = terrainMaterial;
                return;
            }

            // Unity's default terrain material is built for the Built-in pipeline,
            // so under URP it renders magenta until a Terrain Lit material is set.
            if (_fallbackMaterial == null)
            {
                var shader = Shader.Find("Universal Render Pipeline/Terrain/Lit");
                if (shader == null)
                {
                    Debug.LogWarning(
                        $"{nameof(TerrainMapBuilder)} on '{name}': could not find the " +
                        "'Universal Render Pipeline/Terrain/Lit' shader, so the terrain may render magenta. " +
                        "Create a material using that shader and assign it to Terrain Material.",
                        this);
                    return;
                }

                _fallbackMaterial = new Material(shader) { name = "Terrain Material (generated)" };
            }

            terrain.materialTemplate = _fallbackMaterial;
        }

        /// <summary>
        /// A built-in checkered layer used when no usable rules exist, so the terrain is
        /// always visible. Deliberately obvious: it reads as a placeholder rather than
        /// looking like an intended material, and the grid shows shape and scale.
        /// </summary>
        TerrainLayer GetPlaceholderLayer()
        {
            if (_placeholderLayer != null) return _placeholderLayer;

            const int size = 128;
            const int checkerSize = 16;

            var tex = new Texture2D(size, size)
            {
                name = "Placeholder Terrain Texture",
                wrapMode = TextureWrapMode.Repeat
            };

            var light = new Color(0.62f, 0.64f, 0.60f);
            var dark = new Color(0.48f, 0.50f, 0.47f);

            var pixels = new Color[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    bool even = ((x / checkerSize) + (y / checkerSize)) % 2 == 0;
                    pixels[y * size + x] = even ? light : dark;
                }
            }

            tex.SetPixels(pixels);
            tex.Apply();

            _placeholderLayer = new TerrainLayer
            {
                name = "Placeholder Layer",
                diffuseTexture = tex,
                tileSize = new Vector2(16f, 16f)
            };

            return _placeholderLayer;
        }

        // Cached so repeated Build() calls don't leak a texture or material each time.
        TerrainLayer _placeholderLayer;
        Material _fallbackMaterial;
    }
}
