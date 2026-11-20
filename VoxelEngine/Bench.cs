using System.Diagnostics;

namespace VoxelEngine;

/// <summary>
/// Measures what the simulation actually costs. Run with: dotnet run -- --bench
///
/// WHY THIS EXISTS: "should I add multithreading?" is an empirical question, not
/// an opinion. This prints the per-tick cost of the same workload under both
/// schedulers, plus the cost of a per-voxel stencil pass — the two numbers you
/// need before deciding anything.
/// </summary>
public static class Bench
{
    public static int Run()
    {
        VoxelPalette.RegisterDefaults();
        Console.WriteLine($"cores available: {Environment.ProcessorCount}");
        Console.WriteLine();

        SettledWorldCost();
        ActiveWorkload(claim: "everything active (worldgen settle)", voxelCount: 0);
        ActiveWorkload(claim: "a heavy burst (~300k active voxels)", voxelCount: 1_000_000);
        ChannelStencilCost();

        Console.WriteLine();
        Console.WriteLine("Reading: a frame at 60fps has 16.7 ms total. Physics must fit in a slice of that.");
        return 0;
    }

    private static void SettledWorldCost()
    {
        Console.WriteLine("--- a world at rest ---");
        var w = new World();
        WorldGenerator.Generate(w, 4242);
        var ser = new SerialScheduler();
        for (int i = 0; i < 400; i++) ser.Tick(w);   // let it settle

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 1000; i++) ser.Tick(w);
        sw.Stop();
        Console.WriteLine($"  {w.Pending.Count:N0} pending, {sw.Elapsed.TotalMilliseconds / 1000:0.0000} ms per tick");
        Console.WriteLine("  -> the active-set design means a quiet world is free.");
        Console.WriteLine();
    }

    private static void ActiveWorkload(string claim, int voxelCount)
    {
        Console.WriteLine($"--- {claim} ---");

        var serialWorld = new World();
        var parallelWorld = new World();
        WorldGenerator.Generate(serialWorld, 4242);
        WorldGenerator.Generate(parallelWorld, 4242);

        if (voxelCount > 0) FillAir(serialWorld, parallelWorld, voxelCount);

        var serial = new SerialScheduler();
        var parallel = new ParallelScheduler();

        // warm up both (JIT + caches), and let the first burst pass
        for (int i = 0; i < 5; i++) { serial.Tick(serialWorld); parallel.Tick(parallelWorld); }

        int ticks = 60;
        double serTotal = 0, parTotal = 0;
        long peak = 0;

        for (int i = 0; i < ticks; i++)
        {
            peak = Math.Max(peak, serialWorld.ProcessingCount);
            serial.Tick(serialWorld); serTotal += serialWorld.LastTickMs;
        }
        for (int i = 0; i < ticks; i++)
        {
            parallel.Tick(parallelWorld); parTotal += parallelWorld.LastTickMs;
        }

        double s = serTotal / ticks, p = parTotal / ticks;
        Console.WriteLine($"  peak active per tick: {peak:N0}");
        Console.WriteLine($"  serial   {s,8:0.000} ms/tick");
        Console.WriteLine($"  parallel {p,8:0.000} ms/tick   ({(s > 0 ? p / s : 0):0.00}x)");
        if (p > s) Console.WriteLine("  -> parallel is SLOWER here: splitting and merging cost more than the work.");
        Console.WriteLine();
    }

    /// <summary>Flood the open air with falling voxels so there is real work to spread.</summary>
    private static void FillAir(World a, World b, int count)
    {
        int placed = 0;
        // spread it out vertically so the cubes do not all settle in the first tick
        for (int layer = 0; layer < 8 && placed < count; layer++)
        {
            int y0 = 90 + layer * 4;
            for (int z = 20; z < 108 && placed < count; z++)
                for (int x = 20; x < 108 && placed < count; x++)
                {
                    if (a.Get(x, y0, z) != Vox.Air) continue;
                    byte id = ((x + z + layer) & 3) == 0 ? Vox.Water : Vox.Sand;
                    a.Set(x, y0, z, id);
                    b.Set(x, y0, z, id);
                    placed++;
                }
        }
    }

    private static void ChannelStencilCost()
    {
        Console.WriteLine("--- per-voxel channel: one 6-neighbour stencil pass over the whole world ---");
        var w = new World();
        WorldGenerator.Generate(w, 4242);
        var temp = w.Channel<float>("temperature", 20f);

        // Fill the channel with something varying so the loop cannot be optimised away.
        var rnd = new Random(1);
        for (int i = 0; i < temp.Data.Length; i++) temp.Data[i] = (float)rnd.NextDouble() * 100f;

        var data = w.Data;
        var chan = temp.Data;
        var next = new float[chan.Length];
        int ps = World.PS, ps2 = World.PS2;

        // Warm up, then measure. This is what heat diffusion / light propagation
        // would look like: read 7 cells, write 1, per voxel, every tick.
        for (int pass = 0; pass < 3; pass++)
        {
            var sw = Stopwatch.StartNew();
            for (int z = 1; z < World.PS - 1; z++)
                for (int y = 1; y < World.PS - 1; y++)
                {
                    int i = 1 + ps * (y + ps * z);
                    for (int x = 1; x < World.PS - 1; x++, i++)
                    {
                        next[i] = (chan[i - 1] + chan[i + 1]
                                 + chan[i - ps] + chan[i + ps]
                                 + chan[i - ps2] + chan[i + ps2]) * (1f / 6f);
                    }
                }
            sw.Stop();
            if (pass == 2)
                Console.WriteLine($"  {sw.Elapsed.TotalMilliseconds:0.0} ms per full-grid pass " +
                                  $"({World.PS * World.PS * World.PS:N0} cells, 8.8 MB of floats)");
        }
        Console.WriteLine("  -> at 40 Hz that is a large slice of the frame budget. Budget it deliberately.");
    }
}
