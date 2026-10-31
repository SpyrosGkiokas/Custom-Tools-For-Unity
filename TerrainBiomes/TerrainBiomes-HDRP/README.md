# Terrain Biomes (HDRP)

Divides a world into climate regions, so the other tools can ask "what kind of place is
this?" at any world position and get the same answer. A biome map on its own paints
nothing — it is the layer the [Terrain Auto Painter](../../TerrainPainting/TerrainPainter-HDRP/)
reads to paint desert here and alpine there instead of one rule set everywhere.

Climate is two noise fields, temperature and moisture, and each biome claims a rectangle
of that space. Sampling is in **world** coordinates, not per terrain, which is what makes
this usable across a tiled world: a 20km map is several Unity terrains, and a biome that
restarted at each tile boundary would draw the tile grid into the result.

```
Runtime/   ships in builds — biome definition, the climate map, world-space sampling
Editor/    editor-only — climate chart, world preview, coverage readout
```

The two assembly definitions keep `Editor/` out of your builds automatically.

---

## Install

1. Copy the `TerrainBiomes-HDRP` folder into your project's `Assets/`.
2. Wait for Unity to compile. No packages to install beyond HDRP itself.

If the Terrain Auto Painter is already in the project, biome support switches itself on —
see [How it connects to the painter](#how-it-connects-to-the-painter).

## Your first biome map

1. **Assets > Create > Custom Tools > Biome Map.** The asset starts with seven biomes
   covering the climate space — tundra, taiga, grassland, forest, desert, savanna,
   jungle — so it describes a plausible world before you touch anything.
2. Select it. The Inspector shows two previews: **World**, where the biomes actually
   land, and **Climate**, the temperature/moisture space itself.
3. Set **Climate Scale** to the size you want a region to be, in metres. This is the one
   setting that matters most, and the default of 4000 suits a large world.
4. Assign it to a **Terrain Auto Painter** and bind rules to biomes.

Nothing here writes to a terrain, so there is no apply step and nothing to undo.

## The two previews

Neither can be worked out from the numbers. A biome occupying 0.3–0.6 temperature means
nothing until you see how much of the world that covers.

| View | Shows |
|---|---|
| **World** | Where the biomes land, blended, over a square of world you choose |
| **Climate** | The temperature/moisture space, with each biome's claim in its colour |

**World** answers "how big are the regions, and do they read as places?" Use **Area
shown** to zoom out past the climate scale — at 20km across with a 4km scale you see
the pattern rather than one region filling the frame.

**Climate** answers "is my biome set complete?" Dark patches are climates no biome
claims. Those are not an error — the map falls back to the nearest biome by climate
centre, so the world stays continuous — but they mean a region of your world is getting
a biome chosen by proximity rather than by you.

Under both, the legend lists each biome with its **share of the world**. That percentage
is the diagnostic the pictures cannot give: a biome at `~0%` is defined but never occurs,
which is easy to miss by eye and is called out with a note when it happens.

## Climate

**Climate Scale** (metres) is the size of a region, and it wants to be large. A few
kilometres reads as different parts of a world; a few hundred metres reads as patches,
because you can see several biomes at once from anywhere you stand.

**Seed** changes the layout without changing its character — the same biomes, the same
proportions, somewhere else.

**Height Cooling** drops temperature with altitude, so mountains turn alpine on their
own rather than needing a biome placed on them by hand. **Cooling Height** is the
altitude in metres where the full drop has been applied. This is why the same latitude
can be tundra on a peak and forest in the valley below it.

Height cooling is the one input that is not purely horizontal, which is worth knowing
when reading the World preview: it samples at sea level, so a map relying heavily on
cooling will look warmer there than it paints on real terrain.

## Boundaries

A hard switch at a boundary is the failure everyone sees — grass becomes sand along a
line no landscape would draw. Two settings deal with it.

**Blend** is how wide a transition is, as a share of the climate space. Within it, both
biomes have weight, and consumers crossfade their own settings across it.

**Edge Noise Size** and **Edge Noise Strength** break the boundaries up so they do not
read as smooth contours. Size is in metres and wants to be far smaller than the climate
scale — a couple of hundred metres against a four-kilometre region. Strength is how far
the noise may push a boundary, as a share of the blend width, so a wider blend gives the
noise more room to work.

## A biome

| Setting | What it does |
|---|---|
| **Name** | Label in the Inspector, previews and the painter's biome picker |
| **Enabled** | Off leaves it in the list without using it |
| **Temperature** | The band it occupies, 0 coldest to 1 hottest |
| **Moisture** | The band it occupies, 0 driest to 1 wettest |
| **Preview Color** | Identifies it in the previews — it does **not** affect the terrain |
| **Dominance** | Its weight where climates overlap; raise it to win shared ground |

A biome is defined by the climate it occupies, not by where it sits on the map. Nothing
in a biome knows about terrain, position or tiles — it says "I am what cold wet ground
looks like", and the map decides where that is. That separation is what lets one biome
set describe both a 4km test map and a 20km world.

### Why temperature and moisture, rather than one value

A single noise value split into bands is simpler and gets adjacency wrong. Two fields put
desert next to savanna and tundra next to taiga, because those genuinely neighbour each
other in climate. With one band order, every neighbour is arbitrary — and neighbours are
most of what you notice in a finished world.

This is the Whittaker model, which is what ecology uses for the same reason.

## How weights combine

Every biome is evaluated independently and the weights at a position are normalised to
sum to 1, exactly as the painter does with its layer rules. So a position inside one
biome's bands returns 1 for it and 0 for the rest; a position in a transition returns a
share to each.

Weights rather than a single winner, because the blend is the whole point: a consumer
multiplying its own settings by these gets soft boundaries for free, and picking a winner
per position would throw that away.

`Dominance` scales a biome's weight before normalisation, which is how you resolve two
biomes claiming the same climate. It is also the usual reason a biome reads `~0%`: a
neighbour with higher dominance is taking the ground.

## How it connects to the painter

With both folders in the project, a **Biome Map** slot appears on the Terrain Auto
Painter, and each paint rule gains a **Limit To Biome** option under its Advanced
section. A rule limited to a biome paints only where that biome has weight; a rule left
unlimited paints everywhere, which is what a shared layer like cliff rock wants.

The rule's own height, slope, curvature and flow bands still apply within the biome. A
biome selects where a rule *may* paint — it does not paint on the rule's behalf.

**Edge Falloff** on a rule controls how sharply it gives way at a biome edge. 1 follows
the biome map's own blend. Higher values confine the rule closer to the biome's core,
which widens the band where the neighbouring biome's rules take over — useful when two
biomes' layers are similar enough that a long crossfade reads as mud.

The painter samples biomes at the terrain's **world position**, including its height, so
a biome region continues across adjacent terrains rather than restarting on each one.

### Setup is automatic

The painter ships with no reference to this tool, so it compiles whether or not you have
it — that is the point of the folders being independent. When this folder is present, an
editor script adds the assembly reference and defines `CUSTOMTOOLS_BIOMES`, which is what
makes the painter's biome fields appear. You will see one script recompile the first time.

If you later **delete** this folder, those two edits stay behind and the painter fails to
compile on a missing `BiomeMap`. The fix is to remove `CUSTOMTOOLS_BIOMES` from
**Project Settings > Player > Scripting Define Symbols**, and `CustomTools.TerrainBiomes`
from the painter's two `.asmdef` files.

## Sampling from script

The map is a plain `ScriptableObject`, usable from runtime code — scatter tools, spawn
tables, footstep audio, anything that wants to know where it is.

```csharp
using CustomTools.TerrainBiomes;

public class BiomeProbe : MonoBehaviour
{
    public BiomeMap map;

    void Start()
    {
        var p = transform.position;

        // Blended: a share per biome, summing to 1. Reuse the buffer - allocating
        // one per sample means a collection every few thousand points.
        var weights = new float[map.biomes.Length];
        map.SampleWeights(p.x, p.z, p.y, weights);

        // Or one answer, where a blend is not useful.
        int index = map.DominantBiome(p.x, p.z, p.y, weights);
        Debug.Log($"Standing in {map.biomes[index].name}");

        // Or the raw climate, to drive something of your own.
        map.SampleClimate(p.x, p.z, p.y, out float temperature, out float moisture);
    }
}
```

Sampling is a pure function of world position: the same coordinates always give the same
climate, with no setup call and no internal state. That is what lets separate terrain
tiles agree without talking to each other, and it means you can sample from a job or a
background thread as long as nothing is editing the asset at the time.

`SampleWeights` and `DominantBiome` both take a buffer you own, sized to
`biomes.Length`. `DominantBiome` uses it as scratch, so do not expect its contents to
survive the call.

## Performance

Sampling is two Perlin lookups for climate, two more when edge noise is on, and a band
test per biome. The 256px previews rebuild in a few milliseconds, and debounce while you
drag a slider — nothing is recomputed mid-drag, and it rebuilds the moment you release.

A full paint samples biomes once per alphamap cell, which at 512 resolution is 262,144
samples and lands well inside the painter's existing sub-second paint.

## Limitations

- **Noise only.** Climate comes from Perlin fields. Hand-painted biome masks, and
  latitude bands for a map with a north and a south, are not in this version.
- **The painter is the only consumer.** The generator does not read a biome map yet, so
  biomes vary texturing, not terrain shape — a desert region is not flatter than its
  alpine neighbour, only painted differently.
- **No per-biome scatter.** Detail meshes, grass and trees are not driven by biomes,
  because the painter does not drive them by rules either.
- **Rectangular climate claims.** A biome occupies a temperature/moisture box. Real
  climate envelopes are not boxes, and a diagonal boundary between two biomes needs
  dominance and blend to approximate it.
- **Cooling is uniform.** Height cooling applies the same everywhere; there is no
  prevailing wind, no rain shadow, so a mountain range does not dry the land behind it.

## HDRP setup

Nothing. This tool writes no terrain data and uses no materials or shaders — it answers
questions about world positions. Terrain material setup belongs to whichever tool does
the painting; for the auto painter, that is its
[HDRP setup](../../TerrainPainting/TerrainPainter-HDRP/README.md#hdrp-setup).
