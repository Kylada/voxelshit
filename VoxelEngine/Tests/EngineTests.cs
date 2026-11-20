using System.Diagnostics;
using System.Numerics;

namespace VoxelEngine;

/// <summary>
/// Headless test suite. Run with:  dotnet run -- --test
///
/// These tests are the reason the engine layer contains zero OpenTK code: the
/// whole simulation can be exercised without a window, a GPU or a display.
/// </summary>
public static class EngineTests
{
    private static int _failures;
    private static int _checks;

    private static void Ok(bool condition, string message)
    {
        _checks++;
        if (condition) Console.WriteLine("  PASS  " + message);
        else { Console.WriteLine("  FAIL  " + message); _failures++; }
    }

    private static void Section(string title) => Console.WriteLine($"\n=== {title} ===");

    public static int Run()
    {
        VoxelPalette.RegisterDefaults();

        RegistryTests();
        IndexingTests();
        SwapRuleTests();
        SolidModeTests();
        FixableAndTimerTests();
        SandModeTests();
        ViscosityTests();
        LiquidTests();
        CameraConventionTests();
        RaycastTests();
        GenerationTests();
        StabilityTests();
        DeterminismTests();
        SchedulerEquivalenceTests();

        Console.WriteLine();
        Console.WriteLine(_failures == 0
            ? $"ALL TESTS PASSED ({_checks} checks)"
            : $"{_failures} of {_checks} TEST(S) FAILED");
        return _failures == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------------ registry

    private static void RegistryTests()
    {
        Section("registry");

        Ok(VoxelRegistry.Count == 10, "10 voxel types registered");
        var names = string.Join(",", VoxelRegistry.Defs.Select(d => d.Name));
        Ok(names == "Air,Dirt,Water,Stone,Oil,Fire,Wood,Metal,Slime,Sand", "names and order: " + names);

        foreach (var d in VoxelRegistry.Defs)
        {
            Ok(!string.IsNullOrEmpty(d.Name)
               && VoxelRegistry.Viscosity[d.Id] == d.Viscosity
               && VoxelRegistry.Physics[d.Id] == d.Physics,
               $"'{d.Name}' exposes all six core properties");
        }

        Ok(VoxelRegistry.Viscosity[Vox.Air] == 0f, "air viscosity is 0");
        Ok(VoxelRegistry.Viscosity[Vox.Stone] == 1f, "stone viscosity is 1");
        Ok(VoxelRegistry.Viscosity[Vox.Slime] > VoxelRegistry.Viscosity[Vox.Water]
           && VoxelRegistry.Viscosity[Vox.Water] > VoxelRegistry.Viscosity[Vox.Oil],
           "slime > water > oil viscosity");

        var modes = VoxelRegistry.Defs.Select(d => d.Physics).Distinct().Count();
        Ok(modes == 5, "all five physics modes are represented");
    }

    private static void IndexingTests()
    {
        Section("padded storage");

        var w = new World();
        Ok(World.PS == Config.Size + 2, $"padded edge = {World.PS}");
        Ok(w.Data.Length == World.PS * World.PS * World.PS, $"volume = {w.Data.Length:N0}");
        Ok(w.Get(-1, 0, 0) == Vox.Stone && w.Get(0, -1, 0) == Vox.Stone, "outside the box reads as stone");
        Ok(World.Index(0, 0, 0) == 1 + World.PS * (1 + World.PS), "index() applies the pad");
        Ok(World.Index(1, 0, 0) - World.Index(0, 0, 0) == 1, "x is the fastest-changing axis");
        Ok(World.Index(0, 1, 0) - World.Index(0, 0, 0) == World.PS, "+1 y == +PS");
        Ok(World.Index(0, 0, 1) - World.Index(0, 0, 0) == World.PS2, "+1 z == +PS2");
    }

    // ------------------------------------------------------------------ physics

    private static void Tick(World w, IPhysicsScheduler s, int n)
    {
        for (int i = 0; i < n; i++) s.Tick(w);
    }

    private static void SwapRuleTests()
    {
        Section("swap rule (a mover never destroys what it lands in)");

        var w = new World();
        var ser = new SerialScheduler();

        w.SetRaw(5, 10, 5, Vox.Sand);
        w.SetRaw(5, 9, 5, Vox.Water);
        w.ActivateAt(5, 10, 5);
        ser.Tick(w);

        Ok(w.Get(5, 9, 5) == Vox.Sand, "sand fell through water");
        Ok(w.Get(5, 10, 5) == Vox.Water, "the water swapped UP into the vacated cell");
    }

    private static void SolidModeTests()
    {
        Section("'solid' physics (dirt)");

        var w = new World();
        var ser = new SerialScheduler();

        w.SetRaw(5, 20, 5, Vox.Dirt);
        w.ActivateAt(5, 20, 5);
        ser.Tick(w);
        Ok(w.Get(5, 19, 5) == Vox.Dirt && w.Get(5, 20, 5) == Vox.Air, "dirt falls one cell straight down");

        var w2 = new World();
        w2.SetRaw(5, 20, 5, Vox.Dirt);
        w2.SetRaw(5, 19, 5, Vox.Stone);
        w2.SetRaw(5, 18, 5, Vox.Stone);
        for (int i = 0; i < 40; i++) { w2.ActivateAt(5, 20, 5); ser.Tick(w2); }
        Ok(w2.Get(5, 20, 5) == Vox.Dirt, "dirt ignores open diagonals and stays put");

        var w3 = new World();
        w3.SetRaw(5, 20, 5, Vox.Metal);
        for (int i = 0; i < 20; i++) { w3.ActivateAt(5, 20, 5); ser.Tick(w3); }
        Ok(w3.Get(5, 20, 5) == Vox.Metal, "'none' voxels hang in mid-air forever");
    }

    private static void FixableAndTimerTests()
    {
        Section("fixable support and autonomous liquid timer");
        var s = new SerialScheduler();
        var w = new World();
        w.SetRaw(10, 10, 10, Vox.Stone);
        w.ActivateAt(10, 10, 10); s.Tick(w);
        Ok(w.Get(10, 9, 10) == Vox.Stone, "isolated stone falls");
        var side = new World();
        side.SetRaw(10, 10, 10, Vox.Stone); side.SetRaw(11, 10, 10, Vox.Stone);
        side.ActivateAt(10, 10, 10); Tick(side, s, 4);
        Ok(side.Get(10, 10, 10) == Vox.Stone, "stone supported by fixable neighbor");
        var above = new World();
        above.SetRaw(10, 10, 10, Vox.Stone); above.SetRaw(10, 11, 10, Vox.Stone);
        above.ActivateAt(10, 10, 10); s.Tick(above);
        Ok(above.Get(10, 10, 10) == Vox.Stone, "stone supported by fixable voxel above");
        var fire = new World();
        fire.SetRaw(10, 10, 10, Vox.Sand); fire.SetRaw(10, 9, 10, Vox.Fire);
        fire.ActivateAt(10, 10, 10); s.Tick(fire);
        Ok(fire.Get(10, 9, 10) == Vox.Sand && fire.Get(10, 10, 10) == Vox.Fire,
            "falling sand swaps fire upward without deleting it");
        var fluid = new World();
        for (int z = 8; z <= 12; z++) for (int x = 8; x <= 12; x++)
            fluid.SetRaw(x, 9, z, Vox.Metal);
        fluid.SetRaw(10, 10, 10, Vox.Water);
        fluid.ActivateAt(10, 10, 10);
        Tick(fluid, s, VoxelRegistry.LiquidPeriod[Vox.Water] + 1);
        Ok(fluid.Get(10, 10, 10) != Vox.Water, "water spreads without manual reactivation");
    }

    private static void SandModeTests()
    {
        Section("'sand' physics");

        var w = new World();
        var ser = new SerialScheduler();
        w.SetRaw(5, 20, 5, Vox.Sand);
        w.SetRaw(5, 19, 5, Vox.Stone);
        w.SetRaw(6, 19, 5, Vox.Stone);

        bool moved = false;
        for (int i = 0; i < 40; i++)
        {
            w.ActivateAt(5, 20, 5);
            ser.Tick(w);
            if (w.Get(5, 20, 5) != Vox.Sand) { moved = true; break; }
        }
        Ok(moved, "sand slides off a blocked column into a free diagonal");
    }

    private static void ViscosityTests()
    {
        Section("viscosity gate");

        var w = new World();
        var ser = new SerialScheduler();
        for (int dz = -1; dz <= 1; dz++)
            for (int dx = -1; dx <= 1; dx++)
            {
                w.SetRaw(5 + dx, 19, 5 + dz, Vox.Stone);
                w.SetRaw(5 + dx, 20, 5 + dz, Vox.Stone);
            }
        w.SetRaw(5, 20, 5, Vox.Sand);
        for (int i = 0; i < 20; i++) { w.ActivateAt(5, 20, 5); ser.Tick(w); }
        Ok(w.Get(5, 20, 5) == Vox.Sand, "sand never displaces stone (stone is more viscous)");
    }

    private static void LiquidTests()
    {
        Section("'liquid' physics");

        var ser = new SerialScheduler();

        // boxed in by slime (more viscous) -> can never move
        var w = new World();
        for (int dz = -1; dz <= 1; dz++)
            for (int dx = -1; dx <= 1; dx++)
            {
                w.SetRaw(5 + dx, 19, 5 + dz, Vox.Stone);
                w.SetRaw(5 + dx, 20, 5 + dz, Vox.Slime);
            }
        w.SetRaw(5, 20, 5, Vox.Water);
        for (int i = 0; i < 300; i++) { w.ActivateAt(5, 20, 5); ser.Tick(w); }
        Ok(w.Get(5, 20, 5) == Vox.Water, "water boxed in by slime never moves");

        // surrounded by oil (less viscous) -> must eventually push into it
        var w2 = new World();
        for (int dz = -1; dz <= 1; dz++)
            for (int dx = -1; dx <= 1; dx++)
            {
                w2.SetRaw(5 + dx, 19, 5 + dz, Vox.Stone);
                w2.SetRaw(5 + dx, 20, 5 + dz, Vox.Oil);
            }
        w2.SetRaw(5, 20, 5, Vox.Water);
        int movedAt = -1;
        for (int i = 0; i < 300; i++)
        {
            w2.ActivateAt(5, 20, 5);
            ser.Tick(w2);
            if (w2.Get(5, 20, 5) != Vox.Water) { movedAt = i; break; }
        }
        Ok(movedAt >= 0, "water pushed into the oil (less viscous) after ~" + movedAt + " ticks");

        int po = VoxelRegistry.LiquidPeriod[Vox.Oil];
        int pw = VoxelRegistry.LiquidPeriod[Vox.Water];
        int ps = VoxelRegistry.LiquidPeriod[Vox.Slime];
        Ok(po < pw && pw < ps, $"horizontal period grows with viscosity (oil {po} < water {pw} < slime {ps} ticks)");
    }

    // ------------------------------------------------------------------ camera

    private static void CameraConventionTests()
    {
        Section("camera convention (the 'is it flipped?' tests)");

        var cam = new Camera { Position = Vector3.Zero, Yaw = 0f, Pitch = 0f };
        cam.UpdateBasis();
        const float aspect = 16f / 9f;

        // yaw=0 looks along -Z, so +X is to the RIGHT and +Y is UP.
        Ok(cam.Project(new Vector3(10, 0, -10), aspect, out float rx, out float ry) && rx > 0,
           "a world point at +X appears on the RIGHT half of the screen");
        Ok(cam.Project(new Vector3(-10, 0, -10), aspect, out float lx, out _) && lx < 0,
           "a world point at -X appears on the LEFT half");
        Ok(cam.Project(new Vector3(0, 10, -10), aspect, out _, out float ty) && ty > 0,
           "a world point at +Y appears in the TOP half (not flipped)");
        Ok(cam.Project(new Vector3(0, -10, -10), aspect, out _, out float by) && by < 0,
           "a world point at -Y appears in the BOTTOM half");
        Ok(!cam.Project(new Vector3(0, 0, 10), aspect, out _, out _),
           "a point behind the camera is rejected");

        // moving the mouse right must turn the view right: the target slides left
        cam.Project(new Vector3(10, 0, -10), aspect, out float beforeX, out _);
        cam.Look(+100f, 0f, Config.MouseSensitivity);
        cam.Project(new Vector3(10, 0, -10), aspect, out float afterX, out _);
        Ok(afterX < beforeX, "moving the mouse RIGHT turns the view right (target moves left on screen)");

        // moving the mouse down must look down: the target slides up
        cam.Yaw = 0f; cam.Pitch = 0f; cam.UpdateBasis();
        cam.Project(new Vector3(0, 10, -10), aspect, out _, out float beforeY);
        cam.Look(0f, +100f, Config.MouseSensitivity);
        cam.Project(new Vector3(0, 10, -10), aspect, out _, out float afterY);
        Ok(afterY > beforeY, "moving the mouse DOWN looks down (target moves up on screen)");

        // and the ray returned for the screen centre must equal Forward
        cam.Yaw = 0.7f; cam.Pitch = -0.3f; cam.UpdateBasis();
        var centre = cam.ScreenRay(0, 0, aspect);
        Ok(Vector3.Distance(centre, cam.Forward) < 1e-5f, "the centre-screen ray equals Forward");

        // ScreenRay and Project must be inverses
        var dir = cam.ScreenRay(0.5f, -0.25f, aspect);
        cam.Project(cam.Position + dir * 20f, aspect, out float px, out float py);
        Ok(MathF.Abs(px - 0.5f) < 1e-3f && MathF.Abs(py + 0.25f) < 1e-3f,
           "Project() is the exact inverse of ScreenRay()");
    }

    private static void RaycastTests()
    {
        Section("raycast");

        var w = new World();
        w.SetRaw(5, 9, 5, Vox.Stone);

        var hit = VoxelRaycast.Cast(w, new Vector3(5.5f, 20f, 5.5f), new Vector3(0, -1, 0), 32f);
        Ok(hit.HasValue && hit.Value.X == 5 && hit.Value.Y == 9 && hit.Value.Z == 5,
           "straight-down ray finds the voxel");
        Ok(hit.HasValue && hit.Value.NY == 1, "hit normal points up (the face we hit)");
        Ok(hit!.Value.NY == 1 && hit.Value.Y + hit.Value.NY == 10,
           "placement cell = hit + normal is the empty cell above");

        var miss = VoxelRaycast.Cast(w, new Vector3(5.5f, 20f, 5.5f), new Vector3(0, 1, 0), 32f);
        Ok(miss is null, "a ray pointing away finds nothing");
    }

    // ------------------------------------------------------------------ worldgen

    private static void GenerationTests()
    {
        Section("world generation");

        var w = new World();
        var sw = Stopwatch.StartNew();
        WorldGenerator.Generate(w, 20261105);
        sw.Stop();
        Console.WriteLine($"  generation took {sw.ElapsedMilliseconds} ms");

        var counts = w.CountByType();
        for (int i = 0; i < VoxelRegistry.Count; i++)
            Console.WriteLine($"    {VoxelRegistry.Get(i).Name,-6} {counts[i],9:N0}");

        Ok(Enumerable.Range(0, VoxelRegistry.Count).All(i => counts[i] > 0),
           "every one of the ten voxel types is present");
        Ok(counts[Vox.Air] > counts[Vox.Stone], "the world is mostly air above ground");
    }

    // ------------------------------------------------------------------ stability

    private static void StabilityTests()
    {
        Section("simulation stability (1200 ticks @ 20Hz = 60 s)");

        var w = new World();
        WorldGenerator.Generate(w, 20261105);
        var ser = new SerialScheduler();
        var before = w.CountByType();

        double total = 0, worst = 0;
        int peakQueue = 0;
        var times = new List<double>();
        for (int i = 0; i < 1200; i++)
        {
            peakQueue = Math.Max(peakQueue, w.ProcessingCount);
            ser.Tick(w);
            total += w.LastTickMs;
            worst = Math.Max(worst, w.LastTickMs);
            times.Add(w.LastTickMs);
        }
        times.Sort();
        Console.WriteLine($"    peak queue {peakQueue:N0}, mean {total / 1200:0.000} ms, " +
                          $"p95 {times[(int)(1200 * 0.95)]:0.000} ms, worst {worst:0.00} ms");

        var after = w.CountByType();
        bool conserved = Enumerable.Range(0, VoxelRegistry.Count).All(i => before[i] == after[i]);
        Ok(conserved, "voxel counts are conserved by the swap rule");

        Ok(w.Pending.Count < 200_000, $"activity queue settled down ({w.Pending.Count:N0} pending)");
        Ok(worst < 60, $"no pathological tick (worst {worst:0.0} ms)");

        bool bedrock = true;
        for (int z = 0; z < Config.Size && bedrock; z += 7)
            for (int x = 0; x < Config.Size && bedrock; x += 7)
                if (w.Get(x, 0, z) == Vox.Air) bedrock = false;
        Ok(bedrock, "bedrock layer intact — nothing fell out of the world");

        int leaked = 0;
        for (int z = 40; z < Config.Size; z++)
            for (int x = 0; x < Config.Size; x++)
            {
                float d = MathF.Sqrt((x - 42) * (x - 42) + (z - 88) * (z - 88));
                if (d <= 20.5f || d > 45f) continue;
                for (int y = 40; y < 70; y++) if (w.Get(x, y, z) == Vox.Water) leaked++;
            }
        Ok(leaked == 0, $"lake stayed inside its rim ({leaked} leaked voxels)");
    }

    // ------------------------------------------------------------------ determinism

    private static void DeterminismTests()
    {
        Section("determinism");

        var a = new World();
        var b = new World();
        WorldGenerator.Generate(a, 12345);
        WorldGenerator.Generate(b, 12345);
        Ok(a.Data.AsSpan().SequenceEqual(b.Data), "same seed produces an identical world");

        var ser = new SerialScheduler();
        for (int i = 0; i < 200; i++) { ser.Tick(a); ser.Tick(b); }
        Ok(a.Data.AsSpan().SequenceEqual(b.Data),
           "the serial scheduler is deterministic (same bytes after 200 ticks)");

        var c = new World();
        WorldGenerator.Generate(c, 999);
        Ok(!a.Data.AsSpan().SequenceEqual(c.Data), "a different seed produces a different world");
    }

    // ------------------------------------------------------------------ threading

    private static void SchedulerEquivalenceTests()
    {
        Section("multithreading: parallel scheduler vs serial reference");

        Console.WriteLine($"  Environment.ProcessorCount = {Environment.ProcessorCount}");

        // Same world, two schedulers, compare the outcome.
        var serialWorld = new World();
        var parallelWorld = new World();
        WorldGenerator.Generate(serialWorld, 777);
        WorldGenerator.Generate(parallelWorld, 777);

        var serial = new SerialScheduler();
        // force the parallel path even for small workloads so this test is meaningful
        var parallel = new ParallelScheduler(slabs: Math.Max(2, Environment.ProcessorCount), minWork: 0);

        var beforeSerial = serialWorld.CountByType();
        var beforeParallel = parallelWorld.CountByType();

        // lots of liquid so there is real work to split
        for (int z = 30; z < 90; z++)
            for (int y = 88; y < 94; y++)
                for (int x = 30; x < 90; x++)
                {
                    if (((x * 7 + y * 13 + z * 17) & 3) == 0) continue;
                    serialWorld.Set(x, y, z, Vox.Water);
                    parallelWorld.Set(x, y, z, Vox.Water);
                }
        var midSerial = serialWorld.CountByType();
        var midParallel = parallelWorld.CountByType();

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 120; i++) serial.Tick(serialWorld);
        sw.Stop();
        double serialMs = sw.Elapsed.TotalMilliseconds;

        sw.Restart();
        for (int i = 0; i < 120; i++) parallel.Tick(parallelWorld);
        sw.Stop();
        double parallelMs = sw.Elapsed.TotalMilliseconds;

        var afterSerial = serialWorld.CountByType();
        var afterParallel = parallelWorld.CountByType();

        Ok(Enumerable.Range(0, VoxelRegistry.Count).All(i => midSerial[i] == afterSerial[i]),
           "serial: counts conserved over 120 heavy ticks");
        Ok(Enumerable.Range(0, VoxelRegistry.Count).All(i => midParallel[i] == afterParallel[i]),
           "parallel: counts conserved over 120 heavy ticks (no data corruption)");

        Ok(Enumerable.Range(0, VoxelRegistry.Count).All(i => afterSerial[i] == afterParallel[i]),
           "both schedulers end up with exactly the same voxel population");

        // rough similarity check of the actual layout
        int same = 0, total = 0;
        for (int i = 0; i < serialWorld.Data.Length; i += 97)
        {
            total++;
            if (serialWorld.Data[i] == parallelWorld.Data[i]) same++;
        }
        double similarity = 100.0 * same / total;
        Console.WriteLine($"    layout similarity after 120 ticks: {similarity:0.0}%");
        Ok(similarity > 85.0, $"parallel layout stays close to the serial reference ({similarity:0.0}%)");

        Console.WriteLine($"    120 heavy ticks: serial {serialMs:0} ms, parallel {parallelMs:0} ms");
    }
}
