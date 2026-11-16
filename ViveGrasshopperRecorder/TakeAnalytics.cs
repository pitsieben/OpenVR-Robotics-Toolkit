using System.Globalization;
using System.Text;

namespace ViveTcpRecorder;

/// <summary>One recorded frame, in millimetres / seconds. Axis is the tool axis (the plane's Z, unit length).</summary>
public readonly struct PlaneSample
{
    public readonly double X, Y, Z, Ax, Ay, Az, T;
    public PlaneSample(double x, double y, double z, double ax, double ay, double az, double t)
    { X = x; Y = y; Z = z; Ax = ax; Ay = ay; Az = az; T = t; }
}

public sealed class TakeStats
{
    public int Take;                 // 0-based take index
    public int Frames;
    public double PathLengthMm;
    public double DurationSec;
    public double MeanSpeedMmS;      // path length / duration (0 when the duration is 0)
    public double MaxSpeedMmS;       // largest segment speed (segments with dt <= 1 microsecond are skipped)
    public double TiltMinDeg, TiltMeanDeg, TiltMaxDeg; // angle between the tool axis and world +Z
}

/// <summary>Pure analytics over recorded planes (no Rhino / Grasshopper types, unit-testable).</summary>
public static class TakeAnalytics
{
    public static double TiltDeg(in PlaneSample s)
    {
        var len = Math.Sqrt(s.Ax * s.Ax + s.Ay * s.Ay + s.Az * s.Az);
        if (len < 1e-12) return 0;
        var c = Math.Clamp(s.Az / len, -1.0, 1.0);
        return Math.Acos(c) * 180.0 / Math.PI;
    }

    public static TakeStats Compute(int take, IReadOnlyList<PlaneSample> s)
    {
        var st = new TakeStats { Take = take, Frames = s.Count };
        if (s.Count == 0) return st;
        double tMin = double.MaxValue, tMax = double.MinValue, tSum = 0, tiltMax = double.MinValue, tiltMin = double.MaxValue;
        for (var i = 0; i < s.Count; i++)
        {
            var t = TiltDeg(s[i]);
            tSum += t; if (t < tiltMin) tiltMin = t; if (t > tiltMax) tiltMax = t;
            if (s[i].T < tMin) tMin = s[i].T;
            if (s[i].T > tMax) tMax = s[i].T;
            if (i == 0) continue;
            var dx = s[i].X - s[i - 1].X; var dy = s[i].Y - s[i - 1].Y; var dz = s[i].Z - s[i - 1].Z;
            var d = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            st.PathLengthMm += d;
            var dt = s[i].T - s[i - 1].T;
            if (dt > 1e-6) st.MaxSpeedMmS = Math.Max(st.MaxSpeedMmS, d / dt);
        }
        st.DurationSec = tMax - tMin;
        st.MeanSpeedMmS = st.DurationSec > 1e-9 ? st.PathLengthMm / st.DurationSec : 0;
        st.TiltMinDeg = tiltMin; st.TiltMaxDeg = tiltMax; st.TiltMeanDeg = tSum / s.Count;
        return st;
    }

    public static string Summary(IReadOnlyList<TakeStats> takes, bool recording, int currentTake, double removedMm3)
    {
        var ci = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.Append(string.Format(ci, "Removed volume: {0:0} mm3", removedMm3));
        if (takes.Count == 0) { sb.Append("\nNo recorded takes."); return sb.ToString(); }
        foreach (var t in takes)
        {
            sb.Append(string.Format(ci,
                "\nTake {0}{1}: {2} frames, path {3:0.0} mm, speed mean {4:0.0} / max {5:0.0} mm/s, tilt min {6:0.0} / mean {7:0.0} / max {8:0.0} deg",
                t.Take + 1, recording && t.Take == currentTake ? " (recording)" : "",
                t.Frames, t.PathLengthMm, t.MeanSpeedMmS, t.MaxSpeedMmS, t.TiltMinDeg, t.TiltMeanDeg, t.TiltMaxDeg));
        }
        return sb.ToString();
    }
}
