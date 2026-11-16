using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using ClayViewer.Stock;
using ViveClay.Protocol;

namespace ClayViewer;

/// <summary>Latest tool pose, converted to System.Numerics (row-vector) convention. Immutable.</summary>
public sealed class PoseState
{
    public Matrix4x4 Matrix;
    public double Timestamp;
    public bool Cutting;
    public long Seq;
}

/// <summary>Display mesh (tool or cutter), interleaved (pos xyz, normal xyz) per vertex, ready for GL upload.</summary>
public sealed class MeshUpload
{
    public float[] Interleaved = Array.Empty<float>();
    public int[] Indices = Array.Empty<int>();
    public Vector3 Tip;              // tool-local tip candidate (trail)
}

/// <summary>One re-meshed stock chunk on its way to the GPU. A new Epoch means "new stock: drop every chunk first".</summary>
public sealed class ChunkUpload
{
    public int Epoch;
    public StockBox Box;
    public ChunkMesh Mesh = new();
}

/// <summary>Carver statistics (written by the carving thread, read anywhere; approximate reads are fine).</summary>
public sealed class CarveStats
{
    public long PosesReceived, PosesCarved, PosesCoalesced, PosesDropped, PosesIdle;
    public long Sweeps, SubSteps;
    public double CarveMsTotal, CarveMsMax;
    public long Remeshes, ChunksRemeshed;
    public double RemeshMsTotal, RemeshMsMax;
    /// <summary>Seconds of pose time during which the cutter was actually sweeping through the stock.</summary>
    public double CutSeconds;
    public int MaxBacklog;
}

/// <summary>
/// Owns the carving thread and the hand-over between the pipe/demo (producers), the carving worker and the render
/// thread (consumer).
///   pipe/demo thread --Submit()--> command queue + ORDERED pose queue --> carving thread --> IClayStock
///   carving thread --> chunk upload queue --> render thread uploads to GL
/// Every pose is consumed in order and swept from its predecessor, so no part of a stroke is skipped. When the carver
/// is behind, consecutive poses are merged into one longer sweep (coalesced, still gap-free) and the sub-step length
/// is relaxed; poses are only ever dropped if the queue exceeds MaxQueue (reported as PosesDropped).
/// The render thread never waits on anything here.
/// </summary>
public sealed class ClayEngine : IDisposable
{
    const int MaxQueue = 4096;
    readonly Func<IClayStock> _factory;
    readonly ConcurrentQueue<Action> _commands = new();
    readonly ConcurrentQueue<PoseState> _poses = new();
    readonly ConcurrentQueue<ChunkUpload> _uploads = new();
    readonly AutoResetEvent _wake = new(false);
    readonly Thread _thread;
    volatile bool _stop;

    // --- slots shared with the render thread ---
    PoseState? _latestPose;
    MeshUpload? _pendingTool, _pendingCutter;
    RecordedPlanesMsg? _planes;          // latest-wins snapshot of the recorded planes (render thread only reads it)

    // --- carving-thread state ---
    IClayStock? _stock;
    SetStockMsg? _stockMsg;
    MeshData? _tool, _cutter;
    PoseState? _prev;
    int _epoch;
    StockBox _box;
    readonly Stopwatch _clock = Stopwatch.StartNew();
    long _lastStatusMs;
    double _lastSentVolume = -1;
    readonly List<ChunkMesh> _meshOut = new();

    // --- stats ---
    public readonly CarveStats Stats = new();
    long _seq;
    double _removedVolume, _initialVolume;
    string _stockInfo = "no stock";
    volatile string _connection = "waiting for Grasshopper";
    volatile float _stockTime;
    long _stockTimeBase;

    /// <summary>Set by the pipe server so replies (CarvedMesh, Status) reach the current client.</summary>
    public Action<ClayMessage>? Reply;

    public ClayEngine(Func<IClayStock>? factory = null)
    {
        _factory = factory ?? (() => new TriDexelStock());
        _thread = new Thread(Loop) { IsBackground = true, Name = "ClayCarver", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    // ------------------------------------------------------------ producer API (any thread)
    public void Submit(ClayMessage m)
    {
        switch (m)
        {
            case PoseMsg p:
                var ps = new PoseState
                {
                    Matrix = ToNumerics(p.Matrix), Timestamp = p.Timestamp, Cutting = p.CuttingEnabled,
                    Seq = Interlocked.Increment(ref _seq),
                };
                Interlocked.Increment(ref Stats.PosesReceived);
                Volatile.Write(ref _latestPose, ps);
                _poses.Enqueue(ps);
                break;
            case SetStockMsg s: _commands.Enqueue(() => DoSetStock(s)); break;
            case SetToolMsg t: _commands.Enqueue(() => { _tool = t.Mesh.TriangleCount > 0 ? t.Mesh : null; ToolsChanged(); }); break;
            case SetCutterMsg c: _commands.Enqueue(() => { _cutter = c.Mesh.TriangleCount > 0 ? c.Mesh : null; ToolsChanged(); }); break;
            case RecordedPlanesMsg r: Volatile.Write(ref _planes, r); return;     // render-side only, never wakes the carver
            case ResetMsg: _commands.Enqueue(DoReset); break;
            case RequestMeshMsg: _commands.Enqueue(DoRequestMesh); break;
            default: return;
        }
        _wake.Set();
    }

    public void SetConnection(string text) => _connection = text;

    /// <summary>Runs an action on the carving thread (bench / tests).</summary>
    public void Post(Action a) { _commands.Enqueue(a); _wake.Set(); }

    // ------------------------------------------------------------ consumer API (render thread)
    public PoseState? LatestPose => Volatile.Read(ref _latestPose);
    public bool TryTakeChunk(out ChunkUpload? c) => _uploads.TryDequeue(out c);
    public MeshUpload? TakePendingTool() => Interlocked.Exchange(ref _pendingTool, null);
    public MeshUpload? TakePendingCutter() => Interlocked.Exchange(ref _pendingCutter, null);
    public long PosesReceived => Interlocked.Read(ref Stats.PosesReceived);
    public long PosesCarved => Interlocked.Read(ref Stats.PosesCarved);
    public double RemovedVolume => Volatile.Read(ref _removedVolume);
    public string StockInfo => _stockInfo;
    /// <summary>Latest RecordedPlanes snapshot (null until a client sends one). A new reference means new data.</summary>
    public RecordedPlanesMsg? LatestPlanes => Volatile.Read(ref _planes);
    public StockBox Box => _box;
    public double ResolutionMm => _stockMsg?.ResolutionMm ?? 0;
    /// <summary>Stock volume right after SetStock (mm^3).</summary>
    public double InitialVolume => Volatile.Read(ref _initialVolume);
    public bool HasStock => _stockMsg != null;
    public string Connection => _connection;
    public int Backlog => _poses.Count;
    public long CellCount => _stock?.CellCount ?? 0;
    /// <summary>The stock (only touch it from the carving thread, e.g. via Post).</summary>
    public IClayStock? Stock => _stock;
    /// <summary>Current time on the stock clock used for per-vertex cut times (wet-sheen fade in the shader).</summary>
    public float StockTime => _stockTime + (_clock.ElapsedMilliseconds - Interlocked.Read(ref _stockTimeBase)) * 0.001f;

    // ------------------------------------------------------------ carving thread
    void Loop()
    {
        while (!_stop)
        {
            if (_poses.IsEmpty && _commands.IsEmpty) _wake.WaitOne(8);
            try
            {
                while (_commands.TryDequeue(out var cmd)) cmd();
                CarvePending();
                Remesh();
                PublishStatus();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[engine] " + ex);
                Reply?.Invoke(new StatusMsg { Text = "Viewer error: " + ex.Message, RemovedVolumeMm3 = _removedVolume });
            }
        }
    }

    /// <summary>Consume queued poses in order for at most ~10 ms, then let the mesher run.</summary>
    void CarvePending()
    {
        var start = Stopwatch.GetTimestamp();
        while (_poses.TryDequeue(out var cur))
        {
            var backlog = _poses.Count;
            if (backlog > Stats.MaxBacklog) Stats.MaxBacklog = backlog;
            while (backlog > MaxQueue && _poses.TryDequeue(out var skip)) { cur = skip; backlog--; Stats.PosesDropped++; _prev = null; }

            if (_stock == null || _stockMsg == null || (_cutter ?? _tool) == null || !cur.Cutting)
            {
                _prev = null; Stats.PosesIdle++; Interlocked.Increment(ref Stats.PosesCarved);
                continue;
            }
            // Behind: merge the next queued poses into this sweep (the path between them is still swept, as one segment).
            var merged = 0;
            while (backlog > 6 && merged < 3 && _poses.TryPeek(out var nx) && nx.Cutting &&
                   (nx.Matrix.Translation - cur.Matrix.Translation).Length() < 30f && _poses.TryDequeue(out nx))
            { cur = nx; merged++; backlog--; }
            Stats.PosesCoalesced += merged;

            // A tracking glitch / long gap must not carve a huge slice: only sweep when the jump is plausible.
            var prev = _prev;
            _prev = cur;
            if (prev == null || (cur.Matrix.Translation - prev.Matrix.Translation).Length() > 300f)
                prev = cur;     // zero-length sweep: carves the static pose
            else
            {
                var gap = cur.Timestamp - prev.Timestamp;
                if (gap > 0 && gap < 0.25) Stats.CutSeconds += gap;
            }
            _stock.StepFraction = backlog > 12 ? 1.0 : backlog > 4 ? 0.6 : 0.3;
            var t0 = Stopwatch.GetTimestamp();
            var steps = _stock.CarveSweep(prev.Matrix, cur.Matrix);
            var ms = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            Stats.Sweeps++; Stats.SubSteps += steps; Stats.CarveMsTotal += ms;
            if (ms > Stats.CarveMsMax) Stats.CarveMsMax = ms;
            Interlocked.Add(ref Stats.PosesCarved, 1 + merged);
            if (Stopwatch.GetElapsedTime(start).TotalMilliseconds > 10) break;
        }
    }

    void Remesh()
    {
        if (_stock == null || _stockMsg == null) return;
        var t0 = Stopwatch.GetTimestamp();
        _meshOut.Clear();
        var n = _stock.RemeshDirty(_meshOut);
        if (n == 0) return;
        Volatile.Write(ref _removedVolume, _stock.RemovedVolume);
        var ms = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        if (n < 1000)       // the full initial mesh is not a carving remesh: keep it out of the averages
        {
            Stats.Remeshes++; Stats.ChunksRemeshed += n; Stats.RemeshMsTotal += ms;
            if (ms > Stats.RemeshMsMax) Stats.RemeshMsMax = ms;
        }
        else Console.WriteLine($"[engine] full mesh: {n} chunks in {ms:0} ms");
        foreach (var m in _meshOut) _uploads.Enqueue(new ChunkUpload { Epoch = _epoch, Box = _box, Mesh = m });
        SyncStockTime();
    }

    void SyncStockTime()
    {
        if (_stock == null) return;
        _stockTime = _stock.Now;
        Interlocked.Exchange(ref _stockTimeBase, _clock.ElapsedMilliseconds);
    }

    void PublishStatus()
    {
        var now = _clock.ElapsedMilliseconds;
        if (now - _lastStatusMs < 500 || Reply == null) return;
        _lastStatusMs = now;
        var vol = _removedVolume;
        if (Math.Abs(vol - _lastSentVolume) < 1e-9) return;
        _lastSentVolume = vol;
        Reply(new StatusMsg { Text = $"{_stockInfo}; carved {Stats.PosesCarved} poses", RemovedVolumeMm3 = vol });
    }

    void DoSetStock(SetStockMsg m)
    {
        // A reconnecting client replays the same stock: keep the carving it already did.
        if (_stock != null && _stockMsg != null && SameStock(_stockMsg, m)) return;
        _stockMsg = m;
        _stock ??= _factory();
        var box = new StockBox(new Vector3((float)m.Min[0], (float)m.Min[1], (float)m.Min[2]),
                               new Vector3((float)m.Max[0], (float)m.Max[1], (float)m.Max[2]));
        _box = box;
        _epoch++;
        var sw = Stopwatch.StartNew();
        _stock.Init(box, m.ResolutionMm, m.Mesh);
        _prev = null;
        _removedVolume = 0;
        var bs = box.Size;
        Volatile.Write(ref _initialVolume, (_stock as TriDexelStock)?.RemainingVolume() ?? (double)bs.X * bs.Y * bs.Z);
        _stockInfo = _stock.Describe() + (m.Mesh != null ? " (mesh stock)" : "");
        Console.WriteLine($"[engine] stock {box.Size.X:0.#}x{box.Size.Y:0.#}x{box.Size.Z:0.#} mm, {_stockInfo}, {_stock.CellCount:N0} cells, init {sw.ElapsedMilliseconds} ms");
        SyncStockTime();
    }

    static bool SameStock(SetStockMsg a, SetStockMsg b) =>
        a.Min.SequenceEqual(b.Min) && a.Max.SequenceEqual(b.Max) && a.ResolutionMm == b.ResolutionMm &&
        (a.Mesh == null) == (b.Mesh == null) && a.Mesh?.Vertices.Length == b.Mesh?.Vertices.Length;

    /// <summary>Tool = what is drawn (handle, tracker). Cutter = what removes material; when absent the tool cuts.</summary>
    void ToolsChanged()
    {
        _prev = null;
        var eff = _cutter ?? _tool;
        if (eff == null) return;
        _stock ??= _factory();
        _stock.SetCutter(eff);
        var tip = MeshUtil.FarthestVertex(eff);
        Volatile.Write(ref _pendingCutter, new MeshUpload { Interleaved = MeshUtil.Interleave(eff), Indices = eff.Indices, Tip = tip });
        var disp = _cutter != null && _tool != null ? _tool : null;      // without a separate cutter the tool is drawn as the cutter
        Volatile.Write(ref _pendingTool, disp != null
            ? new MeshUpload { Interleaved = MeshUtil.Interleave(disp), Indices = disp.Indices, Tip = tip }
            : new MeshUpload { Tip = tip });
        Console.WriteLine($"[engine] cutter {eff.VertexCount} verts / {eff.TriangleCount} tris ({(_cutter != null ? "explicit Cutter" : "fallback: Tool mesh cuts")}), tip {tip}");
    }

    void DoReset()
    {
        if (_stock == null || _stockMsg == null) return;
        _stock.Reset();
        _prev = null;
        Stats.CutSeconds = 0;
        _removedVolume = 0;
        _lastSentVolume = -1;
        SyncStockTime();
        Console.WriteLine("[engine] reset");
    }

    void DoRequestMesh()
    {
        if (_stock == null || _stockMsg == null) { Reply?.Invoke(new StatusMsg { Text = "No stock set yet." }); return; }
        var mesh = _stock.ExportMesh();
        Reply?.Invoke(new CarvedMeshMsg { Mesh = mesh, RemovedVolumeMm3 = _stock.RemovedVolume });
    }

    /// <summary>Protocol matrix (row-major, column-vector) -> System.Numerics (row-vector, i.e. transposed).</summary>
    public static Matrix4x4 ToNumerics(double[] a) => new(
        (float)a[0], (float)a[4], (float)a[8], (float)a[12],
        (float)a[1], (float)a[5], (float)a[9], (float)a[13],
        (float)a[2], (float)a[6], (float)a[10], (float)a[14],
        (float)a[3], (float)a[7], (float)a[11], (float)a[15]);

    public void Dispose() { _stop = true; _wake.Set(); }
}

public static class MeshUtil
{
    /// <summary>Interleave pos+normal; computes smooth area-weighted normals when the mesh has none.</summary>
    public static float[] Interleave(MeshData m)
    {
        var n = m.Normals ?? ComputeNormals(m);
        var o = new float[m.Vertices.Length * 2];
        for (int i = 0, k = 0; i < m.Vertices.Length; i += 3, k += 6)
        {
            o[k] = m.Vertices[i]; o[k + 1] = m.Vertices[i + 1]; o[k + 2] = m.Vertices[i + 2];
            o[k + 3] = n[i]; o[k + 4] = n[i + 1]; o[k + 5] = n[i + 2];
        }
        return o;
    }

    public static float[] ComputeNormals(MeshData m)
    {
        var n = new float[m.Vertices.Length];
        var v = m.Vertices;
        for (var t = 0; t + 2 < m.Indices.Length; t += 3)
        {
            int a = m.Indices[t] * 3, b = m.Indices[t + 1] * 3, c = m.Indices[t + 2] * 3;
            var pa = new Vector3(v[a], v[a + 1], v[a + 2]);
            var fn = Vector3.Cross(new Vector3(v[b], v[b + 1], v[b + 2]) - pa, new Vector3(v[c], v[c + 1], v[c + 2]) - pa);
            n[a] += fn.X; n[a + 1] += fn.Y; n[a + 2] += fn.Z;
            n[b] += fn.X; n[b + 1] += fn.Y; n[b + 2] += fn.Z;
            n[c] += fn.X; n[c + 1] += fn.Y; n[c + 2] += fn.Z;
        }
        for (var i = 0; i < n.Length; i += 3)
        {
            var l = MathF.Sqrt(n[i] * n[i] + n[i + 1] * n[i + 1] + n[i + 2] * n[i + 2]);
            if (l > 1e-12f) { n[i] /= l; n[i + 1] /= l; n[i + 2] /= l; } else n[i + 2] = 1;
        }
        return n;
    }

    /// <summary>The tool tip heuristic: the vertex farthest from the tool-local origin (= the tracker).</summary>
    public static Vector3 FarthestVertex(MeshData m)
    {
        var best = Vector3.Zero; var bd = -1f;
        for (var i = 0; i < m.Vertices.Length; i += 3)
        {
            var p = new Vector3(m.Vertices[i], m.Vertices[i + 1], m.Vertices[i + 2]);
            var d = p.LengthSquared();
            if (d > bd) { bd = d; best = p; }
        }
        return best;
    }
}
