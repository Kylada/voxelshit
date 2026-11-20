using OpenTK.Graphics.OpenGL4;
using System.Numerics;

namespace VoxelEngine;

/// <summary>
/// Draws flat coloured rectangles in screen space — enough for a hotbar and a
/// crosshair without pulling in a UI library.
///
/// WHY NO TEXT: real text needs a glyph atlas (an image of every character in a
/// font) plus textured quads, which is a whole subsystem. For now the numbers
/// live in the window title bar and the console, and the hotbar is colour-coded.
/// See README §"adding a text HUD" when you want it.
///
/// WHY BATCH: all rectangles go into one vertex buffer and are drawn with a
/// single draw call. Ten separate draw calls with a uniform change each would be
/// slower and no simpler.
/// </summary>
public sealed class HudRenderer : IDisposable
{
    private const int FloatsPerVertex = 6;   // x, y, r, g, b, a
    private const int VertsPerQuad = 6;

    private readonly ShaderProgram _shader;
    private readonly int _vao;
    private readonly int _vbo;
    private float[] _vertices = new float[FloatsPerVertex * VertsPerQuad * 64];
    private int _floatCount;
    private int _width = 1, _height = 1;

    private const string VertexSource = """
    #version 330 core
    layout(location = 0) in vec2 aPos;
    layout(location = 1) in vec4 aColor;
    out vec4 vColor;
    void main()
    {
        vColor = aColor;
        gl_Position = vec4(aPos, 0.0, 1.0);
    }
    """;

    private const string FragmentSource = """
    #version 330 core
    in vec4 vColor;
    out vec4 fragColor;
    void main() { fragColor = vColor; }
    """;

    public HudRenderer()
    {
        _shader = new ShaderProgram(VertexSource, FragmentSource, "hud");
        _vao = GL.GenVertexArray();
        _vbo = GL.GenBuffer();

        GL.BindVertexArray(_vao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, FloatsPerVertex * sizeof(float), 0);
        GL.EnableVertexAttribArray(1);
        GL.VertexAttribPointer(1, 4, VertexAttribPointerType.Float, false, FloatsPerVertex * sizeof(float), 2 * sizeof(float));
    }

    public void Begin(int width, int height)
    {
        _width = Math.Max(1, width);
        _height = Math.Max(1, height);
        _floatCount = 0;
    }

    /// <summary>Queue a rectangle in screen pixels (origin top-left).</summary>
    public void Rect(float x, float y, float w, float h, Vector4 color)
    {
        if (!EnsureCapacity(FloatsPerVertex * VertsPerQuad)) return;

        float x0 = x / _width * 2f - 1f;
        float x1 = (x + w) / _width * 2f - 1f;
        float y0 = 1f - y / _height * 2f;
        float y1 = 1f - (y + h) / _height * 2f;

        PushVertex(x0, y0, color);
        PushVertex(x1, y0, color);
        PushVertex(x1, y1, color);
        PushVertex(x0, y0, color);
        PushVertex(x1, y1, color);
        PushVertex(x0, y1, color);
    }

    /// <summary>Queue an outlined rectangle (four thin bars).</summary>
    public void Outline(float x, float y, float w, float h, float thickness, Vector4 color)
    {
        Rect(x, y, w, thickness, color);
        Rect(x, y + h - thickness, w, thickness, color);
        Rect(x, y, thickness, h, color);
        Rect(x + w - thickness, y, thickness, h, color);
    }

    private void PushVertex(float x, float y, Vector4 c)
    {
        _vertices[_floatCount++] = x;
        _vertices[_floatCount++] = y;
        _vertices[_floatCount++] = c.X;
        _vertices[_floatCount++] = c.Y;
        _vertices[_floatCount++] = c.Z;
        _vertices[_floatCount++] = c.W;
    }

    private bool EnsureCapacity(int extra)
    {
        if (_floatCount + extra <= _vertices.Length) return true;
        Array.Resize(ref _vertices, Math.Max(_vertices.Length * 2, _floatCount + extra));
        return true;
    }

    public void End()
    {
        if (_floatCount == 0) return;

        GL.Disable(EnableCap.DepthTest);
        GL.Enable(EnableCap.Blend);
        GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

        _shader.Use();
        GL.BindVertexArray(_vao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
        GL.BufferData(BufferTarget.ArrayBuffer, _floatCount * sizeof(float), _vertices, BufferUsageHint.StreamDraw);
        GL.DrawArrays(PrimitiveType.Triangles, 0, _floatCount / FloatsPerVertex);
    }

    public void Dispose()
    {
        _shader.Dispose();
        GL.DeleteBuffer(_vbo);
        GL.DeleteVertexArray(_vao);
    }
}
