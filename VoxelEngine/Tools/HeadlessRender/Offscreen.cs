using System.Runtime.InteropServices;
using OpenTK;
using OpenTK.Graphics.OpenGL4;

namespace HeadlessRender;

/// <summary>
/// Creates an OpenGL 3.3 core context with no window and no display, using EGL's
/// "surfaceless" platform on top of Mesa's software rasteriser.
///
/// This exists so the renderer can be tested and screenshotted on a machine with
/// no GPU and no X server (CI, containers, this sandbox). The game itself never
/// uses it.
/// </summary>
internal static class Egl
{
    private const string Lib = "libEGL.so.1";

    private const int EGL_NONE = 0x3038;
    private const int EGL_SURFACE_TYPE = 0x3033;
    private const int EGL_PBUFFER_BIT = 0x0001;
    private const int EGL_RENDERABLE_TYPE = 0x3040;
    private const int EGL_OPENGL_BIT = 0x0008;
    private const int EGL_RED_SIZE = 0x3024;
    private const int EGL_GREEN_SIZE = 0x3023;
    private const int EGL_BLUE_SIZE = 0x3022;
    private const int EGL_ALPHA_SIZE = 0x3021;
    private const int EGL_DEPTH_SIZE = 0x3025;
    private const int EGL_CONTEXT_MAJOR_VERSION = 0x3098;
    private const int EGL_CONTEXT_MINOR_VERSION = 0x30FB;
    private const int EGL_CONTEXT_OPENGL_PROFILE_MASK = 0x30FD;
    private const int EGL_CONTEXT_OPENGL_CORE_PROFILE_BIT = 0x0001;
    private const int EGL_OPENGL_API = 0x30A2;

    [DllImport(Lib)] private static extern IntPtr eglGetDisplay(IntPtr nativeDisplay);
    [DllImport(Lib)] private static extern bool eglInitialize(IntPtr dpy, out int major, out int minor);
    [DllImport(Lib)] private static extern bool eglBindAPI(int api);
    [DllImport(Lib)] private static extern bool eglChooseConfig(IntPtr dpy, int[] attribs, IntPtr[] configs, int configSize, out int numConfigs);
    [DllImport(Lib)] private static extern IntPtr eglCreateContext(IntPtr dpy, IntPtr config, IntPtr share, int[] attribs);
    [DllImport(Lib)] private static extern bool eglMakeCurrent(IntPtr dpy, IntPtr draw, IntPtr read, IntPtr ctx);
    [DllImport(Lib)] private static extern int eglGetError();
    [DllImport(Lib)] private static extern IntPtr eglGetProcAddress(string procName);

    private sealed class Bindings : IBindingsContext
    {
        public IntPtr GetProcAddress(string procName) => eglGetProcAddress(procName);
    }

    public static void Initialise()
    {
        IntPtr dpy = eglGetDisplay(IntPtr.Zero);
        if (dpy == IntPtr.Zero) throw new Exception($"eglGetDisplay failed (0x{eglGetError():X4})");
        if (!eglInitialize(dpy, out int major, out int minor))
            throw new Exception($"eglInitialize failed (0x{eglGetError():X4})");
        Console.WriteLine($"  EGL {major}.{minor}");

        if (!eglBindAPI(EGL_OPENGL_API)) throw new Exception("eglBindAPI(EGL_OPENGL_API) failed");

        int[] configAttribs =
        {
            EGL_SURFACE_TYPE, EGL_PBUFFER_BIT,
            EGL_RENDERABLE_TYPE, EGL_OPENGL_BIT,
            EGL_RED_SIZE, 8, EGL_GREEN_SIZE, 8, EGL_BLUE_SIZE, 8, EGL_ALPHA_SIZE, 8,
            EGL_DEPTH_SIZE, 24,
            EGL_NONE,
        };
        var configs = new IntPtr[32];
        if (!eglChooseConfig(dpy, configAttribs, configs, configs.Length, out int num) || num == 0)
            throw new Exception($"eglChooseConfig found no configs (0x{eglGetError():X4})");

        int[] contextAttribs =
        {
            EGL_CONTEXT_MAJOR_VERSION, 3,
            EGL_CONTEXT_MINOR_VERSION, 3,
            EGL_CONTEXT_OPENGL_PROFILE_MASK, EGL_CONTEXT_OPENGL_CORE_PROFILE_BIT,
            EGL_NONE,
        };
        IntPtr ctx = eglCreateContext(dpy, configs[0], IntPtr.Zero, contextAttribs);
        if (ctx == IntPtr.Zero) throw new Exception($"eglCreateContext failed (0x{eglGetError():X4})");

        // EGL_KHR_surfaceless_context: no window, no pbuffer, just a context.
        if (!eglMakeCurrent(dpy, IntPtr.Zero, IntPtr.Zero, ctx))
            throw new Exception($"eglMakeCurrent(surfaceless) failed (0x{eglGetError():X4})");

        GL.LoadBindings(new Bindings());
        Console.WriteLine("  GL " + GL.GetString(StringName.Version) + " / " + GL.GetString(StringName.Renderer));
    }
}

/// <summary>Offscreen framebuffer we can read pixels back from.</summary>
internal static class Offscreen
{
    public static (int fbo, int color, int depth) Create(int width, int height)
    {
        int fbo = GL.GenFramebuffer();
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);

        int color = GL.GenRenderbuffer();
        GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, color);
        GL.RenderbufferStorage(RenderbufferTarget.Renderbuffer, RenderbufferStorage.Rgba8, width, height);
        GL.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
            RenderbufferTarget.Renderbuffer, color);

        int depth = GL.GenRenderbuffer();
        GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, depth);
        GL.RenderbufferStorage(RenderbufferTarget.Renderbuffer, RenderbufferStorage.DepthComponent24, width, height);
        GL.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment,
            RenderbufferTarget.Renderbuffer, depth);

        var status = GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        if (status != FramebufferErrorCode.FramebufferComplete)
            throw new Exception("framebuffer incomplete: " + status);

        return (fbo, color, depth);
    }

    /// <summary>Read the colour attachment back into a top-down RGBA byte array.</summary>
    public static byte[] ReadPixels(int width, int height)
    {
        var px = new byte[width * height * 4];
        GL.PixelStore(PixelStoreParameter.PackAlignment, 1);
        GL.ReadPixels(0, 0, width, height, PixelFormat.Rgba, PixelType.UnsignedByte, px);

        // GL's origin is bottom-left; flip to top-down for image files.
        int stride = width * 4;
        var flipped = new byte[px.Length];
        for (int y = 0; y < height; y++)
            System.Buffer.BlockCopy(px, (height - 1 - y) * stride, flipped, y * stride, stride);
        return flipped;
    }

    /// <summary>Write a binary PPM (P6). Trivial format, no library needed.</summary>
    public static void WritePpm(string path, byte[] rgba, int width, int height)
    {
        using var fs = File.Create(path);
        var header = System.Text.Encoding.ASCII.GetBytes($"P6\n{width} {height}\n255\n");
        fs.Write(header);
        var rgb = new byte[width * height * 3];
        for (int i = 0, j = 0; i < rgba.Length; i += 4, j += 3)
        {
            rgb[j] = rgba[i];
            rgb[j + 1] = rgba[i + 1];
            rgb[j + 2] = rgba[i + 2];
        }
        fs.Write(rgb);
    }
}
