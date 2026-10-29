# Terrain Auto Painter (URP)

Rule-based terrain texturing for Unity URP. Describe where each layer belongs — by
height, steepness, shape and drainage — and paint the whole terrain in one click, with a
live preview of both the result and the inputs behind it.

Works on **any** terrain: sculpted by hand, imported from a heightmap, or generated. It
reads the heights already in the `TerrainData` and never changes them.

```
Runtime/   ships in builds — terrain analysis, rules, settings asset, painter component
Editor/    editor-only — live preview, channel views, Paint button
```

The two assembly definitions keep `Editor/` out of your builds automatically.

---

## Install

1. Copy the `TerrainPainter-URP` folder into your project's `Assets/`.
2. Wait for Unity to compile. No packages to install beyond URP itself.

## Your first paint

1. **Assets > Create > Custom Tools > Terrain Auto Paint Settings** — this asset is one
   texturing recipe. Duplicate it to keep variants.
2. The asset starts with four rules: grass, steep rock, snow on the peaks, and a wet
   riverbed. Assign a **Terrain Layer** to each one. Layers are ordinary Unity assets
   (**Create > Terrain Layer**) and carry the textures themselves.
3. Add a **Terrain Auto Painter** component to your `Terrain` GameObject and assign the
   settings asset.
4. The Inspector shows a **live preview**. Tune the rules until it looks right, then hit
   **Paint Terrain**.

Painting replaces the terrain's layer list and splatmap, and is undoable.

## The four inputs

A rule can match on any combination of these. Each band can be switched off, so a rule
that only cares about steepness says exactly that.

**Height** — normalized 0 at the terrain's base to 1 at its maximum. The obvious one:
sand low, snow high.

**Slope** — in degrees, 0 flat to 90 vertical. The other obvious one: rock on anything too
steep to hold soil.

**Curvature** — whether the ground curves like a ridge or a hollow. Height and slope
together cannot express this: a ridge crest and a valley floor can sit at the same
elevation and both be flat. Use it for scree in gullies, or bare rock on exposed crests.

**Flow** — how much of the map drains through each point. This is what lets a riverbed
exist as a layer at all. A damp channel and the dry ground beside it are the same height
and the same angle; what differs is the water running through. 0 is ground that sheds
water, 1 a channel that collects it.

Flow is computed from the heightmap by accumulation — every cell passes water downhill to
its lower neighbours, so a cell's total is the area draining through it. No erosion
simulation is needed, which is why it works on terrain this package did not create.

### Macro variation

Rules apply the same way everywhere, so a grass band covers a four-kilometre terrain
identically from one side to the other. That uniformity is what reads as generated, more
than any single transition does.

**Macro Variation** adds a very large noise field that shifts the balance between layers
across the map, so regions emerge where one layer leads and another recedes — lusher here,
barer there. **Region Size** should be large, a few hundred metres at least: the point is
variation across the landscape rather than within one view. The base layer is left out,
since varying the floor would reintroduce the gaps it exists to prevent.

### Noise masks

Bands alone produce clean contour lines, which read as a topographic map rather than
ground. Turn on **Noise Mask** for a layer and its edges break up. **Size** is in metres —
smaller is patchier — and **Seed** offsets the pattern so two layers using the same size
do not break up identically.

## Reading the preview

The toolbar switches what the preview shows. The channel views matter because the inputs
are not things you can picture from numbers: a curvature band of -0.3 to -0.1 means
nothing until you see which ground it covers.

| View | Shows |
|---|---|
| **Layers** | The painted result, each rule in its own colour |
| **Slope** | Dark where flat, bright where steep |
| **Curve** | Blue in valleys, orange on ridges, grey on flat ground |
| **Flow** | Blue where water drains and collects |
| **Solo** | One rule's weight alone, including partial coverage at its edges |

Colours in the Layers view identify rules; they are not the layer's textures. Use **Solo**
when a rule is not painting where you expected — it shows exactly what that rule claims,
before the others blend over it.

The preview analyses at 257px regardless of your terrain's heightmap resolution, since the
panel cannot resolve more than that. Painting uses the full heightmap.

## How weights combine

Every rule is evaluated independently, and the weights at each point are normalised to sum
to 1 — which is what Unity's terrain shader expects. So two rules that both match a point
share it, and **Strength** sets the ratio.

**Blend** softens every band edge on that rule at once. 0 gives a hard line.

### The base layer

One rule should be marked **Base Layer** — the first one is by default. A base layer
covers the whole terrain at a low weight, so anything the other rules do not claim still
has something to show. Its own bands still apply, giving it full weight where they match.

This is the usual way to think about ground: it is grass, then rock gets added on the
steep parts, then snow on the peaks. Without a base layer, any gap in the rules renders as
an untextured hole.

If no rule is marked as the base, the first layer fills unclaimed ground as a last resort.
That still avoids holes, but a visible patch of your first texture is then a gap in the
rules rather than a decision — check with the **Solo** view.

**Smoothing** (on the settings asset) blurs the finished weights, softening edges the
rules' own blending leaves behind. Costs a pass per step; 0 applies the rules exactly as
written.

## Performance

Analysis is one pass for slope and curvature, plus a sorted pass for flow accumulation.
Painting at 512 alphamap resolution against a 513px heightmap takes well under a second.

The preview debounces while you drag: nothing is recomputed mid-drag, and it rebuilds the
moment you release the slider.

## Limitations

- **Splatmaps only.** Detail/grass and tree scattering are not driven by these rules yet.
- **No biomes.** One rule set covers the whole terrain. Regional variation — desert here,
  alpine there — is not in this version.
- **Weight blending, not height blending.** Transitions crossfade between layers. Making
  gravel interlock through grass at an edge needs each layer's height texture and a custom
  shader.
- **Flow assumes the heightmap drains.** On a terrain of isolated basins, flow collects in
  each basin rather than forming a connected network, which is correct but may not be what
  you expect.

## URP setup

Terrain layers need a material that supports them. Assign a **Universal Render
Pipeline/Terrain/Lit** material to your Terrain's **Material** slot, and give each Terrain
Layer at least a Diffuse texture — a layer with no textures renders as flat grey.

The painter sets up layers and weights; everything about how those layers *look* lives on
the Terrain Layer assets and the terrain material.
