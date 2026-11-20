using System.Numerics;

namespace VoxelEngine;

/// <summary>
/// The camera, and the ONE place where the coordinate convention is defined.
///
/// CONVENTION (memorise this, most look/aim bugs are conventions disagreeing):
///   * Right-handed world. +X right, +Y UP, +Z toward the viewer.
///   * Yaw = 0, Pitch = 0 means looking along **-Z** (the classic OpenGL
///     "into the screen" direction).
///   * Pitch is positive when looking UP.
///   * Right = (cos yaw, 0, sin yaw);  Up = Right x Forward.
///   * NDC on screen: x = +1 is the RIGHT edge, y = +1 is the TOP edge.
///
/// The renderer builds its rays with exactly this basis, and the interaction
/// raycast uses exactly this basis, so what the crosshair points at is what you
/// dig. <see cref="Project"/> is the inverse of that and exists so the
/// convention can be unit-tested instead of eyeballed — see EngineTests.
/// </summary>
public sealed class Camera
{
    public Vector3 Position;
    public float Yaw;
    public float Pitch;
    public float FovY = Config.FovY;

    public Vector3 Forward { get; private set; } = new(0, 0, -1);
    public Vector3 Right { get; private set; } = new(1, 0, 0);
    public Vector3 Up { get; private set; } = new(0, 1, 0);

    public void UpdateBasis()
    {
        float cy = MathF.Cos(Yaw), sy = MathF.Sin(Yaw);
        float cp = MathF.Cos(Pitch), sp = MathF.Sin(Pitch);

        Forward = new Vector3(sy * cp, sp, -cy * cp);
        Right = new Vector3(cy, 0f, sy);
        Up = Vector3.Cross(Right, Forward);
    }

    /// <summary>Mouse/keyboard look. +dx = turn right, +dy (mouse down) = look down.</summary>
    public void Look(float dx, float dy, float sensitivity)
    {
        if (Config.InvertLookX) dx = -dx;
        if (Config.InvertLookY) dy = -dy;

        Yaw += dx * sensitivity;
        Pitch -= dy * sensitivity;

        const float limit = MathF.PI / 2f - 0.001f;
        Pitch = Math.Clamp(Pitch, -limit, limit);

        // keep yaw bounded so it never loses float precision in a long session
        if (Yaw > MathF.PI) Yaw -= MathF.Tau;
        else if (Yaw < -MathF.PI) Yaw += MathF.Tau;

        UpdateBasis();
    }

    /// <summary>Ray through a point on the screen. ndcX/ndcY in [-1, 1].</summary>
    public Vector3 ScreenRay(float ndcX, float ndcY, float aspect)
    {
        float th = MathF.Tan(FovY * 0.5f);
        return Vector3.Normalize(Forward + Right * (ndcX * aspect * th) + Up * (ndcY * th));
    }

    /// <summary>
    /// Inverse of <see cref="ScreenRay"/>: where does this world point land on
    /// screen? Returns false if it is behind the camera.
    /// </summary>
    public bool Project(Vector3 worldPoint, float aspect, out float ndcX, out float ndcY)
    {
        Vector3 v = worldPoint - Position;
        float depth = Vector3.Dot(v, Forward);
        ndcX = 0f; ndcY = 0f;
        if (depth <= 1e-4f) return false;

        float th = MathF.Tan(FovY * 0.5f);
        ndcX = Vector3.Dot(v, Right) / (depth * th * aspect);
        ndcY = Vector3.Dot(v, Up) / (depth * th);
        return true;
    }
}
