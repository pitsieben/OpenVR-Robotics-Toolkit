namespace ViveTcpRecorder;

/// <summary>Rigid (rotation + translation) fit between two corresponding point sets, Horn's quaternion method, doubles only.</summary>
public static class RigidFit
{
    /// <summary>Picks up to <paramref name="count"/> well-spread vertex indices (farthest-point sampling) from xyz triplets.</summary>
    public static int[] SelectSpread(double[] xyz, int count)
    {
        var n = xyz.Length / 3;
        if (n <= count) { var all = new int[n]; for (var i = 0; i < n; i++) all[i] = i; return all; }
        var pick = new int[count];
        var dist = new double[n];
        Array.Fill(dist, double.MaxValue);
        var cur = 0;
        for (var k = 0; k < count; k++)
        {
            pick[k] = cur;
            double best = -1; var next = 0;
            double cx = xyz[cur * 3], cy = xyz[cur * 3 + 1], cz = xyz[cur * 3 + 2];
            for (var i = 0; i < n; i++)
            {
                double dx = xyz[i * 3] - cx, dy = xyz[i * 3 + 1] - cy, dz = xyz[i * 3 + 2] - cz;
                var d = dx * dx + dy * dy + dz * dz;
                if (d < dist[i]) dist[i] = d;
                if (dist[i] > best) { best = dist[i]; next = i; }
            }
            cur = next;
        }
        return pick;
    }

    /// <summary>
    /// Finds M (row-major 4x4, p' = M p) minimising sum |M a_i - b_i|^2 over the selected indices, where a = reference, b = current.
    /// rms is the root-mean-square residual (same unit as the input).
    /// </summary>
    public static bool Fit(double[] reference, double[] current, int[] idx, out double[] m, out double rms)
    {
        m = new double[16]; rms = double.NaN;
        var n = idx.Length;
        if (n < 3) return false;
        double ax = 0, ay = 0, az = 0, bx = 0, by = 0, bz = 0;
        foreach (var i in idx)
        {
            ax += reference[i * 3]; ay += reference[i * 3 + 1]; az += reference[i * 3 + 2];
            bx += current[i * 3]; by += current[i * 3 + 1]; bz += current[i * 3 + 2];
        }
        ax /= n; ay /= n; az /= n; bx /= n; by /= n; bz /= n;
        double sxx = 0, sxy = 0, sxz = 0, syx = 0, syy = 0, syz = 0, szx = 0, szy = 0, szz = 0;
        foreach (var i in idx)
        {
            double pa = reference[i * 3] - ax, qa = reference[i * 3 + 1] - ay, ra = reference[i * 3 + 2] - az;
            double pb = current[i * 3] - bx, qb = current[i * 3 + 1] - by, rb = current[i * 3 + 2] - bz;
            sxx += pa * pb; sxy += pa * qb; sxz += pa * rb;
            syx += qa * pb; syy += qa * qb; syz += qa * rb;
            szx += ra * pb; szy += ra * qb; szz += ra * rb;
        }
        var N = new double[4, 4]
        {
            { sxx + syy + szz, syz - szy, szx - sxz, sxy - syx },
            { syz - szy, sxx - syy - szz, sxy + syx, szx + sxz },
            { szx - sxz, sxy + syx, -sxx + syy - szz, syz + szy },
            { sxy - syx, szx + sxz, syz + szy, -sxx - syy + szz },
        };
        var q = LargestEigenvector(N);       // w, x, y, z
        double w = q[0], x = q[1], y = q[2], z = q[3];
        var norm = Math.Sqrt(w * w + x * x + y * y + z * z);
        if (norm < 1e-12) return false;
        w /= norm; x /= norm; y /= norm; z /= norm;
        double r00 = 1 - 2 * (y * y + z * z), r01 = 2 * (x * y - w * z), r02 = 2 * (x * z + w * y);
        double r10 = 2 * (x * y + w * z), r11 = 1 - 2 * (x * x + z * z), r12 = 2 * (y * z - w * x);
        double r20 = 2 * (x * z - w * y), r21 = 2 * (y * z + w * x), r22 = 1 - 2 * (x * x + y * y);
        var tx = bx - (r00 * ax + r01 * ay + r02 * az);
        var ty = by - (r10 * ax + r11 * ay + r12 * az);
        var tz = bz - (r20 * ax + r21 * ay + r22 * az);
        m[0] = r00; m[1] = r01; m[2] = r02; m[3] = tx;
        m[4] = r10; m[5] = r11; m[6] = r12; m[7] = ty;
        m[8] = r20; m[9] = r21; m[10] = r22; m[11] = tz;
        m[15] = 1;
        double sum = 0;
        foreach (var i in idx)
        {
            double px = reference[i * 3], py = reference[i * 3 + 1], pz = reference[i * 3 + 2];
            var ex = r00 * px + r01 * py + r02 * pz + tx - current[i * 3];
            var ey = r10 * px + r11 * py + r12 * pz + ty - current[i * 3 + 1];
            var ez = r20 * px + r21 * py + r22 * pz + tz - current[i * 3 + 2];
            sum += ex * ex + ey * ey + ez * ez;
        }
        rms = Math.Sqrt(sum / n);
        return true;
    }

    /// <summary>Jacobi eigenvalue iteration for a symmetric 4x4; returns the eigenvector of the largest eigenvalue.</summary>
    static double[] LargestEigenvector(double[,] a0)
    {
        var a = (double[,])a0.Clone();
        var v = new double[4, 4];
        for (var i = 0; i < 4; i++) v[i, i] = 1;
        for (var sweep = 0; sweep < 60; sweep++)
        {
            double off = 0;
            for (var i = 0; i < 4; i++) for (var j = i + 1; j < 4; j++) off += a[i, j] * a[i, j];
            if (off < 1e-30) break;
            for (var p = 0; p < 3; p++)
                for (var q = p + 1; q < 4; q++)
                {
                    if (Math.Abs(a[p, q]) < 1e-300) continue;
                    var theta = (a[q, q] - a[p, p]) / (2 * a[p, q]);
                    var t = Math.Sign(theta == 0 ? 1 : theta) / (Math.Abs(theta) + Math.Sqrt(theta * theta + 1));
                    var c = 1 / Math.Sqrt(t * t + 1); var s = t * c;
                    for (var k = 0; k < 4; k++)
                    {
                        double akp = a[k, p], akq = a[k, q];
                        a[k, p] = c * akp - s * akq; a[k, q] = s * akp + c * akq;
                    }
                    for (var k = 0; k < 4; k++)
                    {
                        double apk = a[p, k], aqk = a[q, k];
                        a[p, k] = c * apk - s * aqk; a[q, k] = s * apk + c * aqk;
                    }
                    for (var k = 0; k < 4; k++)
                    {
                        double vkp = v[k, p], vkq = v[k, q];
                        v[k, p] = c * vkp - s * vkq; v[k, q] = s * vkp + c * vkq;
                    }
                }
        }
        var best = 0;
        for (var i = 1; i < 4; i++) if (a[i, i] > a[best, best]) best = i;
        return new[] { v[0, best], v[1, best], v[2, best], v[3, best] };
    }
}
