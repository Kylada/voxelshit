using System.Diagnostics;

namespace VoxelEngine;

/// <summary>
/// Decides *how* the queued voxels get simulated — one thread, or many.
///
/// This is deliberately an interface: the physics rules live in World and do
/// not know or care which scheduler drives them. You can swap the strategy at
/// runtime, benchmark them against each other, and keep the simple one as a
/// correctness reference that the clever one must match.
/// </summary>
public interface IPhysicsScheduler
{
    string Name { get; }
    void Tick(World world);
}

/// <summary>
/// One thread, front to back. Simple, deterministic, and — for this workload at
/// 128^3 — usually the fastest option, because a settled world has almost no
/// work to hand out.
/// </summary>
public sealed class SerialScheduler : IPhysicsScheduler
{
    public string Name => "serial";

    public void Tick(World world)
    {
        long t0 = Stopwatch.GetTimestamp();
        world.BeginTick();

        var list = world.Processing;
        int n = world.ProcessingCount;
        var sink = world.Pending;
        int lo = 0, hi = world.Data.Length;
        for (int k = 0; k < n; k++) world.StepVoxel(list[k], sink, lo, hi);

        world.MarkTickTiming(t0);
    }
}

/// <summary>
/// Multi-threaded tick by SPATIAL PARTITIONING.
///
/// The naive idea — "just wrap the loop in Parallel.For" — is wrong here and it
/// is worth understanding why. Two voxels in different parts of the world can
/// still touch the SAME cell (a grain of sand falls onto a cell that some other
/// grain also wants to fall into). Two threads swapping the same pair of cells
/// at the same time corrupts the array, and worse, does so rarely and
/// non-reproducibly. A lock per cell would serialise everything useful.
///
/// So we split the world into slabs along Z and give each thread exclusive
/// write access to its own slab:
///
///   1. Bucket the tick's work list by slab (counting sort, O(n)).
///   2. Each thread simulates its slab, but any move that would write into a
///      neighbouring slab is REFUSED and the voxel is remembered.
///      -> threads never write to the same memory, so no locks in the hot path.
///   3. Those refused voxels are retried serially afterwards, with full access.
///      That is the safety net that keeps the result identical to serial.
///   4. Each thread collected its wake-ups in its own little list; a short
///      serial merge de-duplicates them into the real queue.
///
/// Where the win comes from: only steps 2 and 3 are heavy, and step 2 is
/// independent per slab. Where the cost comes from: splitting, merging, and the
/// barrier between phases. Which is why there is a threshold below: for a small
/// amount of work the orchestrating costs more than the work.
/// </summary>
public sealed class ParallelScheduler : IPhysicsScheduler
{
    public string Name => "parallel";

    private readonly int _slabs;
    private readonly int _minWork;
    private readonly object _mergeLock = new();

    /// <summary>Reused scratch so a tick does not allocate.</summary>
    private int[] _ordered = Array.Empty<int>();
    private int[] _bucketStart = Array.Empty<int>();
    private int[] _zStart = Array.Empty<int>();
    private int[] _zEnd = Array.Empty<int>();

    /// <param name="slabs">How many pieces to cut the world into. Default: one per core.</param>
    /// <param name="minWork">Below this many queued voxels, just run serially.</param>
    public ParallelScheduler(int? slabs = null, int minWork = 20_000)
    {
        _slabs = Math.Max(1, slabs ?? Environment.ProcessorCount);
        _minWork = minWork;

        _bucketStart = new int[_slabs + 1];
        _zStart = new int[_slabs];
        _zEnd = new int[_slabs];

        // Padded z range that actually contains world data.
        int zLo = World.Pad, zHi = World.PS - World.Pad;
        int span = zHi - zLo;
        for (int s = 0; s < _slabs; s++)
        {
            _zStart[s] = zLo + s * span / _slabs;
            _zEnd[s] = zLo + (s + 1) * span / _slabs;
        }
    }

    public int SlabCount => _slabs;

    public void Tick(World world)
    {
        long t0 = Stopwatch.GetTimestamp();
        world.BeginTick();

        int n = world.ProcessingCount;
        if (n < _minWork || _slabs <= 1)
        {
            var list = world.Processing;
            for (int k = 0; k < n; k++)
                world.StepVoxel(list[k], world.Pending, 0, world.Data.Length);
            world.MarkTickTiming(t0);
            return;
        }

        if (_ordered.Length < n) _ordered = new int[Math.Max(n, _ordered.Length * 2)];
        var src = world.Processing;
        var ordered = _ordered;

        // ---- 1. bucket by slab with a counting sort -------------------------
        Array.Clear(_bucketStart);
        for (int k = 0; k < n; k++) _bucketStart[SlabOf(World.ZOf(src[k])) + 1]++;
        for (int s = 0; s < _slabs; s++) _bucketStart[s + 1] += _bucketStart[s];

        var cursor = new int[_slabs];   // small (one per thread), allocation is fine
        for (int k = 0; k < n; k++)
        {
            int slab = SlabOf(World.ZOf(src[k]));
            ordered[_bucketStart[slab] + cursor[slab]++] = src[k];
        }

        // ---- 2. simulate each slab in parallel ------------------------------
        // Parallel.For with thread-local state: every worker gets its own Sink,
        // so no two threads ever write the same queue. The finally-delegate runs
        // once per worker to merge results under a lock.
        var mergeLock = _mergeLock;
        var queuedFlags = world.QueuedFlags;

        System.Threading.Tasks.Parallel.For(
            0, _slabs,
            // one WorkerState per thread, reused for the whole tick
            () => new WorkerState(queuedFlags),
            (s, _, w) =>
            {
                w.Sink.Count = 0;
                w.Retries.Clear();
                int lo = _bucketStart[s], hi = _bucketStart[s + 1];
                int writeLo = _zStart[s] * World.PS2;
                int writeHi = _zEnd[s] * World.PS2;

                for (int k = lo; k < hi; k++)
                {
                    int idx = ordered[k];
                    // Note the sink de-duplicates against the SHARED flag array,
                    // so each worker only keeps genuinely new wake-ups. Without
                    // that, every move would push 27 neighbours (mostly repeats)
                    // and the merge below would dominate the whole tick.
                    if (world.StepVoxel(idx, w.Sink, writeLo, writeHi))
                        w.Retries.Add(idx);   // wanted to cross a slab border
                }
                return w;
            },
            w =>
            {
                // Merge WITHOUT de-duplicating again: every entry here already
                // claimed its flag, and re-checking it would drop duplicates
                // that we actually want (see World.QueuedFlags).
                lock (mergeLock)
                {
                    var dest = world.Pending;
                    for (int k = 0; k < w.Sink.Count; k++)
                    {
                        int idx = w.Sink.Items[k];
                        if (dest.Count == dest.Items.Length) Array.Resize(ref dest.Items, dest.Items.Length * 2);
                        dest.Items[dest.Count++] = idx;
                    }
                    world.RetryList.AddRange(w.Retries);
                }
            });

        // ---- 3. retry the border-straddling voxels serially ------------------
        var retries = world.RetryList;
        for (int k = 0; k < retries.Count; k++)
            world.StepVoxel(retries[k], world.Pending, 0, world.Data.Length);
        retries.Clear();

        world.MarkTickTiming(t0);
    }

    private sealed class WorkerState
    {
        public readonly Sink Sink;
        public readonly List<int> Retries = new(64);
        public WorkerState(byte[] sharedFlags) => Sink = new Sink(1 << 15, sharedFlags);
    }

    private int SlabOf(int paddedZ)
    {
        int zLo = World.Pad;
        int span = World.PS - 2 * World.Pad;
        int s = (paddedZ - zLo) * _slabs / span;
        return s < 0 ? 0 : (s >= _slabs ? _slabs - 1 : s);
    }
}
