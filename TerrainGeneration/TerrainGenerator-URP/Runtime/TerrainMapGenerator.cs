using UnityEngine;

namespace CustomTools.TerrainGenerationURP
{
    /// <summary>
    /// Builds heightmaps and splatmaps from a <see cref="TerrainMapSettings"/> asset.
    /// Pure data in, pure data out: nothing here touches the scene, so the editor tool
    /// and runtime code share the same path and always agree.
    /// </summary>
    public static class TerrainMapGenerator
    {
        /// <summary>
        /// Generates a normalized heightmap. Indexed [y, x] to match
        /// <see cref="TerrainData.SetHeights"/>.
        /// </summary>
        public static float[,] GenerateHeights(TerrainMapSettings settings)
        {
            return GenerateHeights(settings, out _, null);
        }

        /// <summary>
        /// Generates a normalized heightmap, also returning the water flow map that
        /// erosion produced and reporting progress while it runs.
        /// </summary>
        /// <param name="flow">
        /// Receives a 0..1 map of how much water crossed each cell, or null when
        /// erosion is disabled. Useful for texturing river beds.
        /// </param>
        /// <param name="onProgress">
        /// Progress callback for the erosion pass, which is the slow part. Return false
        /// to cancel. Ignored when erosion is disabled.
        /// </param>
        public static float[,] GenerateHeights(
            TerrainMapSettings settings,
            out float[,] flow,
            System.Func<float, bool> onProgress)
        {
            int res = settings.Resolution;
            var heights = new float[res, res];

            var offsets = Noise.BuildOctaveOffsets(settings.seed, settings.noise.octaves, settings.noise.offset);

            // AnimationCurve.Evaluate is not thread-safe and allocates while sampling;
            // bake it to a lookup table once instead of calling it res*res times.
            var curveLut = BakeCurve(settings.heightCurve, 512);

            // Pass 1: raw noise. Track the extremes in case we normalize.
            float min = float.MaxValue;
            float max = float.MinValue;

            for (int y = 0; y < res; y++)
            {
                float ny = (float)y / (res - 1);
                for (int x = 0; x < res; x++)
                {
                    float nx = (float)x / (res - 1);

                    float h = Noise.Sample(nx, ny, settings.noise, offsets);
                    heights[y, x] = h;

                    if (h < min) min = h;
                    if (h > max) max = h;
                }
            }

            // Pass 2: stretch, shape the edges, then remap through the curve.
            // Normalizing first means the curve always works across the full 0..1 range.
            bool stretch = settings.normalizeHeights && max - min > 0.0001f;

            for (int y = 0; y < res; y++)
            {
                float ny = (float)y / (res - 1);
                for (int x = 0; x < res; x++)
                {
                    float nx = (float)x / (res - 1);

                    float h = heights[y, x];
                    if (stretch) h = (h - min) / (max - min);

                    h = ApplyFalloff(h, nx, ny, settings);
                    h = SampleLut(curveLut, Mathf.Clamp01(h));

                    heights[y, x] = Mathf.Clamp01(h);
                }
            }

            // Pass 3 onward: the weathering and detail stages, in an order that mirrors
            // how real terrain forms. Each reads the surface the previous one left, so
            // reordering them changes the result — water carves the shaped landscape,
            // gravity then settles what water exposed, and detail goes on last so it is
            // not smoothed away by the simulations.
            flow = null;

            bool erode = settings.erosion != null && settings.erosion.enabled && settings.erosion.iterations > 0;
            bool thermal = settings.thermal != null && settings.thermal.enabled && settings.thermal.iterations > 0;

            // Progress is shared across stages so the bar reflects the whole job rather
            // than restarting per stage.
            float stages = (erode ? 1f : 0f) + (thermal ? 1f : 0f);
            float stageIndex = 0f;

            System.Func<float, bool> StageProgress(float index) =>
                onProgress == null ? (System.Func<float, bool>)null
                                   : p => onProgress((index + p) / Mathf.Max(1f, stages));

            if (erode)
            {
                flow = new float[res, res];
                Erosion.Apply(heights, settings.erosion, settings.seed, flow, StageProgress(stageIndex));
                stageIndex++;

                // Deposited sediment can mound a cell above 1.0, which TerrainData
                // rejects. Clamp rather than renormalize: rescaling the whole map
                // would undo the erosion's carefully carved relative depths.
                ClampAll(heights, res);
            }

            if (thermal)
            {
                // After hydraulic erosion, not before: water exposes the steep faces
                // that then shed debris, and running it first would smooth away the
                // slopes water needs in order to cut channels at all.
                ThermalErosion.Apply(
                    heights, settings.thermal, settings.worldSize, settings.worldHeight,
                    StageProgress(stageIndex));
                stageIndex++;
            }

            // Rivers before detail: the channel is terrain shape, and the detail pass
            // reads the flow map to keep river beds smooth, so it needs the channel to
            // already be cut.
            TerrainDetail.ApplyRiverChannels(heights, flow, settings.rivers);

            TerrainDetail.ApplyStrata(heights, settings.strata);

            // Detail last, so neither simulation smooths it away.
            TerrainDetail.ApplyContextDetail(
                heights, flow, settings.detail, settings.seed,
                settings.worldSize, settings.worldHeight);

            return heights;
        }

        static void ClampAll(float[,] heights, int res)
        {
            for (int y = 0; y < res; y++)
                for (int x = 0; x < res; x++)
                    heights[y, x] = Mathf.Clamp01(heights[y, x]);
        }

        /// <summary>
        /// Generates splatmap weights as [y, x, layer], ready for
        /// <see cref="TerrainData.SetAlphamaps"/>. Returns null when no usable rules exist.
        /// </summary>
        public static float[,,] GenerateSplatmap(
            TerrainMapSettings settings, float[,] heights, int alphamapResolution)
        {
            var rules = settings.layerRules;
            if (rules == null || rules.Length == 0) return null;

            int res = alphamapResolution;
            int heightRes = heights.GetLength(0);
            var map = new float[res, res, rules.Length];

            for (int y = 0; y < res; y++)
            {
                for (int x = 0; x < res; x++)
                {
                    // Alphamap and heightmap resolutions differ, so sample in normalized space.
                    float nx = (float)x / (res - 1);
                    float ny = (float)y / (res - 1);

                    float h = BilinearHeight(heights, nx, ny);
                    float slope = SlopeDegrees(heights, nx, ny, heightRes, settings);

                    float total = 0f;
                    for (int i = 0; i < rules.Length; i++)
                    {
                        float w = RuleWeight(rules[i], h, slope);
                        map[y, x, i] = w;
                        total += w;
                    }

                    if (total > 0.0001f)
                    {
                        // Unity needs the weights at each point to sum to 1.
                        for (int i = 0; i < rules.Length; i++)
                            map[y, x, i] /= total;
                    }
                    else
                    {
                        // No rule claims this point; fall back to the first layer
                        // so the terrain never renders as an untextured hole.
                        map[y, x, 0] = 1f;
                    }
                }
            }

            return map;
        }

        /// <summary>
        /// Renders the heightmap to a texture for Inspector preview.
        /// Colors by elevation, with everything below sea level shaded as water.
        /// </summary>
        public static Texture2D GeneratePreview(TerrainMapSettings settings, float[,] heights)
        {
            int res = heights.GetLength(0);
            var tex = new Texture2D(res, res, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };

            var pixels = new Color32[res * res];
            float sea = settings.seaLevel;

            for (int y = 0; y < res; y++)
            {
                for (int x = 0; x < res; x++)
                {
                    float h = heights[y, x];
                    Color c;

                    if (h <= sea)
                    {
                        // Deep to shallow water.
                        float t = sea > 0f ? h / sea : 0f;
                        c = Color.Lerp(new Color(0.05f, 0.12f, 0.33f), new Color(0.25f, 0.55f, 0.75f), t);
                    }
                    else
                    {
                        float t = Mathf.InverseLerp(sea, 1f, h);
                        if (t < 0.12f)      c = Color.Lerp(new Color(0.80f, 0.74f, 0.52f), new Color(0.45f, 0.60f, 0.30f), t / 0.12f);
                        else if (t < 0.55f) c = Color.Lerp(new Color(0.45f, 0.60f, 0.30f), new Color(0.32f, 0.42f, 0.24f), (t - 0.12f) / 0.43f);
                        else if (t < 0.78f) c = Color.Lerp(new Color(0.32f, 0.42f, 0.24f), new Color(0.42f, 0.38f, 0.34f), (t - 0.55f) / 0.23f);
                        else                c = Color.Lerp(new Color(0.42f, 0.38f, 0.34f), Color.white, (t - 0.78f) / 0.22f);

                        // Cheap hillshade so ridges read as shape, not just colour.
                        if (x > 0 && y > 0)
                        {
                            float dx = heights[y, x] - heights[y, x - 1];
                            float dy = heights[y, x] - heights[y - 1, x];
                            float shade = Mathf.Clamp((dx + dy) * 8f, -0.35f, 0.35f);
                            c = new Color(
                                Mathf.Clamp01(c.r + shade),
                                Mathf.Clamp01(c.g + shade),
                                Mathf.Clamp01(c.b + shade));
                        }
                    }

                    // Texture2D rows run bottom-up, matching the heightmap indexing.
                    pixels[y * res + x] = c;
                }
            }

            tex.SetPixels32(pixels);
            tex.Apply();
            return tex;
        }

        // --- shaping -------------------------------------------------------

        static float ApplyFalloff(float h, float nx, float ny, TerrainMapSettings s)
        {
            if (s.falloffMode == FalloffMode.None) return h;

            float d;
            if (s.falloffMode == FalloffMode.Radial)
            {
                // Distance from centre, normalized so the edge midpoint sits at 1.
                float cx = nx * 2f - 1f;
                float cy = ny * 2f - 1f;
                d = Mathf.Clamp01(Mathf.Sqrt(cx * cx + cy * cy));
            }
            else
            {
                // Square falloff: whichever axis is closer to its edge wins.
                float cx = Mathf.Abs(nx * 2f - 1f);
                float cy = Mathf.Abs(ny * 2f - 1f);
                d = Mathf.Max(cx, cy);
            }

            float a = s.falloffSharpness;
            float b = s.falloffStrength;
            float curve = Mathf.Pow(d, a) / (Mathf.Pow(d, a) + Mathf.Pow(Mathf.Max(0.0001f, b - b * d), a));

            return s.falloffMode == FalloffMode.Basin
                ? Mathf.Clamp01(h * curve)              // keep the rim, drop the middle
                : Mathf.Clamp01(h * (1f - curve));      // keep the middle, drop the rim
        }

        static float RuleWeight(TerrainLayerRule rule, float height, float slopeDeg)
        {
            if (rule == null || rule.strength <= 0f) return 0f;

            float hw = BandWeight(height, rule.heightRange.x, rule.heightRange.y, rule.blend);
            if (hw <= 0f) return 0f;

            // Slope blend is in degrees, so scale the normalized blend up to that range.
            float sw = BandWeight(slopeDeg, rule.slopeRange.x, rule.slopeRange.y, rule.blend * 90f);
            if (sw <= 0f) return 0f;

            return hw * sw * rule.strength;
        }

        /// <summary>1 inside [min,max], feathering to 0 across <paramref name="blend"/>.</summary>
        static float BandWeight(float value, float min, float max, float blend)
        {
            if (blend <= 0f) return (value >= min && value <= max) ? 1f : 0f;

            if (value < min) return Mathf.Clamp01(1f - (min - value) / blend);
            if (value > max) return Mathf.Clamp01(1f - (value - max) / blend);
            return 1f;
        }

        static float SlopeDegrees(
            float[,] heights, float nx, float ny, int heightRes, TerrainMapSettings s)
        {
            float step = 1f / (heightRes - 1);

            float hL = BilinearHeight(heights, nx - step, ny);
            float hR = BilinearHeight(heights, nx + step, ny);
            float hD = BilinearHeight(heights, nx, ny - step);
            float hU = BilinearHeight(heights, nx, ny + step);

            // Convert normalized heights into metres before measuring the angle,
            // otherwise slope changes meaning whenever worldSize or worldHeight does.
            float metresPerStep = step * s.worldSize;
            float dx = (hR - hL) * s.worldHeight / (2f * metresPerStep);
            float dy = (hU - hD) * s.worldHeight / (2f * metresPerStep);

            return Mathf.Atan(Mathf.Sqrt(dx * dx + dy * dy)) * Mathf.Rad2Deg;
        }

        static float BilinearHeight(float[,] heights, float nx, float ny)
        {
            int res = heights.GetLength(0);
            float fx = Mathf.Clamp01(nx) * (res - 1);
            float fy = Mathf.Clamp01(ny) * (res - 1);

            int x0 = Mathf.FloorToInt(fx);
            int y0 = Mathf.FloorToInt(fy);
            int x1 = Mathf.Min(x0 + 1, res - 1);
            int y1 = Mathf.Min(y0 + 1, res - 1);

            float tx = fx - x0;
            float ty = fy - y0;

            float bottom = Mathf.Lerp(heights[y0, x0], heights[y0, x1], tx);
            float top    = Mathf.Lerp(heights[y1, x0], heights[y1, x1], tx);
            return Mathf.Lerp(bottom, top, ty);
        }

        // --- curve LUT -----------------------------------------------------

        static float[] BakeCurve(AnimationCurve curve, int samples)
        {
            var lut = new float[samples];
            if (curve == null || curve.length == 0)
            {
                for (int i = 0; i < samples; i++) lut[i] = (float)i / (samples - 1);
                return lut;
            }

            for (int i = 0; i < samples; i++)
                lut[i] = curve.Evaluate((float)i / (samples - 1));
            return lut;
        }

        static float SampleLut(float[] lut, float t)
        {
            float f = t * (lut.Length - 1);
            int i = Mathf.FloorToInt(f);
            int j = Mathf.Min(i + 1, lut.Length - 1);
            return Mathf.Lerp(lut[i], lut[j], f - i);
        }
    }
}
