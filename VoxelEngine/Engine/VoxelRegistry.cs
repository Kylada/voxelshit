using System.Numerics;

namespace VoxelEngine;

/// <summary>How a voxel type behaves in the simulation. Stored as a byte.</summary>
public enum PhysicsMode : byte
{
    /// <summary>Never moves (stone, wood, metal, fire). The structure everything rests on.</summary>
    None = 0,
    /// <summary>Falls straight down only.</summary>
    Solid = 1,
    /// <summary>Falls straight down, and slides diagonally if it can.</summary>
    Sand = 2,
    /// <summary>Falls, slides, and also creeps horizontally on a timer.</summary>
    Liquid = 3,
    /// <summary>Falls when no fixable voxel touches a side or the top.</summary>
    Fixable = 4,
}

/// <summary>
/// One voxel type. These are the six core properties you asked for plus the
/// render hints the shader needs.
/// </summary>
public sealed class VoxelDef
{
    public int Id { get; internal set; }
    public string Name { get; init; } = "unnamed";

    // ---- the six requested properties ----
    public Vector3 Color { get; init; } = new(1f, 0f, 1f);
    /// <summary>0 = opaque, 1 = fully see-through. Render alpha is (1 - this).</summary>
    public float Transparency { get; init; }
    /// <summary>0 = void/air, 1 = fully rigid. Decides who displaces whom.</summary>
    public float Viscosity { get; init; }
    public PhysicsMode Physics { get; init; } = PhysicsMode.None;
    /// <summary>Stub for now — carried, not simulated.</summary>
    public float Temperature { get; init; } = 20f;

    // ---- render hints ----
    /// <summary>Procedural texture id in the fragment shader (0..6).</summary>
    public int Pattern { get; init; }
    /// <summary>Glow [0..1]. FlickerProperty animates this at runtime.</summary>
    public float Emissive { get; set; }

    /// <summary>Extra behaviour. Empty for most types.</summary>
    public List<IVoxelProperty> Properties { get; init; } = new();

    public override string ToString() => $"{Name} (v={Viscosity:0.00}, {Physics})";
}

/// <summary>
/// The palette. Register voxel types at startup; ids are just indices, so a
/// voxel is one byte in the world array and the whole world stays cache-friendly.
///
/// WHY STATIC ARRAYS AS LOOKUP TABLES: the physics loop asks "what is the
/// viscosity of the voxel below me?" about a hundred thousand times per tick.
/// Reading `Registry.Get(id).Viscosity` would mean an array lookup, then a
/// pointer dereference, then a field read — three dependent memory accesses.
/// Reading `Registry.Viscosity[id]` is one. Duplicating the data into parallel
/// flat arrays is the classic data-oriented trade (more memory, far fewer
/// cache misses) and it is why this loop runs at millions of voxels/second.
/// </summary>
public static class VoxelRegistry
{
    private static readonly List<VoxelDef> _defs = new();

    public static IReadOnlyList<VoxelDef> Defs => _defs;
    public static int Count => _defs.Count;

    /// <summary>Bumped whenever a definition changes; the renderer re-uploads its palette.</summary>
    public static int PaletteVersion { get; set; } = 0;

    // ---- hot-loop lookup tables, indexed by voxel id ----
    public static readonly float[] Viscosity = new float[256];
    public static readonly PhysicsMode[] Physics = new PhysicsMode[256];
    public static readonly int[] LiquidPeriod = new int[256];

    public static VoxelDef Define(VoxelDef def)
    {
        if (_defs.Count >= 256) throw new InvalidOperationException("palette is limited to 256 voxel types");
        def.Id = _defs.Count;
        _defs.Add(def);

        Viscosity[def.Id] = def.Viscosity;
        Physics[def.Id] = def.Physics;
        // "move horizontally every viscosity*100 ticks; viscosity 0 => every tick"
        LiquidPeriod[def.Id] = def.Viscosity <= 0f
            ? 1
            : Math.Max(1, (int)MathF.Round(def.Viscosity * Config.LiquidPeriodScale));

        foreach (var p in def.Properties) p.OnAttach(def);
        PaletteVersion++;
        return def;
    }

    public static VoxelDef Get(int id) => _defs[id];
}

/// <summary>Voxel ids, so the code reads as English instead of numbers.</summary>
public static class Vox
{
    public const byte Air = 0;
    public const byte Dirt = 1;
    public const byte Water = 2;
    public const byte Stone = 3;
    public const byte Oil = 4;
    public const byte Fire = 5;
    public const byte Wood = 6;
    public const byte Metal = 7;
    public const byte Slime = 8;
    public const byte Sand = 9;
}

/// <summary>Registers the ten starting voxel types.</summary>
public static class VoxelPalette
{
    public static void RegisterDefaults()
    {
        if (VoxelRegistry.Count > 0) return;

        VoxelRegistry.Define(new VoxelDef
        {
            Name = "Air", Color = Hex(0x0b0d10), Transparency = 1.00f, Viscosity = 0.00f,
            Physics = PhysicsMode.None, Temperature = 20f, Pattern = 0,
        });
        VoxelRegistry.Define(new VoxelDef
        {
            Name = "Dirt", Color = Hex(0x7a5230), Transparency = 0.00f, Viscosity = 0.70f,
            Physics = PhysicsMode.Solid, Temperature = 20f, Pattern = 2,
            Properties = { new TemperatureProperty(20f) },
        });
        VoxelRegistry.Define(new VoxelDef
        {
            Name = "Water", Color = Hex(0x2f6fd0), Transparency = 0.62f, Viscosity = 0.15f,
            Physics = PhysicsMode.Liquid, Temperature = 15f, Pattern = 0,
        });
        VoxelRegistry.Define(new VoxelDef
        {
            Name = "Stone", Color = Hex(0x7d7d86), Transparency = 0.00f, Viscosity = 1.00f,
            Physics = PhysicsMode.Fixable, Temperature = 18f, Pattern = 1,
        });
        VoxelRegistry.Define(new VoxelDef
        {
            Name = "Oil", Color = Hex(0x3a2a1a), Transparency = 0.28f, Viscosity = 0.10f,
            Physics = PhysicsMode.Liquid, Temperature = 22f, Pattern = 0,
        });
        VoxelRegistry.Define(new VoxelDef
        {
            Name = "Fire", Color = Hex(0xff8c1a), Transparency = 0.40f, Viscosity = 0.02f,
            Physics = PhysicsMode.None, Temperature = 800f, Pattern = 6, Emissive = 0.85f,
            Properties = { new FlickerProperty(), new TemperatureProperty(800f) },
        });
        VoxelRegistry.Define(new VoxelDef
        {
            Name = "Wood", Color = Hex(0x8a5a2b), Transparency = 0.00f, Viscosity = 0.95f,
            Physics = PhysicsMode.None, Temperature = 20f, Pattern = 3,
        });
        VoxelRegistry.Define(new VoxelDef
        {
            Name = "Metal", Color = Hex(0xb9c2cc), Transparency = 0.00f, Viscosity = 1.00f,
            Physics = PhysicsMode.None, Temperature = 20f, Pattern = 4,
        });
        VoxelRegistry.Define(new VoxelDef
        {
            Name = "Slime", Color = Hex(0x5fd44a), Transparency = 0.25f, Viscosity = 0.38f,
            Physics = PhysicsMode.Liquid, Temperature = 20f, Pattern = 5, Emissive = 0.12f,
        });
        VoxelRegistry.Define(new VoxelDef
        {
            Name = "Sand", Color = Hex(0xe2c98a), Transparency = 0.00f, Viscosity = 0.50f,
            Physics = PhysicsMode.Sand, Temperature = 24f, Pattern = 5,
        });
    }

    private static Vector3 Hex(int rgb) => new(
        ((rgb >> 16) & 0xFF) / 255f,
        ((rgb >> 8) & 0xFF) / 255f,
        (rgb & 0xFF) / 255f);
}
