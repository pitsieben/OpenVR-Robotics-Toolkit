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
    public double TiltMinDeg, TiltMeanDeg, TiltMaxDeg; // angle between the plane's Z axis and world Z, 0..180
    public double[] TiltsDeg = Array.Empty<double>();   // per plane
    public double[] SpeedsMmS = Array.Empty<double>();  // per plane: speed of the segment arriving at it (the first plane copies the second)
    public int FramesAboveLimit;     // planes whose tilt is greater than the limit (0 when no limit was given)
    public double PctAboveLimit;     // 100 * FramesAboveLimit / Frames
}

/// <summary>Pure analytics over recorded planes (no Rhino / Grasshopper types, unit-testable).</summary>
public static class TakeAnalytics
{
    /// <summary>Angle between the plane's Z axis (Ax, Ay, Az) and world Z in degrees (0 = plane Z along world Z, 180 = opposite).</summary>
    public static double TiltDeg(in PlaneSample s)
    {
        var len = Math.Sqrt(s.Ax * s.Ax + s.Ay * s.Ay + s.Az * s.Az);
        if (len < 1e-12) return 0;
        var c = Math.Clamp(s.Az / len, -1.0, 1.0);
        return Math.Acos(c) * 180.0 / Math.PI;
    }

    public static TakeStats Compute(int take, IReadOnlyList<PlaneSample> s) => Compute(take, s, double.NaN);

    /// <param name="limitDeg">Robot tilt limit; planes with a tilt greater than this are counted (NaN = no limit).</param>
    public static TakeStats Compute(int take, IReadOnlyList<PlaneSample> s, double limitDeg)
    {
        var st = new TakeStats { Take = take, Frames = s.Count };
        if (s.Count == 0) return st;
        var tilts = new double[s.Count];
        var speeds = new double[s.Count];
        double tMin = double.MaxValue, tMax = double.MinValue, tSum = 0, tiltMax = double.MinValue, tiltMin = double.MaxValue;
        for (var i = 0; i < s.Count; i++)
        {
            var t = TiltDeg(s[i]);
            tilts[i] = t;
            tSum += t; if (t < tiltMin) tiltMin = t; if (t > tiltMax) tiltMax = t;
            if (!double.IsNaN(limitDeg) && t > limitDeg) st.FramesAboveLimit++;
            if (s[i].T < tMin) tMin = s[i].T;
            if (s[i].T > tMax) tMax = s[i].T;
            if (i == 0) continue;
            var dx = s[i].X - s[i - 1].X; var dy = s[i].Y - s[i - 1].Y; var dz = s[i].Z - s[i - 1].Z;
            var d = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            st.PathLengthMm += d;
            var dt = s[i].T - s[i - 1].T;
            if (dt > 1e-6) { speeds[i] = d / dt; st.MaxSpeedMmS = Math.Max(st.MaxSpeedMmS, speeds[i]); }
        }
        if (s.Count > 1) speeds[0] = speeds[1];
        st.DurationSec = tMax - tMin;
        st.MeanSpeedMmS = st.DurationSec > 1e-9 ? st.PathLengthMm / st.DurationSec : 0;
        st.TiltMinDeg = tiltMin; st.TiltMaxDeg = tiltMax; st.TiltMeanDeg = tSum / s.Count;
        st.TiltsDeg = tilts; st.SpeedsMmS = speeds;
        st.PctAboveLimit = 100.0 * st.FramesAboveLimit / s.Count;
        return st;
    }

    /// <summary>Share of all planes of all takes whose tilt exceeds the limit, in percent (0 when there are none).</summary>
    public static double OverallPctAbove(IReadOnlyList<TakeStats> takes)
    {
        long n = 0, above = 0;
        foreach (var t in takes) { n += t.Frames; above += t.FramesAboveLimit; }
        return n == 0 ? 0 : 100.0 * above / n;
    }

    /// <summary>Removed volume as a percentage of the stock volume (0 when the stock volume is unknown).</summary>
    public static double PctOfStock(double removedMm3, double stockMm3) =>
        stockMm3 > 1e-9 ? 100.0 * removedMm3 / stockMm3 : 0;

    public static string Summary(IReadOnlyList<TakeStats> takes, double removedMm3, double stockMm3, double limitDeg, double resolutionMm = 0)
    {
        var ci = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.Append(string.Format(ci, "Removed {0:0} mm3 ({1:0.0} % of stock {2:0} mm3)", removedMm3, PctOfStock(removedMm3, stockMm3), stockMm3));
        if (resolutionMm > 0) sb.Append(string.Format(ci, ", resolution {0:0.##} mm", resolutionMm));
        if (takes.Count == 0) { sb.Append("\nNo recorded takes."); return sb.ToString(); }
        foreach (var t in takes)
        {
            sb.Append(string.Format(ci,
                "\nTake {0}: {1} frames, {2:0.0} s, path {3:0.0} mm, speed mean {4:0.0} / max {5:0.0} mm/s, tilt min {6:0.0} / mean {7:0.0} / max {8:0.0} deg",
                t.Take + 1, t.Frames, t.DurationSec, t.PathLengthMm, t.MeanSpeedMmS, t.MaxSpeedMmS, t.TiltMinDeg, t.TiltMeanDeg, t.TiltMaxDeg));
            if (!double.IsNaN(limitDeg)) sb.Append(string.Format(ci, ", {0:0.0} % above {1:0.#} deg", t.PctAboveLimit, limitDeg));
        }
        if (!double.IsNaN(limitDeg)) sb.Append(string.Format(ci, "\nOverall {0:0.0} % of planes above {1:0.#} deg.", OverallPctAbove(takes), limitDeg));
        return sb.ToString();
    }
}
