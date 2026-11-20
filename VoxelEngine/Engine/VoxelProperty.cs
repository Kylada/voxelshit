using System.Numerics;

namespace VoxelEngine;

/// <summary>
/// The extension point: implement this to bolt behaviour onto a voxel type
/// without touching the engine.
///
/// WHY AN INTERFACE (vs. the JS version's base class): C# interfaces support
/// *default implementations* (C# 8+), so every hook below is optional — you
/// implement only the ones you care about and the rest come free. C# also lets
/// the compiler check at compile time that your property type is complete and
/// that you did not typo a hook name, which JS could never do.
///
/// IMPORTANT LIMITATION, and the reason this is per-*type* and not per-*voxel*:
/// these objects live once per voxel type (10 of them), not once per voxel
/// (2.1 million of them). If you gave every voxel its own object you would get
/// millions of heap allocations, millions of pointers to chase, and GC pauses
/// in the middle of a frame. For state that varies per voxel (temperature,
/// damage, moisture) use a <see cref="VoxelChannel{T}"/> instead — a parallel
/// array, one entry per voxel. See World.Channel&lt;T&gt;().
/// </summary>
public interface IVoxelProperty
{
    /// <summary>Stable identifier, handy for debugging and serialisation.</summary>
    string Key { get; }

    /// <summary>Called once, when the owning voxel type is registered.</summary>
    void OnAttach(VoxelDef def) { }

    /// <summary>
    /// Called every physics tick for every *active* voxel of this type.
    /// Return false to veto the engine's own movement rules for this voxel.
    /// </summary>
    bool OnTick(World world, int index, VoxelDef def) => true;

    /// <summary>Called when a voxel of this type appears in the world.</summary>
    void OnPlaced(World world, int index, VoxelDef def) { }

    /// <summary>Called when a voxel of this type disappears.</summary>
    void OnRemoved(World world, int index, VoxelDef def) { }

    /// <summary>Called once per rendered frame.</summary>
    void OnFrame(VoxelDef def, double timeSeconds) { }

    /// <summary>Optional: override the render colour. null = use the voxel's own.</summary>
    Vector3? PaletteColor(VoxelDef def) => null;

    /// <summary>Optional: override emissiveness [0..1]. null = use the voxel's own.</summary>
    float? PaletteEmissive(VoxelDef def) => null;
}

/// <summary>
/// Example property: animated glow. Fire uses it, so if you watch the braziers
/// their brightness pulses. Note it mutates shared render state and bumps
/// <see cref="VoxelRegistry.PaletteVersion"/>, which is how the renderer learns
/// that its colour table is stale.
/// </summary>
public sealed class FlickerProperty : IVoxelProperty
{
    private readonly float _base, _amount, _hz;

    public FlickerProperty(float baseGlow = 0.85f, float amount = 0.3f, float hz = 3.1f)
    {
        _base = baseGlow; _amount = amount; _hz = hz;
    }

    public string Key => "flicker";

    public void OnFrame(VoxelDef def, double t)
    {
        double v = _base + _amount * (
            0.6 * Math.Sin(t * _hz * Math.Tau) +
            0.4 * Math.Sin(t * _hz * 17.3 + 1.7));

        float clamped = Math.Clamp((float)v, 0f, 1f);
        if (MathF.Abs(clamped - def.Emissive) > 0.004f)
        {
            def.Emissive = clamped;
            VoxelRegistry.PaletteVersion++;
        }
    }
}

/// <summary>
/// Example property: a data carrier. Demonstrates that a property does not have
/// to *do* anything — this one just holds data that a future system (heat
/// diffusion, fire spreading) would read.
/// </summary>
public sealed class TemperatureProperty : IVoxelProperty
{
    public float Celsius { get; }
    public float Conductivity { get; }

    public TemperatureProperty(float celsius, float conductivity = 0.05f)
    {
        Celsius = celsius; Conductivity = conductivity;
    }

    public string Key => "temperature";
}
