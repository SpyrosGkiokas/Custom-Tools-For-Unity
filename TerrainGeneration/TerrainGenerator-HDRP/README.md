# Terrain Map Generator (HDRP)

Procedural heightmap terrain for Unity HDRP. Design a map in the Inspector with a live
preview, then generate it as a real Unity `Terrain` — or build it at runtime from a seed
for per-run maps.

```
Runtime/   ships in builds — noise, settings asset, generator, scene component
Editor/    editor-only — live preview, Generate buttons, TerrainData saving
```

The two assembly definitions keep `Editor/` out of your builds automatically.

---

## Install

1. Copy the `TerrainGenerator-HDRP` folder into your project's `Assets/`.
2. Wait for Unity to compile. No packages to install beyond HDRP itself.

## Your first terrain

1. **Assets > Create > Custom Tools > Terrain Map Settings** — this asset is one terrain
   recipe. Duplicate it later to keep presets (Island, Mountains, Plains…).
2. Select the asset. The Inspector shows a **live preview** that refreshes as you drag
   sliders. Hit **New Seed** until you see a shape you like.
3. Click **Create Terrain In Scene**. You now have a `Terrain` GameObject.
4. Assign a material and layers for texturing — see [HDRP setup](#hdrp-setup) next.

### HDRP setup

Two things are easy to miss:

**The material.** HDRP terrain needs an `HDRP/TerrainLit` shader or it renders magenta.
Create a material (**Assets > Create > Material**), set its shader to **HDRP/TerrainLit**,
then assign it to **Terrain Material** on the builder component.

**The layers.** Each layer rule needs a `TerrainLayer` asset
(**Assets > Create > Terrain Layer**) with at least a Diffuse texture. Rules with no layer
assigned are skipped with a console warning rather than mispainting the terrain, so
texturing is all-or-nothing — assign every one.

### Save your terrain before recompiling

A terrain generated in the editor keeps its `TerrainData` **in memory only**. A script
recompile or scene reload will drop it and leave you with a blank terrain.

The builder's Inspector warns you about this and offers **Save TerrainData As Asset…**.
Use it once you're happy with a map. Terrain you generate at runtime doesn't need this.

---

## Settings reference

### Seed & size

| Field | What it does |
|---|---|
| **Seed** | Same seed always rebuilds the same terrain. |
| **Resolution** | Heightmap detail. Unity requires 2ⁿ+1, hence the fixed list. 513 is a good default; 1025+ costs real memory. |
| **World Size** | Footprint in metres. |
| **World Height** | Metres at full white. Raise for dramatic peaks, lower for rolling country. |

### Shape

| Field | What it does |
|---|---|
| **Noise Type** | `FBM` rolling hills · `Ridged` sharp mountain ridges · `Billow` canyons and plateaus |
| **Scale** | Zoom. **Lower = larger, smoother features.** The first dial to reach for. |
| **Octaves** | Layers of detail. 4–6 is usually plenty; more mostly costs time. |
| **Persistence** | How fast octaves lose strength. Lower = smoother. |
| **Lacunarity** | How fast octaves gain frequency. Higher = more fine detail. |
| **Offset** | Pans the noise field without changing its character. |
| **Height Curve** | Remaps elevation. X = raw noise, Y = final height. Flatten the left side for plains and lake beds; steepen the right for cliffs. |
| **Normalize Heights** | Stretches output to the full 0–1 range. **Leave this on** — layered noise naturally clusters mid-range and reads as gentle swells without it. Turn it off only for tiled worlds (see [caveats](#caveats)). |

### Edges

**Falloff Mode** shapes the map border:

- `None` — terrain runs to the edge
- `Island` — square falloff; water at every border
- `Radial` — round island
- `Basin` — inverted; raised rim, sunken middle (craters, valleys)

**Falloff Strength** sets how far in from the edge the effect reaches;
**Falloff Sharpness** sets how hard the land-to-water transition is.

### Water

**Sea Level** is the normalized height treated as water. It drives the preview colors and
gives your layer rules a reference point — it does not create a water plane. Add your own
mesh or HDRP water surface at `worldHeight * seaLevel`.

### Layer rules

Each rule paints one `TerrainLayer` into a height and slope band. Rules are applied in
order and **blend where bands overlap**, so overlap is a feature, not a mistake.

| Field | What it does |
|---|---|
| **Layer** | The `TerrainLayer` to paint. Needs a Diffuse texture. |
| **Height Range** | Normalized 0–1 band. Compare against Sea Level to place beaches. |
| **Slope Range** | Degrees. 0 = flat, 90 = vertical. This is how you get rock on cliffs. |
| **Blend** | Softness of the band edges. 0 gives a hard seam. |
| **Strength** | Overall weight of this layer. |

A sane starting set — note how the bands overlap at their edges so transitions feather:

| Name | Height | Slope | Why |
|---|---|---|---|
| Sand | 0.00 – 0.35 | 0 – 30 | Shoreline, flat ground only |
| Grass | 0.30 – 0.70 | 0 – 35 | Habitable middle |
| Rock | 0.00 – 1.00 | 30 – 90 | Any steep face, at any elevation |
| Snow | 0.75 – 1.00 | 0 – 40 | Peaks, but not sheer cliffs |

Slope rules hold their meaning when you resize the world: the generator converts heights
to metres before measuring angles, so doubling both World Size and World Height leaves
your texturing unchanged.

---

## Runtime use

Add a **Custom Tools > Terrain Map Builder** component, assign a settings asset, and
either tick **Generate On Awake** or drive it yourself:

```csharp
using CustomTools.TerrainGeneration;

public class MapSpawner : MonoBehaviour
{
    [SerializeField] TerrainMapBuilder builder;

    void Start()
    {
        // Fresh map every run.
        builder.Build(Random.Range(int.MinValue, int.MaxValue));

        // Or reproduce a specific map — share this seed to share the map.
        // builder.Build(20261028);
    }
}
```

Place objects on the generated surface:

```csharp
float y = builder.SampleHeight(x, z);
Instantiate(treePrefab, new Vector3(x, y, z), Quaternion.identity);
```

`builder.LastHeights` holds the raw `float[,]` heightmap (normalized 0–1, indexed
`[y, x]`) if you want to make your own decisions from it — spawn density, biome lookup,
navmesh hints.

### Generating without a scene Terrain

`TerrainMapGenerator` is static and touches nothing in the scene, so you can use it for
minimaps, gameplay logic, or validation before committing to a terrain:

```csharp
float[,] heights = TerrainMapGenerator.GenerateHeights(settings);
Texture2D minimap = TerrainMapGenerator.GeneratePreview(settings, heights);
```

This is the same code path the editor preview and the scene builder both use, so what you
inspect is what you get.

---

## Tuning tips

- **Terrain too noisy?** Lower Scale first, then drop Persistence.
- **Too flat or samey?** Raise World Height. Check Normalize Heights is on.
- **Want mountain ranges, not lumps?** `Ridged` noise, Scale around 1.5–2.5.
- **Want plains with occasional hills?** `FBM` with a Height Curve that stays flat across
  the left two-thirds, then rises steeply.
- **Preview feels slow?** Turn off **Auto refresh** and use the **Refresh** button. The
  preview always renders at 257px regardless of your resolution setting, so the real
  terrain carries more detail than you see.

## Caveats

- **Normalize Heights breaks tiling.** It scales each map by its own min/max, so two
  separately generated neighbours won't meet at the seam. For a tiled or infinite world,
  turn it off and use a fixed Height Curve instead.
- **Generation is synchronous.** A 1025² map takes a noticeable moment; 2049² is slower
  still. Fine for level load or an editor click, but don't call `Build()` mid-gameplay at
  high resolutions without hiding it behind a loading screen.
- **`Build(seed)` writes to the settings asset.** It sets `settings.seed` before
  generating. In the editor that dirties the asset — expected, but worth knowing if you
  have it open in version control.

## Using URP instead

Everything in `Runtime/` is renderer-agnostic — only the material changes. A
[URP version](../TerrainGenerator-URP/) is already included in this repo, so use that rather than porting
by hand.

Both folders can live in the same project: their assemblies, namespaces and menu entries
are named differently (`TerrainGeneration` vs `TerrainGenerationURP`), so they will not
collide.
