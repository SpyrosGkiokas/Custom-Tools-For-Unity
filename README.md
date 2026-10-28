# Custom Tools For Unity

A growing collection of drop-in tools for Unity. No packages, no dependencies — copy a
folder into your project and it works.

Every tool ships in both **HDRP** and **URP** flavours, and separates runtime code from
editor code so nothing editor-only ends up in your builds.

---

## Tools

### 🏔 Terrain Generation

Procedural heightmap terrain with a live Inspector preview. Design a map by dragging
sliders, then generate it as a real Unity `Terrain` — or build it at runtime from a seed
for per-run maps.

- Three noise shapes — rolling hills (fBm), sharp mountain ridges, canyons and plateaus
- Island, radial and basin edge falloff for maps that end in water or a crater rim
- Automatic texturing by height **and slope**, so cliffs get rock and peaks get snow
- Seeded and deterministic — share a seed to share the exact map
- Live preview with hillshading that refreshes as you tune

→ **[HDRP version](terrain_generation/hdrp/)** · **[URP version](terrain_generation/urp/)**

<!-- Add a screenshot or GIF of the Inspector preview here — it sells this tool better than any description. -->

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
`HDRenderPipelineAsset` you want `hdrp/`; if it names `UniversalRenderPipelineAsset` you
want `urp/`. Empty means you're on the Built-in pipeline, where neither folder's material
will work as-is.

---

## Contributing

Issues and pull requests are welcome — bug reports, new tools, or pipeline fixes.

If you're adding a tool, the convention here is:

```
tool_name/
  hdrp/
    Runtime/   ships in builds, with a .asmdef
    Editor/    editor-only, .asmdef restricted to the Editor platform
    README.md  setup, settings reference, runtime usage
  urp/
    …same shape
```

Keeping runtime and editor code in separate assemblies is the part that matters — it's
what stops editor code from being compiled into a player build.

## License

MIT — use it in commercial projects, modify it, ship it. No attribution required, though
it's always appreciated.

See [LICENSE](LICENSE) for the full text.
