using OpenTK.Graphics.OpenGL4;
// The engine layer uses System.Numerics (SIMD-accelerated, framework-free).
// OpenTK has its own Vector3, so be explicit about which one we mean.
using Vector3 = System.Numerics.Vector3;

namespace VoxelEngine;

/// <summary>
/// Renders the world by RAYMARCHING the voxel volume in a fragment shader.
///
/// HOW THIS WORKS, and why it is a legitimate choice rather than a toy:
///
///   * The whole world is uploaded as one 3D texture (one byte per voxel — the
///     voxel id). A fullscreen triangle is drawn; for every pixel the fragment
///     shader walks the grid with the same DDA algorithm the CPU raycast uses,
///     jumps from cell to cell, and shades the first solid voxel it hits. Water
///     and oil are semi-transparent, so the ray keeps going and blends them.
///   * No meshing, no chunk build step, no vertex buffers for terrain: editing a
///     voxel is a single byte write, and the change is visible the moment the
///     texture is re-uploaded.
///
/// THE TRADE-OFF vs. mesh-based rendering (what Minecraft does):
///   + Trivial to implement, no CPU work per edit, perfect for a simulation sandbox.
///   + Transparency is natural (no sorting pass, no alpha blending order bugs).
///   - Cost is per PIXEL per voxel-step, so it scales with screen resolution and
///     view depth. A mesh renderer costs per *visible face* instead.
///   - It re-uploads the volume when it changes (2.2 MB here) and needs the whole
///     world resident in VRAM.
///   - It cannot handle worlds much larger than a texture: this is the thing
///     that has to change before a 1024^3 world. See README §"bigger worlds".
/// At 128^3 on a desktop GPU this is comfortably fast. At 512^3+ you would move
/// to chunked meshing (or sparse brick raycasting).
/// </summary>
public sealed class VoxelRenderer : IDisposable
{
    private readonly World _world;
    private readonly ShaderProgram _shader;
    private readonly int _vao;
    private readonly int _texVoxels;
    private readonly int _texPalette;
    private readonly int _texMeta;

    private readonly byte[] _paletteData = new byte[256 * 4];
    private readonly byte[] _metaData = new byte[256 * 4];
    private int _lastPaletteVersion = -1;

    private const string VertexSource = """
    #version 330 core
    // A fullscreen triangle generated from gl_VertexID: no vertex buffer at all.
    void main()
    {
        vec2 p = vec2(float((gl_VertexID << 1) & 2), float(gl_VertexID & 2));
        gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0);
    }
    """;

    private const string FragmentSource = """
    #version 330 core

    out vec4 fragColor;

    uniform sampler3D uVox;
    uniform sampler2D uPal;
    uniform sampler2D uMeta;

    uniform vec2  uRes;
    uniform vec3  uCamPos;
    uniform vec3  uFwd;
    uniform vec3  uRight;
    uniform vec3  uUp;
    uniform float uTanHalf;
    uniform float uAspect;
    uniform float uTime;
    uniform float uWorldSize;

    const int MAXSTEPS = 400;   // a ray can cross at most 3*128 cells

    float hash13(vec3 p)
    {
        p = fract(p * 0.1031);
        p += dot(p, p.yzx + 33.33);
        return fract((p.x + p.y) * p.z);
    }

    vec3 skyCol(vec3 d)
    {
        float t = clamp(d.y * 0.5 + 0.5, 0.0, 1.0);
        vec3 c = mix(vec3(0.56, 0.63, 0.72), vec3(0.09, 0.15, 0.31), pow(t, 1.35));
        vec3 sd = normalize(vec3(0.45, 0.72, 0.35));
        float s = max(dot(d, sd), 0.0);
        c += vec3(1.0, 0.90, 0.70) * pow(s, 220.0) * 1.4;
        c += vec3(1.0, 0.82, 0.55) * pow(s, 9.0) * 0.10;
        return c;
    }

    void main()
    {
        vec2 ndc = gl_FragCoord.xy / uRes * 2.0 - 1.0;
        vec3 dir = normalize(uFwd + uRight * (ndc.x * uAspect * uTanHalf) + uUp * (ndc.y * uTanHalf));

        vec3 cell = floor(uCamPos);
        vec3 sgn = vec3(dir.x >= 0.0 ? 1.0 : -1.0,
                        dir.y >= 0.0 ? 1.0 : -1.0,
                        dir.z >= 0.0 ? 1.0 : -1.0);
        vec3 tDelta = 1.0 / max(abs(dir), vec3(1e-7));
        vec3 tMax;
        tMax.x = (sgn.x > 0.0 ? (cell.x + 1.0 - uCamPos.x) : (uCamPos.x - cell.x)) * tDelta.x;
        tMax.y = (sgn.y > 0.0 ? (cell.y + 1.0 - uCamPos.y) : (uCamPos.y - cell.y)) * tDelta.y;
        tMax.z = (sgn.z > 0.0 ? (cell.z + 1.0 - uCamPos.z) : (uCamPos.z - cell.z)) * tDelta.z;

        vec4 acc = vec4(0.0);
        vec3 nrm = vec3(0.0, 1.0, 0.0);
        float tEnter = 0.0;
        int axis = 1;

        for (int i = 0; i < MAXSTEPS; i++)
        {
            if (cell.x < 0.0 || cell.y < 0.0 || cell.z < 0.0) break;
            if (cell.x >= uWorldSize || cell.y >= uWorldSize || cell.z >= uWorldSize) break;

            // +1: the CPU-side array is padded by one voxel on each side
            int id = int(texelFetch(uVox, ivec3(cell) + ivec3(1), 0).r * 255.0 + 0.5);

            if (id != 0)
            {
                vec4 pal = texelFetch(uPal, ivec2(id, 0), 0);
                float alpha = pal.a;
                if (alpha > 0.004)
                {
                    vec4 meta = texelFetch(uMeta, ivec2(id, 0), 0);
                    vec3 hp = uCamPos + dir * tEnter;

                    vec2 fuv;
                    if (axis == 0)      fuv = fract(hp.yz);
                    else if (axis == 1) fuv = fract(hp.xz);
                    else                fuv = fract(hp.xy);

                    vec2 tp = floor(fuv * 8.0);
                    float n  = hash13(vec3(tp, cell.x * 1.7 + cell.y * 13.1 + cell.z * 57.7));
                    float nv = hash13(cell * 0.731 + 5.0);
                    int pat = int(meta.g * 255.0 + 0.5);

                    float detail = clamp(1.0 - tEnter / 110.0, 0.0, 1.0);
                    float b = 1.0;
                    if (pat == 1) {                          // stone
                        b = 0.92 + 0.14 * n;
                    } else if (pat == 2) {                   // dirt
                        b = 0.90 + 0.20 * n;
                    } else if (pat == 3) {                   // wood grain
                        float g = sin((fuv.x * 5.0 + fuv.y * 1.2 + cell.z * 0.9) * 6.2831);
                        b = 0.94 + 0.07 * g + 0.06 * n;
                    } else if (pat == 4) {                   // metal panels
                        float pe = (fuv.x < 0.06 || fuv.x > 0.94 || fuv.y < 0.06 || fuv.y > 0.94) ? 0.82 : 1.0;
                        b = pe * (0.96 + 0.08 * n);
                    } else if (pat == 5) {                   // sand / slime grain
                        b = 0.94 + 0.12 * n;
                    } else if (pat == 6) {                   // fire
                        b = 1.05 + 0.40 * sin(uTime * 9.0 + n * 6.2831 + cell.x + cell.y * 2.0);
                    } else {
                        b = 0.97 + 0.06 * n;
                    }
                    b = 1.0 + (b - 1.0) * mix(0.35, 1.0, detail);
                    b *= 0.94 + 0.12 * nv;

                    float e = min(min(fuv.x, 1.0 - fuv.x), min(fuv.y, 1.0 - fuv.y));
                    float edge = mix(0.82, 1.0, smoothstep(0.0, 0.045, e));
                    b *= mix(1.0, edge, mix(0.45, 1.0, detail));

                    vec3 L = normalize(vec3(0.45, 0.82, 0.36));
                    float ndl = max(dot(nrm, L), 0.0);
                    float amb = 0.40 + 0.16 * nrm.y + 0.06 * max(-nrm.y, 0.0);
                    vec3 col = pal.rgb * b * (amb + 0.55 * ndl);
                    col *= mix(1.0, 0.62, clamp(tEnter / 240.0, 0.0, 1.0));

                    vec3 V = -dir;
                    vec3 Hv = normalize(V + L);
                    float spec = pow(max(dot(nrm, Hv), 0.0), 42.0);
                    float gloss = 0.18 + (1.0 - alpha) * 1.6;
                    if (pat == 4) gloss += 0.9;
                    col += vec3(1.0, 0.96, 0.88) * spec * gloss;

                    float em = meta.r;
                    col += pal.rgb * em * (1.05 + 0.30 * sin(uTime * 7.0 + n * 6.2831));

                    acc.rgb += (1.0 - acc.a) * alpha * col;
                    acc.a   += (1.0 - acc.a) * alpha;
                    if (acc.a > 0.995) break;
                }
            }

            if (tMax.x <= tMax.y && tMax.x <= tMax.z)
            {
                cell.x += sgn.x; tEnter = tMax.x; tMax.x += tDelta.x;
                nrm = vec3(-sgn.x, 0.0, 0.0); axis = 0;
            }
            else if (tMax.y <= tMax.z)
            {
                cell.y += sgn.y; tEnter = tMax.y; tMax.y += tDelta.y;
                nrm = vec3(0.0, -sgn.y, 0.0); axis = 1;
            }
            else
            {
                cell.z += sgn.z; tEnter = tMax.z; tMax.z += tDelta.z;
                nrm = vec3(0.0, 0.0, -sgn.z); axis = 2;
            }
        }

        vec3 outc = acc.rgb + (1.0 - acc.a) * skyCol(dir);
        fragColor = vec4(pow(max(outc, vec3(0.0)), vec3(0.92)), 1.0);
    }
    """;

    public VoxelRenderer(World world)
    {
        _world = world;
        _shader = new ShaderProgram(VertexSource, FragmentSource, "voxel");

        _vao = GL.GenVertexArray();      // an empty VAO is still required by core profile

        // ---- voxel volume: one R8 3D texture ----
        _texVoxels = GL.GenTexture();
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture3D, _texVoxels);
        GL.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
        GL.TexImage3D(TextureTarget.Texture3D, 0, PixelInternalFormat.R8,
            World.PS, World.PS, World.PS, 0, PixelFormat.Red, PixelType.UnsignedByte,
            _world.Data);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapR, (int)TextureWrapMode.ClampToEdge);

        _texPalette = GL.GenTexture();
        _texMeta = GL.GenTexture();
        UploadPalette();

        GL.Disable(EnableCap.DepthTest);
        GL.Disable(EnableCap.CullFace);
    }

    /// <summary>Re-upload the whole volume. Called only when something changed.</summary>
    public void UploadWorld()
    {
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture3D, _texVoxels);
        GL.TexSubImage3D(TextureTarget.Texture3D, 0, 0, 0, 0,
            World.PS, World.PS, World.PS, PixelFormat.Red, PixelType.UnsignedByte, _world.Data);
    }

    /// <summary>
    /// Build the 256-entry colour + "meta" (emissive, texture pattern) tables
    /// from the registry. Adding a voxel type needs no shader change.
    /// </summary>
    public void UploadPalette()
    {
        Array.Clear(_paletteData);
        Array.Clear(_metaData);

        foreach (var def in VoxelRegistry.Defs)
        {
            int i = def.Id * 4;
            Vector3 c = def.Color;
            float emissive = def.Emissive;

            foreach (var p in def.Properties)
            {
                var pc = p.PaletteColor(def);
                if (pc.HasValue) c = pc.Value;
                var pe = p.PaletteEmissive(def);
                if (pe.HasValue) emissive = pe.Value;
            }

            _paletteData[i + 0] = (byte)Math.Clamp((int)(c.X * 255f + 0.5f), 0, 255);
            _paletteData[i + 1] = (byte)Math.Clamp((int)(c.Y * 255f + 0.5f), 0, 255);
            _paletteData[i + 2] = (byte)Math.Clamp((int)(c.Z * 255f + 0.5f), 0, 255);
            _paletteData[i + 3] = (byte)Math.Clamp((int)((1f - def.Transparency) * 255f + 0.5f), 0, 255);

            _metaData[i + 0] = (byte)Math.Clamp((int)(Math.Clamp(emissive, 0f, 1f) * 255f), 0, 255);
            _metaData[i + 1] = (byte)(def.Pattern & 255);
        }

        UploadLinear(_texPalette, _paletteData, TextureUnit.Texture1);
        UploadLinear(_texMeta, _metaData, TextureUnit.Texture2);
        _lastPaletteVersion = VoxelRegistry.PaletteVersion;
    }

    private static void UploadLinear(int tex, byte[] data, TextureUnit unit)
    {
        GL.ActiveTexture(unit);
        GL.BindTexture(TextureTarget.Texture2D, tex);
        GL.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, 256, 1, 0,
            PixelFormat.Rgba, PixelType.UnsignedByte, data);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
    }

    public void Render(Camera cam, int width, int height, double time)
    {
        if (VoxelRegistry.PaletteVersion != _lastPaletteVersion) UploadPalette();
        if (_world.Dirty) { UploadWorld(); _world.Dirty = false; }

        GL.Viewport(0, 0, width, height);
        GL.Clear(ClearBufferMask.ColorBufferBit);

        _shader.Use();

        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture3D, _texVoxels);
        GL.ActiveTexture(TextureUnit.Texture1);
        GL.BindTexture(TextureTarget.Texture2D, _texPalette);
        GL.ActiveTexture(TextureUnit.Texture2);
        GL.BindTexture(TextureTarget.Texture2D, _texMeta);
        GL.Uniform1(_shader.Uniform("uVox"), 0);
        GL.Uniform1(_shader.Uniform("uPal"), 1);
        GL.Uniform1(_shader.Uniform("uMeta"), 2);

        GL.Uniform2(_shader.Uniform("uRes"), (float)width, (float)height);
        GL.Uniform3(_shader.Uniform("uCamPos"), cam.Position.X, cam.Position.Y, cam.Position.Z);
        GL.Uniform3(_shader.Uniform("uFwd"), cam.Forward.X, cam.Forward.Y, cam.Forward.Z);
        GL.Uniform3(_shader.Uniform("uRight"), cam.Right.X, cam.Right.Y, cam.Right.Z);
        GL.Uniform3(_shader.Uniform("uUp"), cam.Up.X, cam.Up.Y, cam.Up.Z);
        GL.Uniform1(_shader.Uniform("uTanHalf"), MathF.Tan(cam.FovY * 0.5f));
        GL.Uniform1(_shader.Uniform("uAspect"), (float)width / height);
        GL.Uniform1(_shader.Uniform("uTime"), (float)time);
        GL.Uniform1(_shader.Uniform("uWorldSize"), (float)_world.Size);

        GL.BindVertexArray(_vao);
        GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
    }

    public void Dispose()
    {
        _shader.Dispose();
        GL.DeleteTexture(_texVoxels);
        GL.DeleteTexture(_texPalette);
        GL.DeleteTexture(_texMeta);
        GL.DeleteVertexArray(_vao);
    }
}
