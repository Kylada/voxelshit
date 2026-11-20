namespace VoxelEngine;

/// <summary>
/// Every tunable number lives here so it is easy to find and change.
///
/// WHY A SEPARATE CONFIG: game code gets unreadable fast when magic numbers are
/// scattered through it. One place also makes it trivial to later load these
/// from a file instead of recompiling.
/// </summary>
public static class Config
{
    // ---- world ----
    /// <summary>Edge length of the world in voxels. World volume = Size^3.</summary>
    public const int Size = 128;

    /// <summary>
    /// The world array is padded by 1 voxel on every side and that shell is
    /// permanently solid stone. See World.cs — it removes bounds checks from
    /// the physics hot loop entirely.
    /// </summary>
    public const int Pad = 1;

    public const int PaddedSize = Size + Pad * 2;   // 130

    // ---- simulation ----
    /// <summary>
    /// Physics runs at a FIXED rate, independent of the framerate. This is the
    /// single most important decision in the whole project; see README §"fixed
    /// timestep". Changing it changes liquid speed: a liquid's horizontal move
    /// period is measured in ticks.
    /// </summary>
    public const int PhysicsTickHz = 20;

    /// <summary>Horizontal move period = viscosity * this many ticks (0 => every tick).</summary>
    public const float LiquidPeriodScale = 100f;

    /// <summary>Safety valve: never simulate more than this many ticks in one frame.</summary>
    public const int MaxTicksPerFrame = 6;

    // ---- player ----
    public const float Reach = 8f;              // how far you can dig / place, in voxels
    public const float MoveSpeed = 16f;         // voxels per second
    public const float MouseSensitivity = 0.0022f;
    public const float FovY = 1.25f;            // radians, ~72 degrees

    /// <summary>Flip the look axes if the controls feel wrong to you. See Camera.cs.</summary>
    public static bool InvertLookX = false;
    public static bool InvertLookY = false;

    // ---- rendering ----
    public const int WindowWidth = 1280;
    public const int WindowHeight = 720;
}
