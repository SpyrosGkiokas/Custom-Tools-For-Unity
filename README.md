# Custom Tools For Unity

A growing collection of drop-in tools for Unity. No packages, no dependencies — copy a
folder into your project and it works.

Every tool ships in both **HDRP** and **URP** flavours, and separates runtime code from
editor code so nothing editor-only ends up in your builds.

---

## Why this exists

I work on Unity projects alone. A team splits the work that does not really need a person
doing it — placing rocks down a hillside, deciding which slopes get grass and which get
scree — but on your own, that work still has to happen, and it comes out of the time you
wanted to spend on the parts only you can do.

So I automate it. Each tool here gets built when I hit the wall it solves, and there are
plenty of walls left — terrain was simply the first. What they have in common is being the
form I wanted and could not find: no package manager, no dependency tree, no
half-abandoned asset store plugin that breaks on the next Unity version. Copy a folder,
get on with it.

They are MIT because they are more useful to me working than sitting in a private repo.
If you fix something or add a tool, that is a problem neither of us has to solve twice.

---

## Tools

### 🏔 Terrain Generation

Procedural heightmap terrain with a live Inspector preview. Design a map by dragging
sliders, then generate it as a real Unity `Terrain` — or build it at runtime from a seed
for per-run maps.

- **A six-stage realism pipeline** — domain warping, hydraulic erosion, thermal talus
  relief, river channels, strata and context-driven micro-relief, each reading the
  surface the last one left
- Three noise shapes — rolling hills (fBm), sharp mountain ridges, canyons and plateaus
- Island, radial and basin edge falloff for maps that end in water or a crater rim
- Automatic texturing by height **and slope**, so cliffs get rock and peaks get snow
- Seeded and deterministic — share a seed to share the exact map
- Live Inspector preview — a shaded 2D map, or an orbitable 3D mesh, refreshing as you tune

<p align="center">
  <img width="420" alt="Orbiting the 3D terrain preview in the Unity Inspector"
       src="https://github.com/user-attachments/assets/6a7f27ab-ec4d-49c8-a430-e071f60a8308" />
  <img width="420" alt="Tuning sea level with the preview updating live"
       src="https://github.com/user-attachments/assets/f1dfede2-2f14-49a1-bb59-5b489cb7a102" />
</p>

<p align="center">
  <em>Left: the 3D preview, orbited with the mouse. Right: dragging a slider re-runs the
  pipeline and redraws the preview, so terrain is dialled in without touching the scene.</em>
</p>

→ **[HDRP version](TerrainGeneration/TerrainGenerator-HDRP/)** · **[URP version](TerrainGeneration/TerrainGenerator-URP/)**

### 🎨 Terrain Auto Painter

Rule-based texturing for **any** terrain — sculpted, imported or generated. Describe where
each layer belongs and paint the whole thing in one click.

- **Four inputs, not two** — height and slope, plus curvature to tell a ridge from a
  valley, and drainage to find the ground water actually runs through
- Riverbeds and damp hollows as real layers — the places height and slope alone cannot
  describe, since a wet channel and the dry ground beside it are the same height and angle
- Per-layer noise masks, so a transition breaks up instead of reading as a contour line
- Live Inspector preview of the result **and** of each input on its own — tuning a
  curvature band is guesswork until you can see the curvature
- Reads heights from the terrain and never changes them

<p align="center">
  <img width="380" alt="Tuning paint rules with the live preview updating"
       src="https://github.com/user-attachments/assets/c3876b32-c83d-40fb-8a7d-e7223ecae699" />
</p>

<p align="center">
  <em>Rules are tuned against a live preview, which also shows each input on its own —
  slope, curvature and drainage — so it is clear why a layer landed where it did.</em>
</p>

→ **[HDRP version](TerrainPainting/TerrainPainter-HDRP/)** · **[URP version](TerrainPainting/TerrainPainter-URP/)**

---

## Getting started

1. Pick a tool and a pipeline above.
2. Copy that folder into your project's `Assets/`.
3. Follow the README inside it.

Each tool's own README covers setup, every setting, and runtime usage.

## Requirements

- **Unity 2021.3 LTS or newer** — uses assembly definitions and `TerrainLayer`, both long
  stable. Newer versions are fine.
- **HDRP or URP** installed, matching whichever folder you copy. The generation code
  itself is renderer-agnostic; only the terrain material differs between them.

## Which pipeline am I on?

**Edit > Project Settings > Graphics** and look at the Render Pipeline Asset. If it names
`HDRenderPipelineAsset` you want the `-HDRP` folder; if it names
`UniversalRenderPipelineAsset` you want the `-URP` one. Empty means you're on the Built-in
pipeline, where neither folder's material will work as-is.

---

## Contributing

Issues and pull requests are welcome — bug reports, new tools, or pipeline fixes.

If you're adding a tool, the convention here is:

```
ToolName/
  ToolName-HDRP/
    Runtime/   ships in builds, with a .asmdef
    Editor/    editor-only, .asmdef restricted to the Editor platform
    README.md  setup, settings reference, runtime usage
  ToolName-URP/
    …same shape
```

Keeping runtime and editor code in separate assemblies is the part that matters — it's
what stops editor code from being compiled into a player build.

## License

MIT — use it in commercial projects, modify it, ship it. No attribution required, though
it's always appreciated.

See [LICENSE](LICENSE) for the full text.
