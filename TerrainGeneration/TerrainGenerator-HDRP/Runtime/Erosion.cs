using UnityEngine;

namespace CustomTools.TerrainGeneration
{
    /// <summary>
    /// Droplet-based hydraulic erosion. Rain drops are dropped on the terrain and
    /// followed downhill; each picks up sediment on steep ground and drops it on flat
    /// ground. Run over enough droplets this carves branching drainage networks and
    /// settles sediment into the valleys, which is the structure pure noise cannot
    /// produce however many octaves it is given.
    /// </summary>
    public static class Erosion
    {
        /// <summary>
        /// Erodes <paramref name="heights"/> in place.
        /// </summary>
        /// <param name="heights">Normalized heightmap, indexed [y, x].</param>
        /// <param name="settings">Erosion tuning.</param>
        /// <param name="seed">Droplet placement seed; same seed gives the same result.</param>
        /// <param name="flowOut">
        /// Optional map receiving how much water passed over each cell, normalized 0..1.
        /// Useful for texturing river beds and for the preview.
        /// </param>
        /// <param name="onProgress">
        /// Optional 0..1 progress callback. Erosion is slow enough at high droplet
        /// counts that a silent multi-second freeze reads as a hang.
        /// </param>
        public static void Apply(
            float[,] heights,
            ErosionSettings settings,
            int seed,
            float[,] flowOut = null,
            System.Func<float, bool> onProgress = null)
        {
            if (heights == null || settings == null || settings.iterations <= 0) return;

            int res = heights.GetLength(0);
            if (res < 8) return;

            // One shared brush, not one per cell: the disc is the same everywhere, only
            // its centre moves. A per-cell table would cost over a gigabyte at 2049.
            BuildBrush(settings.erosionRadius, out var brushDx, out var brushDy, out var brushWeights);

            var prng = new System.Random(seed);

            // Report sparingly: the callback drives a progress bar, and repainting it
            // per droplet would cost more than the simulation.
            int reportEvery = Mathf.Max(1, settings.iterations / 100);

            for (int iteration = 0; iteration < settings.iterations; iteration++)
            {
                if (onProgress != null && iteration % reportEvery == 0)
                {
                    if (!onProgress((float)iteration / settings.iterations)) return; // cancelled
                }

                SimulateDroplet(heights, settings, prng, brushDx, brushDy, brushWeights, res, flowOut);
            }

            NormalizeFlow(flowOut);
        }

        static void SimulateDroplet(
            float[,] heights, ErosionSettings s, System.Random prng,
            int[] brushDx, int[] brushDy, float[] brushWeights, int res, float[,] flowOut)
        {
            // Start somewhere random, just inside the border so the 2x2 sample is valid.
            float posX = (float)prng.NextDouble() * (res - 3) + 1f;
            float posY = (float)prng.NextDouble() * (res - 3) + 1f;

            float dirX = 0f, dirY = 0f;
            float speed = s.initialSpeed;
            float water = s.initialWater;
            float sediment = 0f;

            for (int life = 0; life < s.maxDropletLifetime; life++)
            {
                int nodeX = (int)posX;
                int nodeY = (int)posY;

                // Offset within the current cell, for bilinear sampling.
                float cellOffsetX = posX - nodeX;
                float cellOffsetY = posY - nodeY;

                var (height, gradX, gradY) = SampleHeightAndGradient(heights, res, posX, posY);

                // Momentum carries the droplet through small bumps instead of letting it
                // stall; without it, droplets pool in every tiny local minimum.
                dirX = dirX * s.inertia - gradX * (1f - s.inertia);
                dirY = dirY * s.inertia - gradY * (1f - s.inertia);

                float len = Mathf.Sqrt(dirX * dirX + dirY * dirY);
                if (len > 0.0001f)
                {
                    dirX /= len;
                    dirY /= len;
                }
                else
                {
                    // Flat ground and no momentum: nudge randomly rather than stop dead.
                    float angle = (float)prng.NextDouble() * Mathf.PI * 2f;
                    dirX = Mathf.Cos(angle);
                    dirY = Mathf.Sin(angle);
                }

                posX += dirX;
                posY += dirY;

                // Off the map, or nowhere to go.
                if (posX < 1f || posX >= res - 2f || posY < 1f || posY >= res - 2f) break;

                float newHeight = SampleHeightAndGradient(heights, res, posX, posY).height;
                float deltaHeight = newHeight - height;

                if (flowOut != null)
                {
                    // Weight by speed, not just volume: a droplet dawdling across a
                    // plain is not a river. Without this the map records droplet
                    // traffic, and hillsides read as wet as the channels.
                    flowOut[nodeY, nodeX] += water * speed;
                }

                // Capacity scales with how fast the water moves and how steeply it falls:
                // a fast droplet running downhill can hold far more sediment than a slow
                // one on the flat, which is what makes deposition happen in valleys.
                float capacity = Mathf.Max(
                    -deltaHeight * speed * water * s.sedimentCapacityFactor,
                    s.minSedimentCapacity);

                if (sediment > capacity || deltaHeight > 0f)
                {
                    // Carrying too much, or running uphill: drop sediment.
                    // Uphill droplets may only fill the dip they just hit, never mound up.
                    float amount = deltaHeight > 0f
                        ? Mathf.Min(deltaHeight, sediment)
                        : (sediment - capacity) * s.depositSpeed;

                    sediment -= amount;
                    DepositBilinear(heights, res, nodeX, nodeY, cellOffsetX, cellOffsetY, amount);
                }
                else
                {
                    // Room to spare: erode, but never cut deeper than the drop ahead,
                    // which would dig a pit the water then cannot leave.
                    float amount = Mathf.Min((capacity - sediment) * s.erodeSpeed, -deltaHeight);
                    sediment += ErodeWithBrush(
                        heights, res, nodeX, nodeY, amount, brushDx, brushDy, brushWeights);
                }

                // Gravity. Negative deltaHeight (downhill) speeds the droplet up.
                float speedSq = speed * speed + -deltaHeight * s.gravity;
                speed = speedSq > 0f ? Mathf.Sqrt(speedSq) : 0f;

                water *= 1f - s.evaporateSpeed;
                if (water < 0.0005f) break;
            }
        }

        /// <summary>
        /// Bilinear height plus the surface gradient at a sub-cell position.
        /// The gradient is what steers the droplet downhill.
        /// </summary>
        static (float height, float gradientX, float gradientY) SampleHeightAndGradient(
            float[,] heights, int res, float posX, float posY)
        {
            int coordX = (int)posX;
            int coordY = (int)posY;

            coordX = Mathf.Clamp(coordX, 0, res - 2);
            coordY = Mathf.Clamp(coordY, 0, res - 2);

            float x = posX - coordX;
            float y = posY - coordY;

            float nw = heights[coordY, coordX];
            float ne = heights[coordY, coordX + 1];
            float sw = heights[coordY + 1, coordX];
            float se = heights[coordY + 1, coordX + 1];

            float gradientX = (ne - nw) * (1f - y) + (se - sw) * y;
            float gradientY = (sw - nw) * (1f - x) + (se - ne) * x;

            float height =
                nw * (1f - x) * (1f - y) +
                ne * x * (1f - y) +
                sw * (1f - x) * y +
                se * x * y;

            return (height, gradientX, gradientY);
        }

        /// <summary>Spreads deposited sediment over the four cells the droplet straddles.</summary>
        static void DepositBilinear(
            float[,] heights, int res, int nodeX, int nodeY,
            float cellOffsetX, float cellOffsetY, float amount)
        {
            if (nodeX < 0 || nodeY < 0 || nodeX >= res - 1 || nodeY >= res - 1) return;

            heights[nodeY, nodeX]         += amount * (1f - cellOffsetX) * (1f - cellOffsetY);
            heights[nodeY, nodeX + 1]     += amount * cellOffsetX * (1f - cellOffsetY);
            heights[nodeY + 1, nodeX]     += amount * (1f - cellOffsetX) * cellOffsetY;
            heights[nodeY + 1, nodeX + 1] += amount * cellOffsetX * cellOffsetY;
        }

        /// <summary>
        /// Removes material over a weighted disc rather than a single cell, so erosion
        /// cuts smooth channels instead of one-pixel-wide scratches.
        /// Returns how much was actually removed.
        /// </summary>
        static float ErodeWithBrush(
            float[,] heights, int res, int nodeX, int nodeY, float amount,
            int[] brushDx, int[] brushDy, float[] brushWeights)
        {
            float removed = 0f;

            for (int i = 0; i < brushDx.Length; i++)
            {
                int bx = nodeX + brushDx[i];
                int by = nodeY + brushDy[i];

                // Clip at the border. The lost weight is simply not removed, which
                // slightly softens erosion at the very edge — preferable to wrapping
                // round and carving the opposite side of the map.
                if (bx < 0 || by < 0 || bx >= res || by >= res) continue;

                float weighted = amount * brushWeights[i];

                // Never dig below zero; the terrain is normalized.
                float delta = Mathf.Min(heights[by, bx], weighted);
                heights[by, bx] -= delta;
                removed += delta;
            }

            return removed;
        }

        /// <summary>
        /// Builds the weighted disc an erosion event spreads over, as offsets from the
        /// droplet's cell. One shared brush rather than one per cell: the shape never
        /// varies, so a per-cell table would waste hundreds of megabytes at high
        /// resolutions for no benefit.
        /// </summary>
        static void BuildBrush(int radius, out int[] dx, out int[] dy, out float[] weights)
        {
            radius = Mathf.Clamp(radius, 1, 8);

            int span = radius * 2 + 1;
            var tmpX = new int[span * span];
            var tmpY = new int[span * span];
            var tmpW = new float[span * span];

            int count = 0;
            float weightSum = 0f;

            for (int oy = -radius; oy <= radius; oy++)
            {
                for (int ox = -radius; ox <= radius; ox++)
                {
                    float sqrDst = ox * ox + oy * oy;
                    if (sqrDst >= radius * radius) continue;

                    // Linear falloff from the centre.
                    float w = 1f - Mathf.Sqrt(sqrDst) / radius;
                    weightSum += w;

                    tmpX[count] = ox;
                    tmpY[count] = oy;
                    tmpW[count] = w;
                    count++;
                }
            }

            dx = new int[count];
            dy = new int[count];
            weights = new float[count];

            for (int i = 0; i < count; i++)
            {
                dx[i] = tmpX[i];
                dy[i] = tmpY[i];
                weights[i] = weightSum > 0f ? tmpW[i] / weightSum : 0f;
            }
        }

        /// <summary>
        /// Scales the flow map into 0..1. Flow is extremely long-tailed — a few river
        /// cells carry orders of magnitude more water than the hillsides — so this uses
        /// a high percentile rather than the maximum, or everything but the main channel
        /// would round to black.
        /// </summary>
        static void NormalizeFlow(float[,] flow)
        {
            if (flow == null) return;

            int res = flow.GetLength(0);
            int total = res * res;

            // Scale against a high percentile of the cells that carry water at all.
            // Including the dry majority would drag the reference down and wash the
            // whole map out; using the maximum would leave only the single largest
            // channel visible.
            var wet = new float[total];
            int n = 0;
            for (int y = 0; y < res; y++)
                for (int x = 0; x < res; x++)
                    if (flow[y, x] > 0f) wet[n++] = flow[y, x];

            if (n == 0) return;

            System.Array.Sort(wet, 0, n);

            float reference = wet[Mathf.Clamp((int)(n * 0.97f), 0, n - 1)];
            if (reference <= 0.0001f) return;

            for (int y = 0; y < res; y++)
                for (int x = 0; x < res; x++)
                    flow[y, x] = Mathf.Clamp01(flow[y, x] / reference);
        }
    }

    /// <summary>Hydraulic erosion tuning.</summary>
    [System.Serializable]
    public class ErosionSettings
    {
        [Tooltip("Run hydraulic erosion after generating the base noise.")]
        public bool enabled = false;

        [Tooltip("Number of rain droplets simulated. More carves deeper, detailed " +
                 "networks but takes longer. 50k is a good balance at 513.")]
        [Min(0)] public int iterations = 50000;

        [Tooltip("How many steps each droplet may travel before it is abandoned. " +
                 "Longer lets rivers run further across the map.")]
        [Range(4, 128)] public int maxDropletLifetime = 30;

        [Tooltip("Radius in cells that one erosion event affects. Larger carves " +
                 "broader, smoother valleys; smaller cuts sharper gullies.")]
        [Range(1, 8)] public int erosionRadius = 3;

        [Tooltip("How much a droplet keeps its direction versus following the slope. " +
                 "0 sticks rigidly to the steepest descent; high values wander and " +
                 "can smear the result.")]
        [Range(0f, 0.99f)] public float inertia = 0.05f;

        [Tooltip("How much sediment the water can carry. Higher carves far more aggressively.")]
        [Min(0.1f)] public float sedimentCapacityFactor = 4f;

        [Tooltip("Floor on carrying capacity, so slow water on flat ground still " +
                 "moves a little material instead of dumping everything at once.")]
        [Min(0f)] public float minSedimentCapacity = 0.01f;

        [Tooltip("How quickly a droplet erodes when it has spare capacity.")]
        [Range(0f, 1f)] public float erodeSpeed = 0.3f;

        [Tooltip("How quickly a droplet drops sediment once it is over capacity.")]
        [Range(0f, 1f)] public float depositSpeed = 0.3f;

        [Tooltip("How fast a droplet dries up. Higher gives shorter rivers.")]
        [Range(0f, 0.1f)] public float evaporateSpeed = 0.01f;

        [Tooltip("Acceleration downhill. Affects how fast water gains carrying capacity.")]
        [Min(0f)] public float gravity = 4f;

        [Tooltip("Speed a droplet starts with.")]
        [Min(0f)] public float initialSpeed = 1f;

        [Tooltip("Water a droplet starts with.")]
        [Min(0.01f)] public float initialWater = 1f;
    }
}
