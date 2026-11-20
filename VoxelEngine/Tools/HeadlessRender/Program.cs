using OpenTK.Graphics.OpenGL4;
using System.Numerics;
using VoxelEngine;

namespace HeadlessRender;

/// <summary>
/// Renders frames of the voxel world with no window and no display.
///
///   dotnet run --project Tools/HeadlessRender -- out.ppm [width] [height] [frames]
///
/// Used to verify the renderer and the camera convention automatically.
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--markers")
            return MarkerTest.Run("/tmp/markers.ppm",
                args.Length > 1 ? int.Parse(args[1]) : 800,
                args.Length > 2 ? int.Parse(args[2]) : 450);

        string outPath = args.Length > 0 ? args[0] : "frame.ppm";
        int width = args.Length > 1 ? int.Parse(args[1]) : 960;
        int height = args.Length > 2 ? int.Parse(args[2]) : 540;
        int settleTicks = args.Length > 3 ? int.Parse(args[3]) : 240;

        Egl.Initialise();

        Console.WriteLine("  building world...");
        VoxelPalette.RegisterDefaults();
        var world = new World();
        WorldGenerator.Generate(world, 20261105);

        var scheduler = new SerialScheduler();
        for (int i = 0; i < settleTicks; i++) scheduler.Tick(world);
        Console.WriteLine($"  settled after {settleTicks} ticks (active {world.Pending.Count:N0})");

        var camera = new Camera
        {
            Position = new Vector3(36.5f, 102.5f, 124.5f),
            Yaw = -0.28f,
            Pitch = -0.40f,
        };
        camera.UpdateBasis();

        var (fbo, color, depth) = Offscreen.Create(width, height);

        using (var renderer = new VoxelRenderer(world))
        {
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
            renderer.Render(camera, width, height, 1.5);
            GL.Finish();

            var pixels = Offscreen.ReadPixels(width, height);
            Offscreen.WritePpm(outPath, pixels, width, height);

            // quick sanity: is the image actually varied, and where is the horizon?
            long sum = 0;
            int min = 255, max = 0;
            for (int i = 0; i < pixels.Length; i += 4)
            {
                int v = pixels[i];
                sum += v;
                min = Math.Min(min, v);
                max = Math.Max(max, v);
            }
            Console.WriteLine($"  wrote {outPath}  ({width}x{height})  mean red {sum / (double)(pixels.Length / 4):0.0}  range {min}..{max}");
        }

        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        GL.DeleteFramebuffer(fbo);
        GL.DeleteRenderbuffer(color);
        GL.DeleteRenderbuffer(depth);
        return 0;
    }
}
