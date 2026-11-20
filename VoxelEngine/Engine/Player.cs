using System.Numerics;

namespace VoxelEngine;

/// <summary>
/// A snapshot of the controls for one frame.
///
/// WHY A STRUCT AND NOT THE KEYBOARD STATE DIRECTLY: the engine layer must not
/// depend on the windowing library. Collecting input into a plain struct at the
/// edge of the program means the player can be driven by a test, a replay, or a
/// network message just as easily as by a keyboard — and it keeps the engine
/// portable if you ever swap OpenTK for something else.
/// </summary>
public struct InputFrame
{
    public bool Forward, Back, Left, Right, Up, Down;
    public float LookDeltaX, LookDeltaY;
    public bool Dig, Place;          // edge-triggered by the caller
    public int SelectSlot;           // -1 = unchanged, 0..9 = hotbar slot
}

/// <summary>
/// The player: a flying point with a camera. No collision, no gravity — that is
/// the next thing to add, and it is deliberately not here yet.
/// </summary>
public sealed class Player
{
    public readonly Camera Camera = new();
    public byte Selected = Vox.Dirt;
    public float Speed = Config.MoveSpeed;

    public Player()
    {
        Camera.Position = new Vector3(36.5f, 102.5f, 124.5f);
        Camera.Yaw = -0.28f;
        Camera.Pitch = -0.40f;
        Camera.UpdateBasis();
    }

    public void Update(World world, float dt, in InputFrame input)
    {
        Camera.Look(input.LookDeltaX, input.LookDeltaY, Config.MouseSensitivity);

        Vector3 move = Vector3.Zero;
        var f = Camera.Forward;
        var r = Camera.Right;

        if (input.Forward) { move += f; }
        if (input.Back) { move -= f; }
        if (input.Right) { move += r; }
        if (input.Left) { move -= r; }
        if (input.Up) { move.Y += 1f; }
        if (input.Down) { move.Y -= 1f; }

        // normalise so diagonals are not faster than straight lines
        if (move.LengthSquared() > 1e-6f)
        {
            move = Vector3.Normalize(move);
            Camera.Position += move * Speed * dt;
        }

        // stay inside the world so the renderer never has to handle an outside camera
        float lim = world.Size - 0.01f;
        Camera.Position = Vector3.Clamp(Camera.Position, new Vector3(0.01f), new Vector3(lim));

        if (input.SelectSlot >= 0) Selected = (byte)Math.Clamp(input.SelectSlot, 0, 9);

        if (input.Dig) Dig(world);
        if (input.Place) Place(world);
    }

    /// <summary>Remove the voxel under the crosshair.</summary>
    public bool Dig(World world)
    {
        var hit = VoxelRaycast.Cast(world, Camera.Position, Camera.Forward, Config.Reach);
        if (hit is null) return false;
        return world.Set(hit.Value.X, hit.Value.Y, hit.Value.Z, Vox.Air);
    }

    /// <summary>Place the selected voxel in the empty cell the crosshair is facing.</summary>
    public bool Place(World world)
    {
        if (Selected == Vox.Air) return false;
        var hit = VoxelRaycast.Cast(world, Camera.Position, Camera.Forward, Config.Reach);
        if (hit is null) return false;

        var h = hit.Value;
        int tx = h.X + h.NX, ty = h.Y + h.NY, tz = h.Z + h.NZ;
        if (!world.InBounds(tx, ty, tz)) return false;
        if (world.Get(tx, ty, tz) != Vox.Air) return false;

        // do not entomb the camera
        var p = Camera.Position;
        if (tx == (int)MathF.Floor(p.X) && ty == (int)MathF.Floor(p.Y) && tz == (int)MathF.Floor(p.Z))
            return false;

        return world.Set(tx, ty, tz, Selected);
    }
}
