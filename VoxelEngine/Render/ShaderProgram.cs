using OpenTK.Graphics.OpenGL4;

namespace VoxelEngine;

/// <summary>
/// Thin wrapper around a compiled+linked GLSL program.
///
/// Everything it does is compile, link, report errors loudly, and cache uniform
/// locations (looking a uniform up by string every frame is a needless hash
/// lookup per frame per uniform).
/// </summary>
public sealed class ShaderProgram : IDisposable
{
    public int Handle { get; private set; }
    private readonly Dictionary<string, int> _uniforms = new();

    public ShaderProgram(string vertexSource, string fragmentSource, string debugName = "shader")
    {
        int vs = Compile(ShaderType.VertexShader, vertexSource, debugName + ".vert");
        int fs = Compile(ShaderType.FragmentShader, fragmentSource, debugName + ".frag");

        Handle = GL.CreateProgram();
        GL.AttachShader(Handle, vs);
        GL.AttachShader(Handle, fs);
        GL.LinkProgram(Handle);

        GL.GetProgram(Handle, GetProgramParameterName.LinkStatus, out int ok);
        if (ok == 0)
            throw new Exception($"[{debugName}] link failed:\n{GL.GetProgramInfoLog(Handle)}");

        GL.DetachShader(Handle, vs);
        GL.DetachShader(Handle, fs);
        GL.DeleteShader(vs);
        GL.DeleteShader(fs);
    }

    private static int Compile(ShaderType type, string source, string debugName)
    {
        int shader = GL.CreateShader(type);
        GL.ShaderSource(shader, source);
        GL.CompileShader(shader);

        GL.GetShader(shader, ShaderParameter.CompileStatus, out int ok);
        if (ok == 0)
            throw new Exception($"[{debugName}] compile failed:\n{GL.GetShaderInfoLog(shader)}");

        return shader;
    }

    public void Use() => GL.UseProgram(Handle);

    public int Uniform(string name)
    {
        if (_uniforms.TryGetValue(name, out int loc)) return loc;
        loc = GL.GetUniformLocation(Handle, name);
        _uniforms[name] = loc;
        return loc;
    }

    public void Dispose()
    {
        if (Handle != 0) { GL.DeleteProgram(Handle); Handle = 0; }
    }
}
