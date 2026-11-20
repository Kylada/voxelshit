using System.Numerics;

namespace VoxelEngine;

public readonly struct RayHit
{
    public readonly int X, Y, Z;
    public readonly byte Id;
    /// <summary>Face normal of the face we hit — where a placed voxel goes.</summary>
    public readonly int NX, NY, NZ;
    public readonly float Distance;

    public RayHit(int x, int y, int z, byte id, int nx, int ny, int nz, float distance)
    {
        X = x; Y = y; Z = z; Id = id; NX = nx; NY = ny; NZ = nz; Distance = distance;
    }
}

/// <summary>
/// Grid traversal ("DDA", after Amanatides &amp; Woo) — the standard way to walk a
/// voxel grid with a ray.
///
/// WHY NOT STEP IN SMALL INCREMENTS: a fixed 0.1-voxel step is 10x slower and
/// can still tunnel through a corner. DDA instead computes exactly when the ray
/// crosses each cell boundary and jumps straight to the next cell, so it visits
/// only the cells the ray actually passes through — typically 5-20 of them for
/// a reach of 8 voxels. The GPU shader uses the same algorithm per pixel.
/// </summary>
public static class VoxelRaycast
{
    public static RayHit? Cast(World world, Vector3 origin, Vector3 dir, float maxDistance)
    {
        int x = (int)MathF.Floor(origin.X);
        int y = (int)MathF.Floor(origin.Y);
        int z = (int)MathF.Floor(origin.Z);

        int stepX = dir.X >= 0 ? 1 : -1;
        int stepY = dir.Y >= 0 ? 1 : -1;
        int stepZ = dir.Z >= 0 ? 1 : -1;

        // guard against divide-by-zero for axis-aligned rays
        const float eps = 1e-7f;
        float dx = MathF.Max(MathF.Abs(dir.X), eps);
        float dy = MathF.Max(MathF.Abs(dir.Y), eps);
        float dz = MathF.Max(MathF.Abs(dir.Z), eps);

        float tDeltaX = 1f / dx, tDeltaY = 1f / dy, tDeltaZ = 1f / dz;

        float tMaxX = (stepX > 0 ? (x + 1 - origin.X) : (origin.X - x)) * tDeltaX;
        float tMaxY = (stepY > 0 ? (y + 1 - origin.Y) : (origin.Y - y)) * tDeltaY;
        float tMaxZ = (stepZ > 0 ? (z + 1 - origin.Z) : (origin.Z - z)) * tDeltaZ;

        int nx = 0, ny = 0, nz = 0;
        float t = 0f;

        for (int i = 0; i < 512; i++)
        {
            if (!world.InBounds(x, y, z)) return null;

            byte id = world.Get(x, y, z);
            if (id != Vox.Air) return new RayHit(x, y, z, id, nx, ny, nz, t);

            if (tMaxX <= tMaxY && tMaxX <= tMaxZ)
            {
                x += stepX; t = tMaxX; tMaxX += tDeltaX; nx = -stepX; ny = 0; nz = 0;
            }
            else if (tMaxY <= tMaxZ)
            {
                y += stepY; t = tMaxY; tMaxY += tDeltaY; nx = 0; ny = -stepY; nz = 0;
            }
            else
            {
                z += stepZ; t = tMaxZ; tMaxZ += tDeltaZ; nx = 0; ny = 0; nz = -stepZ;
            }

            if (t > maxDistance) return null;
        }
        return null;
    }
}
