#pragma warning disable CA1416 // Validate platform compatibility for System.Drawing.Bitmap

using System;
using System.Collections.Generic;
using System.Drawing;
using Grasshopper.Kernel;
using Rhino.Geometry;

namespace ViveTcpRecorder;

public sealed class ViveToolCenterPointComponent : GH_Component
{
    private static readonly Lazy<Bitmap> ComponentIcon = new(LoadIcon);

    public ViveToolCenterPointComponent()
        : base(
            "Tool Center Point",
            "TCP",
            "Calculates the Tool Center Point (TCP) and fixed pivot point from at least 4 flange/tracker poses using least-squares calibration.",
            "Vive",
            "Calibration")
    {
    }

    public override Guid ComponentGuid => new("7F14D59C-6502-4A2B-864D-55BE2E715DF3");

    protected override Bitmap Icon => ComponentIcon.Value;

    protected override void RegisterInputParams(GH_InputParamManager pManager)
    {
        pManager.AddPlaneParameter(
            "Poses", "P",
            "List of at least 4 flange/tracker poses pointing at the fixed pivot tip from different orientations.",
            GH_ParamAccess.list);
    }

    protected override void RegisterOutputParams(GH_OutputParamManager pManager)
    {
        pManager.AddPointParameter(
            "TCP_Relative", "Rel",
            "TCP position relative to the flange coordinate frame (p_F).",
            GH_ParamAccess.item);

        pManager.AddPointParameter(
            "TCP", "TCP",
            "Tool Center Point in the current/first flange pose.",
            GH_ParamAccess.item);

        pManager.AddPointParameter(
            "FixedPoint", "Fix",
            "Fixed calibration pivot point in base/world coordinates (P_B).",
            GH_ParamAccess.item);

        pManager.AddNumberParameter(
            "Residual", "R",
            "RMS fitting error across all input poses in millimetres.",
            GH_ParamAccess.item);

        pManager.AddNumberParameter(
            "Errors", "E",
            "Individual error per pose in millimetres.",
            GH_ParamAccess.list);

        pManager.AddTextParameter(
            "Status", "S",
            "Calculation status.",
            GH_ParamAccess.item);
    }

    protected override void SolveInstance(IGH_DataAccess DA)
    {
        var flangePoses = new List<Plane>();
        if (!DA.GetDataList(0, flangePoses) || flangePoses.Count < 4)
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "At least 4 diverse flange poses are required to calculate the TCP.");
            DA.SetData(5, "Need at least 4 poses.");
            Message = "Need >= 4 poses";
            return;
        }

        int n = flangePoses.Count;

        // Create matrix A (3n x 6) and vector b (3n x 1)
        double[,] A = new double[3 * n, 6];
        double[] b = new double[3 * n];

        var transforms = new List<Transform>(n);

        for (int i = 0; i < n; i++)
        {
            Plane flangePose = flangePoses[i];
            Transform H = Transform.PlaneToPlane(Plane.WorldXY, flangePose);
            transforms.Add(H);

            double[,] R = {
                { H.M00, H.M01, H.M02 },
                { H.M10, H.M11, H.M12 },
                { H.M20, H.M21, H.M22 }
            };

            Vector3d t = new Vector3d(H.M03, H.M13, H.M23);

            for (int j = 0; j < 3; j++)
            {
                A[3 * i + j, 0] = R[j, 0] - (j == 0 ? 1.0 : 0.0);
                A[3 * i + j, 1] = R[j, 1] - (j == 1 ? 1.0 : 0.0);
                A[3 * i + j, 2] = R[j, 2] - (j == 2 ? 1.0 : 0.0);

                A[3 * i + j, 3] = (j == 0 ? 1.0 : 0.0);
                A[3 * i + j, 4] = (j == 1 ? 1.0 : 0.0);
                A[3 * i + j, 5] = (j == 2 ? 1.0 : 0.0);

                b[3 * i + j] = t[j];
            }
        }

        double[]? x;
        try
        {
            x = SolveLeastSquares(A, b);
        }
        catch (Exception ex)
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Solver failed: " + ex.Message);
            DA.SetData(5, "Solver error: " + ex.Message);
            Message = "Singular matrix";
            return;
        }

        Point3d tcp = new Point3d(x[0], x[1], x[2]);
        Point3d fixedPoint = new Point3d(x[3], x[4], x[5]);
        Point3d tcpRelative = tcp;

        // Calculate residual errors per pose
        var errors = new List<double>(n);
        double sumSq = 0.0;
        for (int i = 0; i < n; i++)
        {
            Point3d transformedTip = tcp;
            transformedTip.Transform(transforms[i]);
            double dist = transformedTip.DistanceTo(fixedPoint);
            errors.Add(dist);
            sumSq += dist * dist;
        }

        double rms = Math.Sqrt(sumSq / n);

        DA.SetData(0, tcpRelative);
        DA.SetData(1, tcp);
        DA.SetData(2, fixedPoint);
        DA.SetData(3, rms);
        DA.SetDataList(4, errors);
        DA.SetData(5, $"Success: {n} poses (RMS error: {rms:F3} mm)");

        Message = $"RMS: {rms:F2}mm";
    }

    public static double[] SolveLeastSquares(double[,] A, double[] b)
    {
        int rows = A.GetLength(0);
        int cols = A.GetLength(1);

        var At = new double[cols, rows];
        var AtA = new double[cols, cols];
        var Atb = new double[cols];

        for (int i = 0; i < rows; i++)
            for (int j = 0; j < cols; j++)
                At[j, i] = A[i, j];

        for (int i = 0; i < cols; i++)
            for (int j = 0; j < cols; j++)
                for (int k = 0; k < rows; k++)
                    AtA[i, j] += At[i, k] * A[k, j];

        for (int i = 0; i < cols; i++)
            for (int k = 0; k < rows; k++)
                Atb[i] += At[i, k] * b[k];

        return SolveLinearSystemWithPartialPivoting(AtA, Atb);
    }

    public static double[] SolveLinearSystemWithPartialPivoting(double[,] A, double[] b)
    {
        int n = A.GetLength(0);
        double[,] M = (double[,])A.Clone();
        double[] x = (double[])b.Clone();

        for (int k = 0; k < n; k++)
        {
            // Find pivot row
            int maxRow = k;
            double maxVal = Math.Abs(M[k, k]);
            for (int r = k + 1; r < n; r++)
            {
                if (Math.Abs(M[r, k]) > maxVal)
                {
                    maxVal = Math.Abs(M[r, k]);
                    maxRow = r;
                }
            }

            if (maxVal < 1e-12)
                throw new InvalidOperationException("Matrix is singular or near-singular. Please provide poses with greater angular variation.");

            // Swap rows if necessary
            if (maxRow != k)
            {
                for (int c = 0; c < n; c++)
                {
                    double tmp = M[k, c];
                    M[k, c] = M[maxRow, c];
                    M[maxRow, c] = tmp;
                }
                double tmpB = x[k];
                x[k] = x[maxRow];
                x[maxRow] = tmpB;
            }

            // Forward elimination
            for (int i = k + 1; i < n; i++)
            {
                double factor = M[i, k] / M[k, k];
                for (int j = k; j < n; j++)
                    M[i, j] -= factor * M[k, j];
                x[i] -= factor * x[k];
            }
        }

        // Back substitution
        double[] result = new double[n];
        for (int i = n - 1; i >= 0; i--)
        {
            double sum = 0.0;
            for (int j = i + 1; j < n; j++)
                sum += M[i, j] * result[j];
            result[i] = (x[i] - sum) / M[i, i];
        }

        return result;
    }

    private static Bitmap LoadIcon()
    {
        var stream = typeof(ViveToolCenterPointComponent).Assembly.GetManifestResourceStream("ViveTcpRecorder.Resources.Calibration.png");
        if (stream is null) return new Bitmap(24, 24);
        using (stream)
        {
            using var bmp = new Bitmap(stream);
            return new Bitmap(bmp, new Size(24, 24));
        }
    }
}
