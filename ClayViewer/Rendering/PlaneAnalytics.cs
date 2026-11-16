using System.Numerics;
using ViveClay.Protocol;

namespace ClayViewer.Rendering;

/// <summary>Per-take statistics of the recorded planes. Indices point into PlaneAnalytics.Order.</summary>
public sealed class TakeStats
{
    public int Take, First, Count;
    public double Duration, PathMm, MeanSpeed, MaxSpeed;
    public float MinTilt, MeanTilt, MaxTilt;
}

/// <summary>
/// Analytics over one RecordedPlanes snapshot, computed on the render thread when a new snapshot arrives (at most 5 Hz).
/// Tilt = angle between the tool axis (plane Z) and world Z, folded into 0..90 deg (the sign of the axis is irrelevant
/// to the robot's reach, only its inclination).
/// </summary>
public sealed class PlaneAnalytics
{
    public const int Bins = 18;               // 5 deg bins, 0..90
    public RecordedPlanesMsg Source = new();
    public Vector3[] Pos = Array.Empty<Vector3>();     // in Order
    public Vector3[] Axis = Array.Empty<Vector3>();    // in Order, flipped to point up (z >= 0)
    public float[] Tilt = Array.Empty<float>();        // in Order, degrees
    public double[] Time = Array.Empty<double>();
    public readonly List<TakeStats> Takes = new();
    public readonly int[] Histogram = new int[Bins];
    public float Mean, P95, Max;
    public int Count => Tilt.Length;

    public static PlaneAnalytics Compute(RecordedPlanesMsg m)
    {
        var a = new PlaneAnalytics { Source = m };
        var n = Math.Min(m.Count, Math.Min(m.Origin.Length / 3, Math.Min(m.Axis.Length / 3, m.Time.Length)));
        // stable order: by take, then time
        var order = Enumerable.Range(0, n).OrderBy(i => m.Take[i]).ThenBy(i => m.Time[i]).ToArray();
        a.Pos = new Vector3[n]; a.Axis = new Vector3[n]; a.Tilt = new float[n]; a.Time = new double[n];
        for (var k = 0; k < n; k++)
        {
            var i = order[k];
            a.Pos[k] = new Vector3(m.Origin[3 * i], m.Origin[3 * i + 1], m.Origin[3 * i + 2]);
            var ax = new Vector3(m.Axis[3 * i], m.Axis[3 * i + 1], m.Axis[3 * i + 2]);
            var len = ax.Length();
            ax = len > 1e-9f ? ax / len : Vector3.UnitZ;
            if (ax.Z < 0) ax = -ax;
            a.Axis[k] = ax;
            a.Tilt[k] = MathF.Acos(Math.Clamp(ax.Z, 0f, 1f)) * 180f / MathF.PI;
            a.Time[k] = m.Time[i];
        }

        // per take
        for (var k = 0; k < n;)
        {
            var take = m.Take[order[k]];
            var s = new TakeStats { Take = take, First = k, MinTilt = float.MaxValue };
            double tiltSum = 0, prevV = 0;
            var e = k;
            for (; e < n && m.Take[order[e]] == take; e++)
            {
                var t = a.Tilt[e];
                s.MinTilt = Math.Min(s.MinTilt, t); s.MaxTilt = Math.Max(s.MaxTilt, t); tiltSum += t;
                if (e > k)
                {
                    var d = Vector3.Distance(a.Pos[e], a.Pos[e - 1]);
                    var dt = a.Time[e] - a.Time[e - 1];
                    s.PathMm += d;
                    if (dt > 1e-3)
                    {
                        // two-sample average damps tracker jitter in the max-speed figure
                        var v = d / dt;
                        s.MaxSpeed = Math.Max(s.MaxSpeed, e > k + 1 ? 0.5 * (v + prevV) : v);
                        prevV = v;
                    }
                }
            }
            s.Count = e - k;
            s.MeanTilt = (float)(tiltSum / s.Count);
            s.Duration = a.Time[e - 1] - a.Time[k];
            s.MeanSpeed = s.Duration > 1e-6 ? s.PathMm / s.Duration : 0;
            a.Takes.Add(s);
            k = e;
        }

        if (n > 0)
        {
            double sum = 0;
            foreach (var t in a.Tilt)
            {
                sum += t;
                a.Histogram[Math.Clamp((int)(t / 5f), 0, Bins - 1)]++;
                a.Max = Math.Max(a.Max, t);
            }
            a.Mean = (float)(sum / n);
            var sorted = (float[])a.Tilt.Clone();
            Array.Sort(sorted);
            a.P95 = sorted[Math.Clamp((int)Math.Ceiling(0.95 * n) - 1, 0, n - 1)];
        }
        return a;
    }

    public int CountAbove(float limitDeg)
    {
        var c = 0;
        foreach (var t in Tilt) if (t > limitDeg) c++;
        return c;
    }

    public int CountAbove(TakeStats s, float limitDeg)
    {
        var c = 0;
        for (var i = s.First; i < s.First + s.Count; i++) if (Tilt[i] > limitDeg) c++;
        return c;
    }

    /// <summary>Tilt colour ramp: cool blue (vertical) -> teal -> amber -> orange at the robot limit, red above it.</summary>
    public static Vector3 TiltColor(float tiltDeg, float limitDeg)
    {
        if (tiltDeg > limitDeg) return new Vector3(0.94f, 0.24f, 0.26f);
        var u = Math.Clamp(tiltDeg / Math.Max(1f, limitDeg), 0f, 1f);
        ReadOnlySpan<float> st = stackalloc float[] { 0f, 0.45f, 0.8f, 1f };
        Span<Vector3> c = stackalloc Vector3[]
        {
            new(0.27f, 0.56f, 1.00f), new(0.25f, 0.82f, 0.78f), new(0.96f, 0.82f, 0.30f), new(1.00f, 0.56f, 0.18f),
        };
        for (var i = 1; i < st.Length; i++)
            if (u <= st[i]) return Vector3.Lerp(c[i - 1], c[i], (u - st[i - 1]) / (st[i] - st[i - 1]));
        return c[^1];
    }
}
