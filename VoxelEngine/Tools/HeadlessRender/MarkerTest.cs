using OpenTK.Graphics.OpenGL4;
using System.Numerics;
using VoxelEngine;

namespace HeadlessRender;

/// <summary>
/// Proves the camera convention at the PIXEL level, not just in maths.
///
/// EngineTests already proves Camera.Project() matches Camera.ScreenRay(), but
/// the shader computes its own rays from uFwd/uRight/uUp. If the shader had a
/// sign error the unit tests would still pass while the image came out mirrored,
/// so this test reads back real pixels and checks where known markers landed.
///
/// Layout (camera at the world centre, yaw=0 so it looks along -Z):
///     FIRE   (orange) at +X, in front  -> must be on the RIGHT
///     SLIME  (green)  at -X, in front  -> must be on the LEFT
///     METAL  (white)  high up, in front -> must be in the UPPER half
///     SAND   (tan)    low down, in front -> must be in the LOWER half
/// </summary>
public static class MarkerTest
{
    public static int Run(string outPath, int width, int height)
    {
        Egl.Initialise();
        VoxelPalette.RegisterDefaults();

        var world = new World();
        // Empty world with a solid floor only, so the markers are unmistakable.
        for (int z = 0; z < world.Size; z++)
            for (int x = 0; x < world.Size; x++)
                for (int y = 0; y < 40; y++)
                    world.SetRaw(x, y, z, Vox.Stone);

        var centre = new Vector3(64f, 90f, 64f);
        // 25 voxels in front (-Z) and 8 off-axis: comfortably inside the frustum.
        Marker(world, 72, 90, 39, Vox.Fire);    // +X  -> right
        Marker(world, 56, 90, 39, Vox.Slime);   // -X  -> left
        Marker(world, 64, 98, 39, Vox.Metal);   // +Y  -> up
        Marker(world, 64, 82, 39, Vox.Sand);    // -Y  -> down

        var camera = new Camera { Position = centre, Yaw = 0f, Pitch = 0f };
        camera.UpdateBasis();

        var (fbo, color, depth) = Offscreen.Create(width, height);
        byte[] px;
        using (var renderer = new VoxelRenderer(world))
        {
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
            renderer.Render(camera, width, height, 0.0);
            GL.Finish();
            px = Offscreen.ReadPixels(width, height);
            Offscreen.WritePpm(outPath, px, width, height);
        }

        // Classify pixels by colour family and average their positions.
        double fireX = 0, fireY = 0, slimeX = 0, slimeY = 0, metalY = 0, sandY = 0;
        int nFire = 0, nSlime = 0, nMetal = 0, nSand = 0;

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = (y * width + x) * 4;
                int r = px[i], g = px[i + 1], b = px[i + 2];

                // fire: strongly orange (red high, blue low)
                if (r > 170 && g > 90 && g < 210 && b < 110) { fireX += x; fireY += y; nFire++; }
                // slime: strongly green (green dominant)
                else if (g > 130 && g - r > 40 && g - b > 40) { slimeX += x; slimeY += y; nSlime++; }
                // metal: desaturated grey (r,g,b all close, mid-bright). The sky is
                // strongly blue-biased so it cannot match this.
                else if (Math.Abs(r - g) < 16 && Math.Abs(g - b) < 16 && r > 85) { metalY += y; nMetal++; }
                // sand: tan (red high, blue clearly lower, green between)
                else if (r > 150 && g > 120 && b < r - 40 && g > b) { sandY += y; nSand++; }
            }
        }

        double cx = width / 2.0, cy = height / 2.0;
        bool ok = true;
        void Check(string what, double a, double b, int na, int nb, string axis)
        {
            bool pass = na > 30 && nb > 30 && a > b;
            Console.WriteLine($"  {(pass ? "PASS" : "FAIL")}  {what}: {axis} {a:0.0} vs {b:0.0}  " +
                              $"({na} / {nb} px, screen {axis}-centre {cx:0}/{cy:0})");
            if (!pass) ok = false;
        }

        Console.WriteLine();
        Check("orange (fire) is to the RIGHT of green (slime)", fireX / Math.Max(1, nFire), slimeX / Math.Max(1, nSlime), nFire, nSlime, "x");
        Console.WriteLine($"  screen centre x = {cx:0}, orange x = {fireX / Math.Max(1, nFire):0.0}, green x = {slimeX / Math.Max(1, nSlime):0.0}");

        bool metalAbove = nMetal > 30 && metalY / Math.Max(1, nMetal) < cy;
        bool sandBelow = nSand > 30 && sandY / Math.Max(1, nSand) > cy;
        Console.WriteLine($"  {(metalAbove ? "PASS" : "FAIL")}  white (metal) is in the UPPER half: y {metalY / Math.Max(1, nMetal):0.0} < {cy:0} ({nMetal} px)");
        Console.WriteLine($"  {(sandBelow ? "PASS" : "FAIL")}  tan (sand) is in the LOWER half: y {sandY / Math.Max(1, nSand):0.0} > {cy:0} ({nSand} px)");
        if (!metalAbove || !sandBelow) ok = false;

        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        GL.DeleteFramebuffer(fbo);
        GL.DeleteRenderbuffer(color);
        GL.DeleteRenderbuffer(depth);

        Console.WriteLine(ok ? "\n  CAMERA CONVENTION OK" : "\n  CAMERA CONVENTION BROKEN");
        return ok ? 0 : 1;
    }

    private static void Marker(World w, int x, int y, int z, byte id)
    {
        for (int dz = 0; dz < 3; dz++)
            for (int dy = 0; dy < 3; dy++)
                for (int dx = 0; dx < 3; dx++)
                    w.SetRaw(x + dx, y + dy, z + dz, id);
        w.Dirty = true;
    }
}
