// Client side of the ViveClayViewer pipe, shared by the Grasshopper component and the test client.
// Everything here is non-blocking for the caller: control messages go into a queue, poses into an
// ordered queue, and a background thread owns the connection (auto-reconnects if the viewer is
// closed or restarted; stock and tool are replayed on every reconnect).
using System.Collections.Concurrent;
using System.IO.Pipes;

namespace ViveClay.Protocol;

public sealed class ClayPipeClient : IDisposable
{
    readonly string _pipe;
    readonly ConcurrentQueue<ClayMessage> _control = new();
    readonly SemaphoreSlim _signal = new(0);
    readonly Thread _thread;
    volatile bool _disposed;
    volatile bool _connected;
    readonly ConcurrentQueue<PoseMsg> _poses = new();   // every pose is delivered in order (the viewer sweeps between them)
    const int MaxQueuedPoses = 1024;                     // only while the viewer is away / stalled: oldest are dropped
    SetStockMsg? _stock; SetToolMsg? _tool; SetCutterMsg? _cutter; // replayed after reconnect
    readonly object _stateLock = new();
    long _connectionEpoch;
    RecordedPlanesMsg? _lastRecorded;                    // replayed after reconnect
    RecordedPlanesMsg? _recordedPending;                 // latest-wins slot (v3), sent after the control messages

    /// <summary>Raised on the client's background threads for every message received from the viewer.</summary>
    public event Action<ClayMessage>? MessageReceived;
    /// <summary>Raised when the connection comes up or goes away.</summary>
    public event Action<bool>? ConnectionChanged;

    public bool IsConnected => _connected;
    public int ViewerVersion { get; private set; }

    public ClayPipeClient(string pipeName = ClayWire.PipeName)
    {
        _pipe = pipeName;
        _thread = new Thread(Run) { IsBackground = true, Name = "ClayPipeClient" };
        _thread.Start();
    }

    public void SetStock(SetStockMsg m) { lock (_stateLock) _stock = m; _control.Enqueue(m); _signal.Release(); }
    public void SetTool(SetToolMsg m) { lock (_stateLock) _tool = m; _control.Enqueue(m); _signal.Release(); }
    public void SetCutter(SetCutterMsg m) { lock (_stateLock) _cutter = m; _control.Enqueue(m); _signal.Release(); }
    public void SendPose(PoseMsg m)
    {
        if (!_connected) { while (_poses.TryDequeue(out _)) { } }       // nothing to carve into: keep only the newest
        _poses.Enqueue(m);
        while (_poses.Count > MaxQueuedPoses) _poses.TryDequeue(out _);
        _signal.Release();
    }
    /// <summary>v3. Latest-wins: only the newest snapshot is kept and sent; nothing is queued up while the viewer is away.</summary>
    public void SetRecordedPlanes(RecordedPlanesMsg m) { Volatile.Write(ref _recordedPending, m); _signal.Release(); }
    public void Reset() { _control.Enqueue(new ResetMsg()); _signal.Release(); }
    public void RequestMesh() { _control.Enqueue(new RequestMeshMsg()); _signal.Release(); }

    void Run()
    {
        while (!_disposed)
        {
            NamedPipeClientStream? s = null;
            try
            {
                s = new NamedPipeClientStream(".", _pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
                s.Connect(300);     // Asynchronous (overlapped) so a pending Read never blocks Write. Throws TimeoutException when no viewer is running
            }
            catch
            {
                s?.Dispose();
                Thread.Sleep(700);
                continue;
            }

            var epoch = Interlocked.Increment(ref _connectionEpoch);
            try
            {
                // Fresh connection: drop stale control messages and replay current stock/tool instead.
                while (_control.TryDequeue(out _)) { }
                lock (_stateLock)
                {
                    if (_stock != null) _control.Enqueue(_stock);
                    if (_tool != null) _control.Enqueue(_tool);
                    if (_cutter != null) _control.Enqueue(_cutter);
                }
                if (_lastRecorded != null && Volatile.Read(ref _recordedPending) == null) Volatile.Write(ref _recordedPending, _lastRecorded);
                var stream = s;
                var reader = new Thread(() => ReadLoop(stream, epoch)) { IsBackground = true, Name = "ClayPipeClient.Read" };
                reader.Start();
                _connected = true;
                ConnectionChanged?.Invoke(true);
                SendLoop(s, epoch);
            }
            catch { /* connection lost; fall through and reconnect */ }
            finally
            {
                _connected = false;
                try { s.Dispose(); } catch { }
                ConnectionChanged?.Invoke(false);
            }
        }
    }

    void SendLoop(Stream s, long epoch)
    {
        while (!_disposed && Interlocked.Read(ref _connectionEpoch) == epoch && _connected)
        {
            _signal.Wait(250);                       // wake periodically to notice a dead connection
            while (_control.TryDequeue(out var m)) ClayWire.Write(s, m);
            var rec = Interlocked.Exchange(ref _recordedPending, null);
            if (rec != null) { _lastRecorded = rec; ClayWire.Write(s, rec); }
            while (_poses.TryDequeue(out var pose)) ClayWire.Write(s, pose);
        }
    }

    void ReadLoop(Stream s, long epoch)
    {
        try
        {
            while (!_disposed)
            {
                var m = ClayWire.Read(s);
                if (m == null || m is CloseMsg) break;
                if (m is HelloMsg h) ViewerVersion = h.Version;
                MessageReceived?.Invoke(m);
            }
        }
        catch { }
        // Reader ended => connection is dead; make the sender notice.
        if (Interlocked.Read(ref _connectionEpoch) == epoch) { _connected = false; _signal.Release(); }
    }

    public void Dispose()
    {
        _disposed = true;
        _signal.Release();
    }
}
