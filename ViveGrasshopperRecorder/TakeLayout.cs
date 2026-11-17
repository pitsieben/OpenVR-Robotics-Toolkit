namespace ViveTcpRecorder;

/// <summary>One recorded item of one device in one frame (T is the plane type; generic so the grouping is unit-testable without Rhino).</summary>
public sealed class DeviceFrame<T>
{
    public int Device;          // 0-based index of the Data branch the plane came from
    public T Item = default!;
    public double Time;         // seconds, monotonic clock
    public bool Trigger;
    public double TriggerValue;
}

/// <summary>All frames of one device in one take, with the output path they go to.</summary>
public sealed class TakeBranch<T>
{
    public int Device, Take;
    public int[] Path = Array.Empty<int>();
    public List<DeviceFrame<T>> Frames = new();
}

public static class TakeLayout
{
    /// <summary>
    /// Groups recorded frames into one branch per take and device, take-major.
    /// Path is {take} when only one device was recorded, otherwise {device;take}. Frames keep their recording order;
    /// takes without any frame of a device produce no branch for that device.
    /// </summary>
    public static List<TakeBranch<T>> Group<T>(IReadOnlyList<IReadOnlyList<DeviceFrame<T>[]>> takes)
    {
        var devices = new SortedSet<int>();
        foreach (var take in takes)
            foreach (var frame in take)
                foreach (var f in frame) devices.Add(f.Device);
        var multi = devices.Count > 1;

        var result = new List<TakeBranch<T>>();
        for (var t = 0; t < takes.Count; t++)
        {
            foreach (var d in devices)
            {
                var branch = new TakeBranch<T> { Device = d, Take = t, Path = multi ? new[] { d, t } : new[] { t } };
                foreach (var frame in takes[t])
                    foreach (var f in frame)
                        if (f.Device == d) branch.Frames.Add(f);
                if (branch.Frames.Count > 0) result.Add(branch);
            }
        }
        return result;
    }
}
