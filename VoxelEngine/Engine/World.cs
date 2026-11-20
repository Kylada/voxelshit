using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace VoxelEngine;

/// <summary>
/// A thread-agnostic place to put "wake up this voxel next tick" requests.
///
/// The serial scheduler uses one that de-duplicates through World's flag array.
/// The parallel scheduler gives each worker thread its own un-deduplicated
/// buffer, so threads never write to shared memory while simulating — that is
/// the whole trick behind making the tick parallel without locks.
/// </summary>
public sealed class Sink
{
    private readonly byte[]? _dedupeFlags;
    public int[] Items;
    public int Count;

    public Sink(int capacity, byte[]? dedupeFlags)
    {
        Items = new int[capacity];
        _dedupeFlags = dedupeFlags;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Push(int index)
    {
        if (_dedupeFlags != null)
        {
            if (_dedupeFlags[index] != 0) return;
            _dedupeFlags[index] = 1;
        }
        if (Count == Items.Length) Array.Resize(ref Items, Items.Length * 2);
        Items[Count++] = index;
    }
}

/// <summary>
/// A per-voxel array of some extra value (temperature, damage, moisture...).
///
/// This is how you add per-voxel state WITHOUT objects: one flat array, one
/// entry per voxel, indexed exactly like the world. Costs sizeof(T) bytes per
/// voxel — 8.8 MB for a float at 128^3 — and is cache-friendly when you walk it
/// in index order, which is why the voxel loop is written the way it is.
/// </summary>
public sealed class VoxelChannel<T> where T : struct
{
    public readonly T[] Data;
    public VoxelChannel(int volume, T initial)
    {
        Data = new T[volume];
        if (!EqualityComparer<T>.Default.Equals(initial, default))
            Array.Fill(Data, initial);
    }
}

/// <summary>
/// The voxel world: storage, the activity queue, and the per-voxel physics rules.
///
/// STORAGE LAYOUT — read this once and the rest of the engine makes sense.
///
///   * One flat <c>byte[]</c>, one byte per voxel: the voxel *id*.
///   * Index layout is x-fastest, then y, then z:
///         index = xp + PS * (yp + PS * zp)
///     This matters for two reasons: (a) a 3D texture upload wants exactly this
///     order, and (b) walking x in the inner loop touches one cache line at a
///     time instead of striding through memory.
///   * The array is padded by one voxel on every side, and that shell is
///     permanently the 'Stone' id. A 3x3x3 neighbourhood lookup can therefore
///     never leave the array, so the physics kernel needs ZERO bounds checks —
///     it just adds precomputed offsets. The shell doubles as bedrock: nothing
///     can fall out of the world or leak off the side.
///
/// THE ACTIVE SET — a settled world must cost nothing, so we never scan the
/// world to find out what to simulate. Voxels that might need to do something
/// are pushed into a queue; anything that changes (a move, or a player edit)
/// wakes the 3x3x3 neighbourhood around it for the NEXT tick. Consequences:
///   * no work at all when the world is at rest,
///   * falling happens at exactly one voxel per tick (cascades are naturally
///     limited to the tick rate),
///   * a voxel can never be simulated twice within one tick, so feedback loops
///     are impossible.
/// </summary>
public sealed class World
{
    public const int Pad = Config.Pad;
    public const int PS = Config.PaddedSize;
    public const int PS2 = PS * PS;

    public readonly int Size;
    public readonly int Volume;

    /// <summary>Voxel ids. The single source of truth.</summary>
    public readonly byte[] Data;

    /// <summary>
    /// 1 = this voxel is already waiting in the pending queue.
    ///
    /// The parallel scheduler has its workers de-duplicate against this same
    /// array while they run. That is technically a data race (two threads may
    /// both read 0 and both push), but it is a *benign* one: the worst case is
    /// that a voxel appears twice in the next work list and gets simulated
    /// twice, which is harmless. It is never lost, which is what would matter.
    /// Avoiding the race entirely would mean atomics on every wake-up, which
    /// costs far more than the rare duplicate.
    /// </summary>
    private readonly byte[] _queued;

    public byte[] QueuedFlags => _queued;

    /// <summary>The list being simulated right now (owned by the scheduler).</summary>
    public int[] Processing;
    public int ProcessingCount;

    /// <summary>Where new wake-ups go; becomes Processing at the next BeginTick.</summary>
    public Sink Pending;

    public int TickCount { get; private set; }
    public double LastTickMs { get; private set; }
    public bool Dirty { get; set; } = true;

    private readonly int[] _diag = new int[4];
    private readonly int[] _horiz = new int[4];
    private readonly int[] _touch = new int[27];
    private readonly int _offDown;

    private readonly Dictionary<string, object> _channels = new();

    /// <summary>Scheduler scratch: voxels whose move straddled a slab border.</summary>
    public readonly List<int> RetryList = new();

    public World(int size = Config.Size)
    {
        Size = size;
        Volume = PS * PS * PS;
        Data = new byte[Volume];
        _queued = new byte[Volume];
        Processing = new int[Volume];
        Pending = new Sink(Volume, _queued);

        _offDown = -PS;
        _diag[0] = -PS + 1;        // neighbour doubles are only +/-1 in x and z:
        _diag[1] = -PS - 1;        // diagonals below are "down" plus one lateral step
        _diag[2] = -PS + PS2;
        _diag[3] = -PS - PS2;
        _horiz[0] = 1;
        _horiz[1] = -1;
        _horiz[2] = PS2;
        _horiz[3] = -PS2;

        int k = 0;
        for (int dz = -1; dz <= 1; dz++)
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                    _touch[k++] = dx + dy * PS + dz * PS2;

        Reset();
    }

    // ---------------------------------------------------------------- indexing

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Index(int x, int y, int z) => (x + Pad) + PS * ((y + Pad) + PS * (z + Pad));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool InBounds(int x, int y, int z) =>
        (uint)x < (uint)Size && (uint)y < (uint)Size && (uint)z < (uint)Size;

    /// <summary>Padded z of a flat index (used by the parallel scheduler to bucket work).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ZOf(int index) => index / PS2;

    public byte Get(int x, int y, int z) =>
        InBounds(x, y, z) ? Data[Index(x, y, z)] : Vox.Stone;   // outside the box == rock

    public void SetRaw(int x, int y, int z, byte id)
    {
        if (InBounds(x, y, z)) Data[Index(x, y, z)] = id;
    }

    /// <summary>Write a voxel and wake the neighbourhood. Returns true if it changed.</summary>
    public bool Set(int x, int y, int z, byte id)
    {
        if (!InBounds(x, y, z)) return false;
        int i = Index(x, y, z);
        byte old = Data[i];
        if (old == id) return false;
        Data[i] = id;

        var oldDef = VoxelRegistry.Get(old);
        foreach (var p in oldDef.Properties) p.OnRemoved(this, i, oldDef);
        var newDef = VoxelRegistry.Get(id);
        foreach (var p in newDef.Properties) p.OnPlaced(this, i, newDef);

        Touch(i, Pending);
        Dirty = true;
        return true;
    }

    // ---------------------------------------------------------------- queueing

    /// <summary>Wake a voxel (and its 3x3x3 neighbourhood) for the next tick.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Touch(int i, Sink sink)
    {
        var t = _touch;
        for (int k = 0; k < 27; k++) sink.Push(i + t[k]);
    }

    public void ActivateAt(int x, int y, int z)
    {
        if (InBounds(x, y, z)) Pending.Push(Index(x, y, z));
    }

    /// <summary>Seed the queue with every voxel that has physics. Called once after worldgen.</summary>
    public void ActivateAll()
    {
        var d = Data;
        // Never schedule the padded shell: it is a fixed boundary, not world stone.
        for (int z = 0; z < Size; z++)
            for (int y = 0; y < Size; y++)
                for (int x = 0; x < Size; x++)
                {
                    int i = Index(x, y, z);
                    if (VoxelRegistry.Physics[d[i]] != PhysicsMode.None) Pending.Push(i);
                }
    }

    /// <summary>
    /// Rotate the queues. The scheduler calls this once per tick, then walks
    /// Processing[0..ProcessingCount).
    /// </summary>
    public void BeginTick()
    {
        TickCount++;

        // swap the two buffers: this tick's work becomes the array we just filled
        (Processing, Pending.Items) = (Pending.Items, Processing);
        ProcessingCount = Pending.Count;
        Pending.Count = 0;

        // Clearing the flags up-front means anything woken DURING this tick gets
        // re-queued for the next one, which is what caps cascades at 1 cell/tick.
        var q = _queued;
        for (int k = 0; k < ProcessingCount; k++) q[Processing[k]] = 0;
    }

    public void Reset()
    {
        Array.Fill(Data, Vox.Air);
        for (int z = 0; z < PS; z++)
            for (int y = 0; y < PS; y++)
                for (int x = 0; x < PS; x++)
                    if (x < Pad || y < Pad || z < Pad ||
                        x >= PS - Pad || y >= PS - Pad || z >= PS - Pad)
                        Data[x + PS * (y + PS * z)] = Vox.Stone;

        Array.Clear(_queued);
        Pending.Count = 0;
        ProcessingCount = 0;
        TickCount = 0;
        Dirty = true;
    }

    // ---------------------------------------------------------------- physics

    public enum MoveResult { NoMove, Moved, BlockedByRange }

    /// <summary>
    /// Move a voxel into a neighbouring cell, SWAPPING rather than overwriting:
    /// the mover takes the target cell and whatever was there (air, or a less
    /// viscous voxel) is pushed back into the vacated cell. Nothing is ever
    /// created or destroyed, so total voxel counts are conserved.
    /// <para>
    /// <paramref name="writeLo"/>/<paramref name="writeHi"/> limit which cells
    /// this call may write. The serial scheduler passes the whole array; the
    /// parallel scheduler passes the slab a thread owns. A move that would
    /// write outside the range is refused instead of corrupting another
    /// thread's data.
    /// </para>
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private MoveResult TrySwap(int from, int to, Sink sink, int writeLo, int writeHi)
    {
        if (to < writeLo || to >= writeHi) return MoveResult.BlockedByRange;
        var d = Data;
        (d[from], d[to]) = (d[to], d[from]);
        Touch(from, sink);
        Touch(to, sink);
        Dirty = true;
        return MoveResult.Moved;
    }

    /// <summary>
    /// The rule set for one voxel. This is the whole simulation.
    /// </summary>
    /// <returns>
    /// true if the voxel wanted to move but the write range refused it (it
    /// straddles a slab border). The parallel scheduler collects those and
    /// retries them serially; the serial scheduler ignores the result.
    /// </returns>
    public bool StepVoxel(int i, Sink sink, int writeLo, int writeHi)
    {
        var d = Data;
        // Neighborhood wakeups include the padded shell. It must never move.
        int z = i / PS2, y = (i / PS) % PS, x = i % PS;
        if (x < Pad || x >= Pad + Size || y < Pad || y >= Pad + Size || z < Pad || z >= Pad + Size) return false;
        byte id = d[i];
        var mode = VoxelRegistry.Physics[id];
        if (mode == PhysicsMode.None) return false;

        var def = VoxelRegistry.Get(id);
        var props = def.Properties;
        if (props.Count > 0)
        {
            // NOTE: during a parallel tick your property must not call
            // World.Set — it would race with other threads. Read-only, or
            // mutate type-level state only.
            for (int p = 0; p < props.Count; p++)
                if (!props[p].OnTick(this, i, def)) return false;
        }

        float v = VoxelRegistry.Viscosity[id];
        var visc = VoxelRegistry.Viscosity;

        // A fixable voxel is anchored by another fixable voxel at a side or above.
        // This is a local rule, not structural connectivity to the ground.
        if (mode == PhysicsMode.Fixable &&
            (VoxelRegistry.Physics[d[i + 1]] == PhysicsMode.Fixable ||
             VoxelRegistry.Physics[d[i - 1]] == PhysicsMode.Fixable ||
             VoxelRegistry.Physics[d[i + PS2]] == PhysicsMode.Fixable ||
             VoxelRegistry.Physics[d[i - PS2]] == PhysicsMode.Fixable ||
             VoxelRegistry.Physics[d[i + PS]] == PhysicsMode.Fixable)) return false;

        // 1. fall straight down if the voxel below is LESS viscous than me
        int below = i + _offDown;
        if (visc[d[below]] < v)
        {
            var res = TrySwap(i, below, sink, writeLo, writeHi);
            if (res == MoveResult.Moved) return false;
            if (res == MoveResult.BlockedByRange) return true;
        }

        if (mode == PhysicsMode.Solid || mode == PhysicsMode.Fixable) return false;

        // 2. sand (and liquids): also slide into a less viscous diagonal-below
        int r = (int)(Rng.Hash(i, TickCount) & 3);
        bool retry = false;
        for (int k = 0; k < 4; k++)
        {
            int off = _diag[(r + k) & 3];
            if (visc[d[i + off]] < v)
            {
                var res = TrySwap(i, i + off, sink, writeLo, writeHi);
                if (res == MoveResult.Moved) return false;
                if (res == MoveResult.BlockedByRange) retry = true;
            }
        }

        if (mode != PhysicsMode.Liquid) return retry;

        // 3. liquids additionally creep horizontally, on a per-viscosity timer
        int period = VoxelRegistry.LiquidPeriod[id];
        if (period > 1)
        {
            // per-voxel phase so a lake does not step in lockstep
            int phase = (int)(Rng.Hash(i, 0) % (uint)period);
            if ((TickCount + phase) % period != 0)
            {
                // The active-set queue otherwise goes to sleep before this timer
                // can expire. Only keep surfaces with a possible move awake.
                for (int k = 0; k < 4; k++)
                    if (visc[d[i + _horiz[k]]] < v) { sink.Push(i); break; }
                return retry;
            }
        }

        r = (int)(Rng.Hash(i, TickCount ^ 0x5bd1e995) & 3);
        for (int k = 0; k < 4; k++)
        {
            int off = _horiz[(r + k) & 3];
            if (visc[d[i + off]] < v)
            {
                var res = TrySwap(i, i + off, sink, writeLo, writeHi);
                if (res == MoveResult.Moved) return false;
                if (res == MoveResult.BlockedByRange) retry = true;
            }
        }
        // otherwise blocked by something at least as viscous -> do not move
        return retry;
    }

    // ---------------------------------------------------------------- channels

    /// <summary>Get (and lazily allocate) a per-voxel data channel. See VoxelChannel&lt;T&gt;.</summary>
    public VoxelChannel<T> Channel<T>(string key, T initial) where T : struct
    {
        if (_channels.TryGetValue(key, out var existing)) return (VoxelChannel<T>)existing;
        var ch = new VoxelChannel<T>(Volume, initial);
        _channels[key] = ch;
        return ch;
    }

    // ---------------------------------------------------------------- stats

    public int[] CountByType()
    {
        var counts = new int[256];
        var d = Data;
        for (int i = 0; i < Volume; i++) counts[d[i]]++;
        return counts;
    }

    public void MarkTickTiming(long startTimestamp)
    {
        LastTickMs = (Stopwatch.GetTimestamp() - startTimestamp) * 1000.0 / Stopwatch.Frequency;
    }
}

/// <summary>
/// Deterministic, stateless randomness.
///
/// WHY NOT <c>Random</c>: a shared Random object is mutable state, so (a) two
/// threads would corrupt it, and (b) the same world would simulate differently
/// every run, which makes bugs impossible to reproduce. Hashing the voxel index
/// together with the tick number gives the same answer for the same voxel on
/// the same tick, on any thread, forever — and it is completely free of
/// synchronisation.
/// </summary>
public static class Rng
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Hash(int index, int tick)
    {
        uint x = (uint)index * 2654435761u ^ (uint)tick * 2246822519u;
        x ^= x >> 15; x *= 2246822519u;
        x ^= x >> 13; x *= 3266489917u;
        x ^= x >> 16;
        return x;
    }
}
