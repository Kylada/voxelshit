namespace VoxelEngine;

/// <summary>
/// Builds a starting world that contains every voxel type, plus a few things
/// that fall on their own so you can watch the physics work immediately.
///
/// WHY THE WORLD IS HAND-PLACED RATHER THAN NOISE-ONLY: you asked for all ten
/// types to be present, and noise alone cannot guarantee that. Each feature is
/// built in its own little region with a deliberate layout, which also makes the
/// generator easy to read and to change.
/// </summary>
public static class WorldGenerator
{
    /// <summary>Repeatable pseudo-random numbers — same seed, same world, every time.</summary>
    private sealed class Rng32
    {
        private uint _s;
        public Rng32(uint seed) { _s = seed == 0 ? 1u : seed; }
        public float Next()
        {
            _s ^= _s << 13; _s ^= _s >> 17; _s ^= _s << 5;
            return (_s & 0xFFFFFF) / (float)0x1000000;
        }
        public int Range(int a, int b) => a + (int)(Next() * (b - a));
    }

    /// <summary>Smooth 2D value noise: the workhorse of cheap terrain.</summary>
    private sealed class ValueNoise
    {
        private const int G = 256;
        private readonly float[] _grid = new float[G * G];

        public ValueNoise(uint seed)
        {
            var rnd = new Rng32(seed);
            for (int i = 0; i < G * G; i++) _grid[i] = rnd.Next();
        }

        private float At(int x, int z) =>
            _grid[((x % G) + G) % G + (((z % G) + G) % G) * G];

        public float Sample(float x, float z)
        {
            int x0 = (int)MathF.Floor(x), z0 = (int)MathF.Floor(z);
            float fx = x - x0, fz = z - z0;
            float sx = fx * fx * (3 - 2 * fx), sz = fz * fz * (3 - 2 * fz);
            float a = At(x0, z0), b = At(x0 + 1, z0), c = At(x0, z0 + 1), d = At(x0 + 1, z0 + 1);
            float ab = a + (b - a) * sx, cd = c + (d - c) * sx;
            return ab + (cd - ab) * sz;
        }
    }

    public static void Generate(World world, int seed = 20261105)
    {
        world.Reset();
        int S = world.Size;
        var rng = new Rng32((uint)seed);
        var n1 = new ValueNoise((uint)seed + 11);
        var n2 = new ValueNoise((uint)seed + 4242);
        var height = new int[S * S];

        // ---- base terrain: stone with a dirt crust, bare rock up high ----
        for (int z = 0; z < S; z++)
        {
            for (int x = 0; x < S; x++)
            {
                float hf = 51f + n1.Sample(x * 0.035f, z * 0.035f) * 19f
                                + n2.Sample(x * 0.11f, z * 0.11f) * 5f - 2f;
                int h = Math.Clamp((int)MathF.Round(hf), 30, 78);
                height[x + z * S] = h;

                int crust = h > 66 ? 0 : (h > 62 ? 2 : 4);
                for (int y = 0; y < h; y++)
                    world.SetRaw(x, y, z, y < h - crust ? Vox.Stone : Vox.Dirt);
            }
        }

        // ---- metal ore veins buried in the stone ----
        for (int k = 0; k < 26; k++)
        {
            float cx = 6 + rng.Next() * (S - 12);
            float cz = 6 + rng.Next() * (S - 12);
            float cy = 6 + rng.Next() * 46;
            float r = 1.2f + rng.Next() * 2.0f;

            for (int z = (int)(cz - r); z <= cz + r; z++)
                for (int y = (int)(cy - r); y <= cy + r; y++)
                    for (int x = (int)(cx - r); x <= cx + r; x++)
                    {
                        float dx = x - cx, dy = y - cy, dz = z - cz;
                        if (dx * dx + dy * dy + dz * dz <= r * r && world.Get(x, y, z) == Vox.Stone)
                            world.SetRaw(x, y, z, Vox.Metal);
                    }
        }

        // ---- contained pools: the rim is what keeps a liquid from spreading ----
        Pool(world, 42, 88, 17, 56, 46, Vox.Water);   // lake
        Pool(world, 95, 96, 12, 54, 48, Vox.Oil);     // oil pool
        Pool(world, 104, 38, 9, 55, 50, Vox.Slime);   // slime pit

        // ---- sand dune ----
        {
            const int cx = 30, cz = 32, R = 16;
            for (int z = cz - R; z <= cz + R; z++)
                for (int x = cx - R; x <= cx + R; x++)
                {
                    if (x < 1 || z < 1 || x >= S - 1 || z >= S - 1) continue;
                    float d = MathF.Sqrt((x - cx) * (x - cx) + (z - cz) * (z - cz));
                    if (d > R) continue;
                    int mound = (int)MathF.Round((1 - d / R) * 9);
                    int h = height[x + z * S];
                    for (int y = h; y < h + mound && y < S; y++) world.SetRaw(x, y, z, Vox.Sand);
                }
        }

        // ---- trees (wood only: there is no leaf type yet) ----
        int trees = 0;
        for (int attempt = 0; attempt < 400 && trees < 22; attempt++)
        {
            int x = rng.Range(5, S - 5), z = rng.Range(5, S - 5);
            int h = height[x + z * S];
            if (world.Get(x, h - 1, z) != Vox.Dirt) continue;
            if (world.Get(x, h, z) != Vox.Air) continue;
            if (Dist(x, z, 42, 88) < 24 || Dist(x, z, 95, 96) < 19) continue;
            if (Dist(x, z, 104, 38) < 16 || Dist(x, z, 30, 32) < 20) continue;
            if (Dist(x, z, 70, 62) < 14) continue;

            int trunk = rng.Range(4, 8);
            for (int y = h; y < h + trunk; y++) world.SetRaw(x, y, z, Vox.Wood);

            int ty = h + trunk;
            for (int dy = -2; dy <= 1; dy++)
                for (int dz = -2; dz <= 2; dz++)
                    for (int dx = -2; dx <= 2; dx++)
                    {
                        float q = dx * dx + dz * dz + dy * dy * 2.2f;
                        if (q <= 5.2f && world.Get(x + dx, ty + dy, z + dz) == Vox.Air)
                            world.SetRaw(x + dx, ty + dy, z + dz, Vox.Wood);
                    }
            trees++;
        }

        // ---- metal platform on pillars, with fire braziers ----
        {
            const int mx = 70, mz = 62;
            int baseY = height[mx + mz * S] + 1;
            int top = baseY + 7;

            foreach (var (px, pz) in new[] { (-4, -4), (4, -4), (-4, 4), (4, 4) })
                for (int y = baseY - 2; y < top; y++)
                    world.SetRaw(mx + px, y, mz + pz, Vox.Metal);

            for (int dz = -5; dz <= 5; dz++)
                for (int dx = -5; dx <= 5; dx++)
                    if (Math.Abs(dx) == 5 || Math.Abs(dz) == 5 || (dx + dz) % 2 == 0)
                        world.SetRaw(mx + dx, top, mz + dz, Vox.Metal);

            foreach (var (px, pz) in new[] { (-4, -4), (4, -4), (-4, 4), (4, 4) })
            {
                world.SetRaw(mx + px, top, mz + pz, Vox.Stone);
                world.SetRaw(mx + px, top + 1, mz + pz, Vox.Fire);
            }

            for (int k = 0; k < 3; k++)
            {
                int cx = mx - 2 + rng.Range(0, 5), cz = mz - 2 + rng.Range(0, 5);
                world.SetRaw(cx, top + 1, cz, Vox.Metal);
                world.SetRaw(cx, top + 2, cz, Vox.Metal);
            }
        }

        // ---- ground fire pit ----
        {
            const int fx = 58, fz = 40;
            int baseY = height[fx + fz * S];
            for (int dz = -2; dz <= 2; dz++)
                for (int dx = -2; dx <= 2; dx++)
                {
                    float d = MathF.Sqrt(dx * dx + dz * dz);
                    if (d > 2.4f) continue;
                    if (d < 1.2f)
                    {
                        world.SetRaw(fx + dx, baseY, fz + dz, Vox.Fire);
                        world.SetRaw(fx + dx, baseY + 1, fz + dz, Vox.Fire);
                    }
                    else world.SetRaw(fx + dx, baseY, fz + dz, Vox.Stone);

                    for (int y = baseY + 3; y < baseY + 8; y++) world.SetRaw(fx + dx, y, fz + dz, Vox.Air);
                }
        }

        // ---- floating cubes: these fall on load, showing off the physics ----
        Cube(world, 12, 84, 16, 6, 5, 6, Vox.Water);
        Cube(world, 24, 88, 66, 5, 4, 5, Vox.Sand);
        Cube(world, 66, 90, 30, 4, 4, 4, Vox.Dirt);
        Cube(world, 52, 86, 108, 4, 4, 4, Vox.Slime);
        Cube(world, 118, 82, 60, 4, 3, 4, Vox.Sand);

        // ---- stone bowl to catch the water demo ----
        {
            const int bx = 18, bz = 22, r = 5;
            int baseY = height[bx + bz * S];
            for (int dz = -r; dz <= r; dz++)
                for (int dx = -r; dx <= r; dx++)
                {
                    float d = MathF.Sqrt(dx * dx + dz * dz);
                    if (d > r) continue;
                    world.SetRaw(bx + dx, baseY, bz + dz, Vox.Stone);
                    if (d > r - 1) world.SetRaw(bx + dx, baseY + 1, bz + dz, Vox.Stone);
                }
        }

        world.Dirty = true;
        world.Pending.Count = 0;
        world.ActivateAll();
    }

    private static float Dist(int x, int z, int cx, int cz) =>
        MathF.Sqrt((x - cx) * (x - cx) + (z - cz) * (z - cz));

    private static void Cube(World w, int x0, int y0, int z0, int sx, int sy, int sz, byte id)
    {
        for (int z = z0; z < z0 + sz; z++)
            for (int y = y0; y < y0 + sy; y++)
                for (int x = x0; x < x0 + sx; x++)
                    w.SetRaw(x, y, z, id);
    }

    /// <summary>A liquid-filled basin with a solid rim, so the liquid stays put.</summary>
    private static void Pool(World world, int cx, int cz, int radius, int level, int floorY, byte fluid)
    {
        int S = world.Size;
        int outer = radius + 3;

        for (int z = (int)(cz - outer); z <= cz + outer; z++)
            for (int x = (int)(cx - outer); x <= cx + outer; x++)
            {
                if (x < 0 || z < 0 || x >= S || z >= S) continue;
                float d = MathF.Sqrt((x - cx) * (x - cx) + (z - cz) * (z - cz));

                if (d <= radius)
                {
                    for (int y = 0; y < level; y++)
                    {
                        if (y < floorY) world.SetRaw(x, y, z, y < floorY - 3 ? Vox.Stone : Vox.Dirt);
                        else world.SetRaw(x, y, z, fluid);
                    }
                    for (int y = level; y < level + 16 && y < S; y++) world.SetRaw(x, y, z, Vox.Air);
                }
                else if (d <= radius + 2.6f)
                {
                    for (int y = 0; y <= level + 1; y++)
                        world.SetRaw(x, y, z, y >= level - 1 ? Vox.Dirt : Vox.Stone);
                    for (int y = level + 2; y < level + 16 && y < S; y++) world.SetRaw(x, y, z, Vox.Air);
                }
            }
    }
}
