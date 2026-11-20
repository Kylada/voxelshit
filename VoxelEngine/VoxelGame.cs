using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;
using Vector4 = System.Numerics.Vector4;

namespace VoxelEngine;

/// <summary>
/// The window and the game loop. This is the only file that knows OpenTK exists,
/// apart from the two renderer classes — everything in Engine/ is framework-free
/// and unit-testable.
///
/// THE GAME LOOP, and the single most important thing in it:
/// physics runs on a FIXED timestep, rendering runs at whatever the monitor
/// does. Frame time is accumulated and physics ticks are consumed from that
/// accumulator. Why:
///   * A variable timestep makes the simulation depend on the framerate — falling
///     speed, how far a liquid spreads, all of it changes between a 60 Hz and a
///     144 Hz monitor, and it is impossible to reproduce a bug.
///   * A fixed step makes a tick a well-defined unit you can reason about,
///     count, replay, and send to a server later.
/// The cost is that the simulation can lag behind the display, which is why
/// rendering usually *interpolates* between the last two ticks. Our voxels snap
/// to grid cells, so there is nothing to interpolate — but when you add moving
/// entities they will need it.
/// </summary>
public sealed class VoxelGame : GameWindow
{
    private World _world = null!;
    private Player _player = null!;
    private VoxelRenderer _renderer = null!;
    private HudRenderer _hud = null!;
    private IPhysicsScheduler _scheduler = null!;

    private double _tickAccumulator;
    private double _time;
    private bool _paused;

    private double _fps;
    private double _fpsAccum;
    private int _fpsFrames;
    private double _titleTimer;

    private bool _prevLeft, _prevRight;
    private int _pendingSlot = -1;

    public VoxelGame() : base(GameWindowSettings.Default, new NativeWindowSettings
    {
        ClientSize = new Vector2i(Config.WindowWidth, Config.WindowHeight),
        Title = "Voxel Engine (C#)",
        APIVersion = new Version(3, 3),
        Profile = ContextProfile.Core,
        Flags = ContextFlags.ForwardCompatible,
    })
    {
    }

    protected override void OnLoad()
    {
        base.OnLoad();
        VSync = VSyncMode.On;

        VoxelPalette.RegisterDefaults();

        _world = new World();
        WorldGenerator.Generate(_world, 20261105);
        _player = new Player();

        _renderer = new VoxelRenderer(_world);
        _hud = new HudRenderer();

        // Swap this one line for `new ParallelScheduler()` to run the tick on all
        // cores. Read the comment on ParallelScheduler first — it explains why
        // "just add threads" is not free.
        _scheduler = new SerialScheduler();

        CursorState = CursorState.Grabbed;
        PrintHelp();
    }

    private static void PrintHelp()
    {
        Console.WriteLine();
        Console.WriteLine("  VOXEL ENGINE (C#)");
        Console.WriteLine("  ---------------------------------------------------------");
        Console.WriteLine("  W A S D ....... fly horizontally");
        Console.WriteLine("  Space / Shift . fly up / down");
        Console.WriteLine("  Mouse ......... look (Esc releases the cursor, click re-grabs)");
        Console.WriteLine("  LMB ........... destroy voxel");
        Console.WriteLine("  RMB ........... place selected voxel");
        Console.WriteLine("  1 .. 9, 0 ..... select voxel type (in palette order)");
        Console.WriteLine("  Wheel ......... cycle voxel type");
        Console.WriteLine("  P ............. pause / resume physics");
        Console.WriteLine("  T ............. toggle serial <-> parallel scheduler");
        Console.WriteLine("  R ............. regenerate world");
        Console.WriteLine("  F1 ............ print scheduler timings");
        Console.WriteLine("  ---------------------------------------------------------");
        Console.WriteLine();
        Console.WriteLine("  Voxel palette:");
        for (int i = 0; i < VoxelRegistry.Count; i++)
        {
            var d = VoxelRegistry.Get(i);
            Console.WriteLine($"   {(i == 9 ? 0 : i + 1)}  {d.Name,-6} viscosity {d.Viscosity:0.00}  physics {d.Physics}");
        }
        Console.WriteLine();
    }

    protected override void OnUpdateFrame(FrameEventArgs args)
    {
        base.OnUpdateFrame(args);
        float dt = (float)args.Time;
        _time += args.Time;

        _fpsAccum += args.Time;
        _fpsFrames++;
        if (_fpsAccum >= 0.35)
        {
            _fps = _fpsFrames / _fpsAccum;
            _fpsAccum = 0;
            _fpsFrames = 0;
        }

        var kb = KeyboardState;
        var ms = MouseState;

        bool left = ms.IsButtonDown(MouseButton.Left);
        bool right = ms.IsButtonDown(MouseButton.Right);

        bool lookWithMouse = CursorState == CursorState.Grabbed || left;
        var input = new InputFrame
        {
            Forward = kb.IsKeyDown(Keys.W),
            Back = kb.IsKeyDown(Keys.S),
            Left = kb.IsKeyDown(Keys.A),
            Right = kb.IsKeyDown(Keys.D),
            Up = kb.IsKeyDown(Keys.Space),
            Down = kb.IsKeyDown(Keys.LeftShift) || kb.IsKeyDown(Keys.RightShift),
            LookDeltaX = lookWithMouse ? ms.Delta.X : 0f,
            LookDeltaY = lookWithMouse ? ms.Delta.Y : 0f,
            Dig = left && !_prevLeft && CursorState == CursorState.Grabbed,
            Place = right && !_prevRight && CursorState == CursorState.Grabbed,
            SelectSlot = _pendingSlot,
        };
        _pendingSlot = -1;
        _prevLeft = left;
        _prevRight = right;

        _player.Update(_world, dt, input);

        // ---- fixed timestep ----
        if (!_paused)
        {
            _tickAccumulator += args.Time;
            double step = 1.0 / Config.PhysicsTickHz;
            int budget = Config.MaxTicksPerFrame;
            while (_tickAccumulator >= step && budget-- > 0)
            {
                _scheduler.Tick(_world);
                _tickAccumulator -= step;
            }
            // If we fell far behind (window drag, breakpoint), drop the backlog
            // instead of trying to catch up forever and freezing.
            if (_tickAccumulator > step * 8) _tickAccumulator = 0;
        }

        // per-frame property hooks (e.g. fire flicker)
        foreach (var def in VoxelRegistry.Defs)
            foreach (var p in def.Properties)
                p.OnFrame(def, _time);

        _titleTimer += args.Time;
        if (_titleTimer > 0.25)
        {
            _titleTimer = 0;
            var d = VoxelRegistry.Get(_player.Selected);
            Title = $"Voxel Engine (C#)  |  {_fps:0} fps  |  tick {_world.LastTickMs:0.00} ms ({_scheduler.Name})" +
                    $"  |  active {_world.Pending.Count:N0}" +
                    $"  |  {(_paused ? "PAUSED" : "running")}" +
                    $"  |  holding {d.Name}";
        }
    }

    protected override void OnRenderFrame(FrameEventArgs args)
    {
        base.OnRenderFrame(args);

        int w = ClientSize.X;
        int h = ClientSize.Y;

        _renderer.Render(_player.Camera, w, h, _time);

        // ---- HUD: hotbar + crosshair ----
        _hud.Begin(w, h);

        const float slot = 52f, gap = 6f, margin = 18f;
        float barWidth = VoxelRegistry.Count * slot + (VoxelRegistry.Count - 1) * gap;
        float barX = (w - barWidth) * 0.5f;
        float barY = h - margin - slot;

        _hud.Rect(barX - 8, barY - 8, barWidth + 16, slot + 16, new Vector4(0.04f, 0.06f, 0.09f, 0.72f));

        for (int i = 0; i < VoxelRegistry.Count; i++)
        {
            var def = VoxelRegistry.Get(i);
            float x = barX + i * (slot + gap);

            bool selected = i == _player.Selected;
            var c = def.Color;
            var swatch = new Vector4(c.X, c.Y, c.Z, def.Id == Vox.Air ? 0.15f : 1f);
            _hud.Rect(x, barY, slot, slot, new Vector4(0.10f, 0.13f, 0.18f, 0.9f));
            _hud.Rect(x + 8, barY + 8, slot - 16, slot - 16, swatch);

            if (selected) _hud.Outline(x - 2, barY - 2, slot + 4, slot + 4, 2f, new Vector4(0.45f, 0.72f, 1f, 1f));
            else _hud.Outline(x, barY, slot, slot, 1f, new Vector4(1f, 1f, 1f, 0.08f));
        }

        // crosshair
        float cx = w * 0.5f, cy = h * 0.5f;
        _hud.Rect(cx - 7, cy - 1, 14, 2, new Vector4(0f, 0f, 0f, 0.55f));
        _hud.Rect(cx - 1, cy - 7, 2, 14, new Vector4(0f, 0f, 0f, 0.55f));
        _hud.Rect(cx - 6, cy - 0.5f, 12, 1, new Vector4(1f, 1f, 1f, 0.95f));
        _hud.Rect(cx - 0.5f, cy - 6, 1, 12, new Vector4(1f, 1f, 1f, 0.95f));

        _hud.End();

        SwapBuffers();
    }

    protected override void OnKeyDown(KeyboardKeyEventArgs e)
    {
        base.OnKeyDown(e);

        switch (e.Key)
        {
            case Keys.Escape:
                CursorState = CursorState.Normal;
                break;

            case Keys.P:
                _paused = !_paused;
                Console.WriteLine(_paused ? "physics paused" : "physics running");
                break;

            case Keys.T:
                _scheduler = _scheduler is SerialScheduler
                    ? new ParallelScheduler()
                    : new SerialScheduler();
                Console.WriteLine("scheduler -> " + _scheduler.Name);
                break;

            case Keys.R:
                WorldGenerator.Generate(_world, Random.Shared.Next());
                Console.WriteLine("world regenerated");
                break;

            case Keys.F1:
                Console.WriteLine($"scheduler={_scheduler.Name}  last tick {_world.LastTickMs:0.000} ms  " +
                                  $"active {_world.Pending.Count:N0}  tick #{_world.TickCount}");
                break;

            case Keys.D1: case Keys.D2: case Keys.D3: case Keys.D4: case Keys.D5:
            case Keys.D6: case Keys.D7: case Keys.D8: case Keys.D9:
                _pendingSlot = (int)e.Key - (int)Keys.D1;
                break;
            case Keys.D0:
                _pendingSlot = 9;
                break;
        }
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (CursorState != CursorState.Grabbed) CursorState = CursorState.Grabbed;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        int dir = e.OffsetY > 0 ? 1 : -1;
        _pendingSlot = ((_player.Selected + dir) % VoxelRegistry.Count + VoxelRegistry.Count) % VoxelRegistry.Count;
    }

    protected override void OnUnload()
    {
        _hud?.Dispose();
        _renderer?.Dispose();
        base.OnUnload();
    }
}
