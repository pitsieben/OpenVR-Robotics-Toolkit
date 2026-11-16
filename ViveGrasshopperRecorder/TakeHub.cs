namespace ViveTcpRecorder;

/// <summary>Immutable copy of one recorder's state, handed to subscribers (e.g. the Clay Viewer component).</summary>
public sealed class TakeHubSnapshot
{
    public long Version;
    public bool Recording;
    /// <summary>0-based take index: the take being recorded now, or (when idle) the index the next take will get.</summary>
    public int CurrentTake;
    public PlaneSample[][] Takes = Array.Empty<PlaneSample[]>();
}

/// <summary>
/// In-process hub between Vive Take Recorder components (writers) and anything that wants to follow them (readers),
/// without wires. Only frames that were actually recorded are stored. When several recorders exist, readers get the
/// MOST RECENTLY UPDATED one (the recorder that last recorded a frame, started/ended a take or was cleared).
/// </summary>
public static class TakeHub
{
    sealed class State
    {
        public readonly List<List<PlaneSample>> Takes = new();
        public bool Recording;
        public long Version, Stamp;
    }

    static readonly object Gate = new();
    static readonly Dictionary<Guid, State> Recorders = new();
    static long _stamp;
    static Guid _latest;

    /// <summary>Raised (on the writer's thread) after any change. Handlers must be quick and thread-safe.</summary>
    public static event Action? Changed;

    static State Get(Guid id)
    {
        if (!Recorders.TryGetValue(id, out var s)) Recorders[id] = s = new State();
        s.Version++; s.Stamp = ++_stamp; _latest = id;
        return s;
    }

    public static void StartTake(Guid id)
    {
        lock (Gate) { var s = Get(id); DropEmptyCurrent(s); s.Takes.Add(new List<PlaneSample>()); s.Recording = true; }
        Changed?.Invoke();
    }

    public static void AddSample(Guid id, PlaneSample sample)
    {
        lock (Gate)
        {
            var s = Get(id);
            if (!s.Recording || s.Takes.Count == 0) { s.Takes.Add(new List<PlaneSample>()); s.Recording = true; }
            s.Takes[^1].Add(sample);
        }
        Changed?.Invoke();
    }

    public static void EndTake(Guid id)
    {
        lock (Gate) { var s = Get(id); s.Recording = false; DropEmptyCurrent(s); }
        Changed?.Invoke();
    }

    public static void Clear(Guid id)
    {
        lock (Gate) { var s = Get(id); s.Takes.Clear(); s.Recording = false; }
        Changed?.Invoke();
    }

    public static void Remove(Guid id)
    {
        lock (Gate)
        {
            Recorders.Remove(id);
            if (_latest == id) _latest = Recorders.OrderByDescending(k => k.Value.Stamp).Select(k => k.Key).FirstOrDefault();
        }
        Changed?.Invoke();
    }

    static void DropEmptyCurrent(State s)
    {
        if (s.Takes.Count > 0 && s.Takes[^1].Count == 0 && !s.Recording) s.Takes.RemoveAt(s.Takes.Count - 1);
    }

    /// <summary>Cheap change counter of the latest recorder (0 when none): compare before taking a snapshot.</summary>
    public static long CurrentVersion
    {
        get { lock (Gate) return Recorders.TryGetValue(_latest, out var s) ? s.Version + s.Stamp * 1_000_003L : 0; }
    }

    public static TakeHubSnapshot? Snapshot()
    {
        lock (Gate)
        {
            if (!Recorders.TryGetValue(_latest, out var s)) return null;
            var takes = new PlaneSample[s.Takes.Count][];
            for (var i = 0; i < takes.Length; i++) takes[i] = s.Takes[i].ToArray();
            return new TakeHubSnapshot
            {
                Version = s.Version + s.Stamp * 1_000_003L,
                Recording = s.Recording,
                CurrentTake = s.Recording ? s.Takes.Count - 1 : s.Takes.Count,
                Takes = takes,
            };
        }
    }
}
