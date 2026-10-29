using UnityEngine;

namespace CustomTools.TerrainPainting
{
    /// <summary>Which map the preview draws.</summary>
    public enum PreviewChannel
    {
        /// <summary>The painted result, each layer in its own colour.</summary>
        Layers,
        /// <summary>Slope in degrees, dark where flat and bright where steep.</summary>
        Slope,
        /// <summary>Curvature: valleys one colour, ridges the other.</summary>
        Curvature,
        /// <summary>Flow accumulation, showing where water drains.</summary>
        Flow,
        /// <summary>One rule's weight alone, to see exactly where it claims.</summary>
        SoloLayer
    }

    /// <summary>
    /// Renders previews of the paint inputs and result to a texture.
    ///
    /// The channel views exist because the inputs are not things you can picture from
    /// numbers: a curvature band of -0.3 to -0.1 means nothing until you see which
    /// parts of the terrain it covers. Tuning the four inputs blind is guesswork, so
    /// each is inspectable on its own.
    /// </summary>
    public static class PaintPreview
    {
        /// <summary>
        /// Distinct hues for the layer view. These identify a layer, they do not
        /// represent its texture: the point is telling regions apart, and inventing a
        /// plausible grass-green would imply the preview knows what the layer looks like.
        /// </summary>
        static readonly Color[] LayerColors =
        {
            new Color(0.42f, 0.62f, 0.30f),
            new Color(0.58f, 0.52f, 0.46f),
            new Color(0.90f, 0.92f, 0.95f),
            new Color(0.35f, 0.52f, 0.68f),
            new Color(0.78f, 0.68f, 0.42f),
            new Color(0.52f, 0.38f, 0.56f),
            new Color(0.72f, 0.45f, 0.35f),
            new Color(0.40f, 0.65f, 0.62f)
        };

        /// <summary>
        /// Average colours sampled from each layer's texture. Sampling reads back from
        /// the GPU, which is far too slow to do per preview rebuild, and a terrain
        /// layer's texture rarely changes.
        ///
        /// Keyed by the texture object itself rather than an id: Unity 6 deprecated
        /// GetInstanceID in favour of GetEntityId, and keying on the reference avoids
        /// having to pick between them across versions.
        /// </summary>
        static readonly System.Collections.Generic.Dictionary<Texture, Color> SampledColors =
            new System.Collections.Generic.Dictionary<Texture, Color>();

        /// <summary>
        /// Drops the sampled colour cache, so an edited or reassigned texture is picked
        /// up rather than kept from a previous read.
        /// </summary>
        public static void ClearSampledColors() => SampledColors.Clear();

        /// <summary>
        /// Average colour of a layer's diffuse texture, for a preview that approximates
        /// the painted result rather than colour-coding it.
        ///
        /// Falls back to the palette when there is no texture to read: a rule with no
        /// layer assigned still has to appear as something, and a rule that paints
        /// nothing visible would be the most confusing possible answer.
        /// </summary>
        public static Color SampledLayerColor(TerrainLayer layer, int index)
        {
            var texture = layer != null ? layer.diffuseTexture : null;
            if (texture == null) return LayerColor(index);

            if (SampledColors.TryGetValue(texture, out var cached)) return cached;

            var average = AverageColor(texture) ?? LayerColor(index);
            SampledColors[texture] = average;
            return average;
        }

        /// <summary>
        /// Reads a texture's average colour by scaling it to a single pixel on the GPU.
        ///
        /// Going through a RenderTexture rather than GetPixels is what lets this work on
        /// any texture: GetPixels needs Read/Write Enabled, which is an import setting
        /// per asset, and a preview that silently failed on textures nobody had prepared
        /// would be worse than one that never offered this at all.
        /// </summary>
        static Color? AverageColor(Texture texture)
        {
            RenderTexture rt = null;
            RenderTexture previous = RenderTexture.active;
            Texture2D readback = null;

            try
            {
                // Blitting straight to 1x1 lets the GPU's own filtering do the
                // averaging, in one step rather than a mip chain walked by hand.
                rt = RenderTexture.GetTemporary(1, 1, 0, RenderTextureFormat.ARGB32);
                rt.filterMode = FilterMode.Bilinear;

                Graphics.Blit(texture, rt);

                RenderTexture.active = rt;
                readback = new Texture2D(1, 1, TextureFormat.RGBA32, false);
                readback.ReadPixels(new Rect(0f, 0f, 1f, 1f), 0, 0);
                readback.Apply(false);

                var c = readback.GetPixel(0, 0);

                // Lift very dark or washed-out samples: a texture that averages to near
                // black leaves the hillshade with nothing to shade, and the region reads
                // as a hole rather than a layer.
                Color.RGBToHSV(c, out float h, out float s, out float v);
                return Color.HSVToRGB(h, Mathf.Clamp(s, 0.08f, 0.9f), Mathf.Clamp(v, 0.25f, 0.95f));
            }
            catch (System.Exception)
            {
                // Compressed formats a blit cannot handle, and textures still streaming
                // in, both land here. The palette is a perfectly good answer.
                return null;
            }
            finally
            {
                RenderTexture.active = previous;
                if (rt != null) RenderTexture.ReleaseTemporary(rt);
                if (readback != null) Object.DestroyImmediate(readback);
            }
        }

        /// <summary>Colour used to identify layer <paramref name="index"/>.</summary>
        public static Color LayerColor(int index) =>
            LayerColors[Mathf.Abs(index) % LayerColors.Length];

        /// <summary>
        /// Draws a channel into a texture, reusing <paramref name="existing"/> when it
        /// is already the right size. Editor previews rebuild often, and discarding a
        /// Texture2D per rebuild leaks until the next GC.
        /// </summary>
        public static Texture2D Render(
            TerrainAnalysis analysis,
            float[,,] splat,
            PreviewChannel channel,
            int soloLayer,
            int resolution,
            Texture2D existing = null,
            Color[] layerColors = null)
        {
            if (analysis == null) return existing;

            int res = Mathf.Max(2, resolution);
            var tex = existing;

            if (tex == null || tex.width != res || tex.height != res)
            {
                if (tex != null) Object.DestroyImmediate(tex);
                tex = new Texture2D(res, res, TextureFormat.RGBA32, false)
                {
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp
                };
            }

            var pixels = new Color32[res * res];

            for (int y = 0; y < res; y++)
            {
                float ny = (float)y / (res - 1);

                for (int x = 0; x < res; x++)
                {
                    float nx = (float)x / (res - 1);

                    // Height is not read here: form comes from the hillshade below,
                    // which takes its own gradient rather than the elevation alone.
                    analysis.Sample(nx, ny, out _, out float slope, out float curvature, out float flow);

                    Color c = channel switch
                    {
                        PreviewChannel.Slope => SlopeColor(slope),
                        PreviewChannel.Curvature => CurvatureColor(curvature),
                        PreviewChannel.Flow => FlowColor(flow),
                        PreviewChannel.SoloLayer => SoloColor(splat, nx, ny, soloLayer, layerColors),
                        _ => LayersColor(splat, nx, ny, layerColors)
                    };

                    // Light the surface rather than shading it by height. Brightness by
                    // elevation alone leaves ridges and valleys invisible - the whole
                    // map reads as one flat wash - and since it can only darken, it
                    // drains the colours as well. A hillshade shows the form the rules
                    // are responding to, which is what makes the result readable.
                    float shade = Hillshade(analysis, nx, ny);
                    c.r *= shade;
                    c.g *= shade;
                    c.b *= shade;

                    // Texture rows run bottom-up, matching the heightmap's indexing.
                    pixels[y * res + x] = c;
                }
            }

            tex.SetPixels32(pixels);
            tex.Apply(false);
            return tex;
        }

        /// <summary>
        /// Directional shading from the surface normal, lit from the north-west at a
        /// shallow angle. North-west is the convention for relief maps and reads as
        /// raised rather than sunken to most people; a shallow angle exaggerates the
        /// relief, which is what makes subtle form visible at preview scale.
        /// </summary>
        static float Hillshade(TerrainAnalysis analysis, float nx, float ny)
        {
            // One cell at the analysis resolution, so the gradient is taken at the
            // finest scale the preview actually has.
            float step = 1f / (analysis.Resolution - 1);

            float hL = TerrainAnalysis.Bilinear(analysis.Height, nx - step, ny);
            float hR = TerrainAnalysis.Bilinear(analysis.Height, nx + step, ny);
            float hD = TerrainAnalysis.Bilinear(analysis.Height, nx, ny - step);
            float hU = TerrainAnalysis.Bilinear(analysis.Height, nx, ny + step);

            // Metres per cell, so the normal is in real proportions rather than
            // depending on the heightmap's resolution.
            float metresPerStep = step * analysis.WorldSize;

            float dzdx = (hR - hL) * analysis.WorldHeight / (2f * metresPerStep);
            float dzdy = (hU - hD) * analysis.WorldHeight / (2f * metresPerStep);

            var normal = new Vector3(-dzdx, 1f, -dzdy).normalized;

            // Light from the north-west, 45 degrees up.
            var light = new Vector3(-0.5f, 0.7f, 0.5f).normalized;

            float lambert = Mathf.Clamp01(Vector3.Dot(normal, light));

            // Lifted off zero so a face turned fully away still shows its colour: this
            // is a readability aid, not a render, and a black slope hides the rule that
            // painted it.
            return 0.45f + 0.75f * lambert;
        }

        /// <summary>Blends the layer colours by weight, giving the painted result.</summary>
        /// <summary>
        /// Share of the terrain each layer holds, summing to 1 across all layers.
        ///
        /// This is the diagnostic the colours cannot give: a layer reading 0% is not
        /// painting at all, and a layer at 90% is swamping the rest. Either is obvious
        /// as a number and easy to miss by eye, particularly when two layers sample to
        /// similar colours.
        /// </summary>
        public static float[] Coverage(float[,,] splat)
        {
            if (splat == null) return null;

            int res = splat.GetLength(0);
            int count = splat.GetLength(2);
            var totals = new float[count];

            for (int y = 0; y < res; y++)
                for (int x = 0; x < res; x++)
                    for (int i = 0; i < count; i++)
                        totals[i] += splat[y, x, i];

            // Weights already sum to 1 per cell, so the grand total is the cell count.
            float cells = res * res;
            if (cells <= 0f) return totals;

            for (int i = 0; i < count; i++) totals[i] /= cells;
            return totals;
        }

        /// <summary>Colour for layer i: the resolved set when given, the palette otherwise.</summary>
        static Color Resolve(Color[] colors, int index) =>
            colors != null && index < colors.Length ? colors[index] : LayerColor(index);

        static Color LayersColor(float[,,] splat, float nx, float ny, Color[] colors)
        {
            if (splat == null) return new Color(0.5f, 0.5f, 0.5f);

            int res = splat.GetLength(0);
            int count = splat.GetLength(2);

            // Nearest sample: the splat is already at preview resolution or finer, and
            // interpolating weights here would soften edges the rules deliberately made.
            int x = Mathf.Clamp(Mathf.RoundToInt(nx * (res - 1)), 0, res - 1);
            int y = Mathf.Clamp(Mathf.RoundToInt(ny * (res - 1)), 0, res - 1);

            var c = new Color(0f, 0f, 0f);
            for (int i = 0; i < count; i++)
            {
                float w = splat[y, x, i];
                if (w <= 0f) continue;

                var lc = Resolve(colors, i);
                c.r += lc.r * w;
                c.g += lc.g * w;
                c.b += lc.b * w;
            }

            return c;
        }

        /// <summary>
        /// One layer's weight in its own colour against a dark background, so partial
        /// coverage at a band edge is visible rather than being hidden by the layers
        /// blended over it.
        /// </summary>
        static Color SoloColor(float[,,] splat, float nx, float ny, int layer, Color[] colors)
        {
            if (splat == null) return new Color(0.2f, 0.2f, 0.2f);

            int res = splat.GetLength(0);
            int count = splat.GetLength(2);
            if (layer < 0 || layer >= count) return new Color(0.2f, 0.2f, 0.2f);

            int x = Mathf.Clamp(Mathf.RoundToInt(nx * (res - 1)), 0, res - 1);
            int y = Mathf.Clamp(Mathf.RoundToInt(ny * (res - 1)), 0, res - 1);

            float w = splat[y, x, layer];
            var lc = Resolve(colors, layer);
            var background = new Color(0.14f, 0.14f, 0.16f);

            return Color.Lerp(background, lc, w);
        }

        /// <summary>
        /// Slope as brightness, banded at the angles that matter for painting so the
        /// preview reads as thresholds rather than a smooth gradient.
        /// </summary>
        static Color SlopeColor(float slopeDeg)
        {
            float t = Mathf.Clamp01(slopeDeg / 70f);
            return Color.Lerp(new Color(0.15f, 0.18f, 0.22f), new Color(1f, 0.85f, 0.55f), t);
        }

        /// <summary>
        /// Curvature diverging about zero: blue in valleys, grey on flat ground, warm
        /// on ridges. Flat ground dominates most terrain, so the midpoint is kept
        /// neutral and only genuine concavity or convexity takes on colour.
        /// </summary>
        static Color CurvatureColor(float curvature)
        {
            var flat = new Color(0.42f, 0.42f, 0.44f);

            if (curvature < 0f)
                return Color.Lerp(flat, new Color(0.25f, 0.45f, 0.80f), Mathf.Clamp01(-curvature));

            return Color.Lerp(flat, new Color(0.85f, 0.55f, 0.30f), Mathf.Clamp01(curvature));
        }

        /// <summary>
        /// Flow as water over dry ground. The analysis already log-scales flow, so a
        /// linear ramp here shows the low end where damp hollows sit.
        /// </summary>
        static Color FlowColor(float flow)
        {
            var dry = new Color(0.30f, 0.26f, 0.22f);
            var wet = new Color(0.35f, 0.70f, 0.95f);
            return Color.Lerp(dry, wet, Mathf.Clamp01(flow));
        }
    }
}
