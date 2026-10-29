#pragma warning disable CA1416 // Validate platform compatibility for System.Drawing.Bitmap

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Grasshopper.Kernel;
using Rhino;
using Rhino.Geometry;

namespace ViveTcpRecorder;

public sealed class ViveTcpRecorderComponentV2 : GH_Component
{
    private const int PublisherPort = 7000;
    private static readonly Lazy<Bitmap> ComponentIcon = new(LoadIcon);

    private readonly object _gate = new();
    private readonly object _scheduleGate = new();

    private CancellationTokenSource? _cancellation;
    private Task? _receiverTask;
    private string? _activeEndpoint;
    private string _status = "Idle";

    private ViveTrackData _latestData = new();
    private Plane _latestPlane = Plane.WorldXY;
    private double _latestTime;
    private bool _hasData;
    private bool _updateScheduled;
    private readonly Stopwatch _frameStopwatch = Stopwatch.StartNew();
    private const int MinUpdateIntervalMs = 20;

    public ViveTcpRecorderComponentV2()
        : base(
            "Vive TCP Listener V2",
            "ViveListener",
            "Streams OpenVR Vive tracking data (Trackers and Lighthouses) in real-time over TCP.",
            "Vive",
            "TCP")
    {
    }

    public override Guid ComponentGuid => new("C46881DC-94DC-462E-9D72-BC83E9A34C7A");

    protected override Bitmap Icon => ComponentIcon.Value;

    protected override void RegisterInputParams(GH_InputParamManager parameters)
    {
        parameters.AddBooleanParameter(
            "AutoUpdate", "AutoUpdate",
            "Set to True to connect to the Vive TCP publisher and stream live tracking data.",
            GH_ParamAccess.item,
            false);

        parameters.AddTextParameter(
            "IP", "IP",
            "IP address and optional port of ViveTcpPublisher (e.g. '127.0.0.1' or '127.0.0.1:7000'). Default port is 7000.",
            GH_ParamAccess.item,
            "127.0.0.1:7000");
        parameters[1].Optional = true;
    }

    protected override void RegisterOutputParams(GH_OutputParamManager parameters)
    {
        parameters.AddTextParameter(
            "Msg", "Msg",
            "Running information and device summary of your Vive tracking stream.",
            GH_ParamAccess.item);

        parameters.AddGenericParameter(
            "Vive", "Vive",
            "The Core of Vive tracking data context, passed to downstream Tracker and Lighthouse components.",
            GH_ParamAccess.item);

        parameters.AddPlaneParameter(
            "Plane", "Plane",
            "Primary live tracker plane representation with Z facing UP out of top dome.",
            GH_ParamAccess.item);

        parameters.AddNumberParameter(
            "Time", "Time",
            "Latest timestamp in seconds from publisher.",
            GH_ParamAccess.item);
    }

    protected override void SolveInstance(IGH_DataAccess dataAccess)
    {
        var autoUpdate = false;
        var ip = "127.0.0.1:7000";

        dataAccess.GetData(0, ref autoUpdate);
        dataAccess.GetData(1, ref ip);

        ip = string.IsNullOrWhiteSpace(ip) ? "127.0.0.1:7000" : ip.Trim();

        if (autoUpdate)
        {
            if (!TryParseEndpoint(ip, out var address, out var port))
            {
                StopStreaming();
                SetOutput(dataAccess, "Invalid IP or IP:Port format.");
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "IP must be a numeric address or address:port (e.g. 127.0.0.1 or 127.0.0.1:7000).");
                return;
            }

            var endpointKey = $"{address}:{port}";
            if (_cancellation is null || !string.Equals(_activeEndpoint, endpointKey, StringComparison.OrdinalIgnoreCase))
            {
                StartStreaming(endpointKey, address, port);
            }
        }
        else
        {
            StopStreaming();
        }

        SetOutput(dataAccess, null);
    }

    public override void RemovedFromDocument(GH_Document document)
    {
        StopStreaming();
        base.RemovedFromDocument(document);
    }

    private void StartStreaming(string endpointKey, IPAddress address, int port)
    {
        StopStreaming();

        var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        _activeEndpoint = endpointKey;
        SetStatus("Connecting");

        _receiverTask = Task.Run(() => ReceiveLoopAsync(address, port, cancellation.Token));
    }

    private void StopStreaming()
    {
        CancellationTokenSource? cancellation;
        lock (_gate)
        {
            cancellation = _cancellation;
            _cancellation = null;
            _activeEndpoint = null;
            _status = "Disconnected";
            _hasData = false;
        }

        if (cancellation is null)
            return;

        try
        {
            cancellation.Cancel();
            cancellation.Dispose();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task ReceiveLoopAsync(IPAddress address, int port, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient? client = null;
            try
            {
                SetStatus("Connecting");
                client = new TcpClient();
                using (cancellationToken.Register(() => client.Dispose()))
                {
                    await client.ConnectAsync(address, port, cancellationToken);
                }

                SetStatus("Connected");
                using var stream = client.GetStream();
                using var reader = new System.IO.StreamReader(stream, Encoding.UTF8);

                while (!cancellationToken.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(cancellationToken);
                    if (line is null)
                        break;

                    if (string.IsNullOrWhiteSpace(line))
                        continue;

                    // Drain backlog in network buffer to keep only the latest state
                    while (stream.DataAvailable)
                    {
                        var next = reader.ReadLine();
                        if (!string.IsNullOrWhiteSpace(next))
                            line = next;
                    }

                    if (TryParsePacket(line, out var trackData, out var primaryPlane))
                    {
                        lock (_gate)
                        {
                            _latestData = trackData;
                            _latestPlane = primaryPlane;
                            _latestTime = trackData.Timestamp;
                            _hasData = true;
                        }

                        ScheduleDocumentUpdate();
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                SetStatus($"Disconnected: {ex.Message}");
            }
            finally
            {
                client?.Dispose();
            }

            if (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(500, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        SetStatus("Disconnected");
    }

    private void ScheduleDocumentUpdate()
    {
        lock (_scheduleGate)
        {
            if (_updateScheduled || _frameStopwatch.ElapsedMilliseconds < MinUpdateIntervalMs)
                return;

            _updateScheduled = true;
        }

        RhinoApp.InvokeOnUiThread(new Action(() =>
        {
            var document = OnPingDocument();
            if (document is null || _cancellation is null || _cancellation.IsCancellationRequested)
            {
                lock (_scheduleGate)
                    _updateScheduled = false;
                return;
            }

            document.ScheduleSolution(1, _ =>
            {
                lock (_scheduleGate)
                {
                    _frameStopwatch.Restart();
                    _updateScheduled = false;
                }
                ExpireSolution(false);
            });
        }));
    }

    private void SetOutput(IGH_DataAccess dataAccess, string? explicitStatus)
    {
        ViveTrackData data;
        Plane plane;
        double time;
        string status;
        bool hasData;

        lock (_gate)
        {
            data = _latestData;
            plane = _latestPlane;
            time = _latestTime;
            status = explicitStatus ?? _status;
            hasData = _hasData;
        }

        string msg;
        if (hasData)
        {
            msg = data.Summary();
            Message = $"{data.Trackers.Count} Trk, {data.Lighthouses.Count} LH";
        }
        else
        {
            msg = $"Vive TCP Stream: {status}";
            Message = status;
        }

        dataAccess.SetData(0, msg);
        dataAccess.SetData(1, data);
        dataAccess.SetData(2, plane);
        dataAccess.SetData(3, time);
    }

    private void SetStatus(string status)
    {
        lock (_gate)
            _status = status;
    }

    private static bool TryParsePacket(string json, out ViveTrackData trackData, out Plane primaryPlane)
    {
        trackData = new ViveTrackData();
        primaryPlane = Plane.WorldXY;

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            double timeSeconds = 0.0;
            if (GetProperty(root, "time", out var timeEl) && timeEl.TryGetDouble(out var tVal))
                timeSeconds = tVal;

            long seq = 0;
            if (GetProperty(root, "seq", out var seqEl) && seqEl.TryGetInt64(out var sVal))
                seq = sVal;

            trackData.Timestamp = timeSeconds;
            trackData.Sequence = seq;
            trackData.Status = "Streaming";

            // 1. Check for multi-device array: "devices"
            if (GetProperty(root, "devices", out var devicesEl) && devicesEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var devEl in devicesEl.EnumerateArray())
                {
                    if (TryParseDevicePose(devEl, out var pose))
                    {
                        trackData.Devices.Add(pose);
                    }
                }
            }

            // 2. Fallback / Single device support from root properties
            if (trackData.Devices.Count == 0 && GetProperty(root, "matrix", out var matrixEl) && matrixEl.ValueKind == JsonValueKind.Array)
            {
                if (TryParseDevicePose(root, out var rootPose))
                {
                    trackData.Devices.Add(rootPose);
                }
            }

            // Determine primary plane (first tracked tracker, or first tracked device)
            var activeTracker = trackData.Trackers.FirstOrDefault(d => d.Tracked) ?? trackData.Trackers.FirstOrDefault();
            if (activeTracker != null)
            {
                primaryPlane = activeTracker.Plane;
            }
            else if (trackData.Devices.Count > 0)
            {
                primaryPlane = trackData.Devices[0].Plane;
            }

            return trackData.Devices.Count > 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryParseDevicePose(JsonElement element, out ViveDevicePose pose)
    {
        pose = new ViveDevicePose();

        if (GetProperty(element, "id", out var idEl))
            pose.SerialNumber = idEl.GetString() ?? string.Empty;

        if (GetProperty(element, "deviceIndex", out var idxEl) && idxEl.TryGetUInt32(out var uIdx))
            pose.DeviceIndex = uIdx;

        if (GetProperty(element, "deviceClass", out var classEl))
            pose.DeviceClass = classEl.GetString() ?? "Tracker";

        if (GetProperty(element, "connected", out var connEl))
            pose.Connected = connEl.GetBoolean();

        if (GetProperty(element, "tracked", out var trkEl))
            pose.Tracked = trkEl.GetBoolean();

        if (GetProperty(element, "trigger", out var trigEl))
            pose.Trigger = trigEl.GetBoolean();

        if (GetProperty(element, "triggerValue", out var trigValEl) && trigValEl.TryGetDouble(out var tv))
            pose.TriggerValue = tv;
        else if (pose.Trigger)
            pose.TriggerValue = 1.0;

        if (GetProperty(element, "grip", out var gripEl))
            pose.Grip = gripEl.GetBoolean();

        if (GetProperty(element, "menu", out var menuEl))
            pose.Menu = menuEl.GetBoolean();

        if (GetProperty(element, "trackpad", out var padEl))
            pose.Trackpad = padEl.GetBoolean();

        if (GetProperty(element, "buttonMask", out var maskEl) && maskEl.TryGetUInt64(out var mask))
            pose.ButtonMask = mask;

        if (GetProperty(element, "matrix", out var matrixEl) && matrixEl.ValueKind == JsonValueKind.Array)
        {
            var matrix = new double[12];
            int i = 0;
            foreach (var valEl in matrixEl.EnumerateArray())
            {
                if (i < 12 && valEl.TryGetDouble(out var d))
                    matrix[i++] = d;
            }

            if (i == 12)
            {
                pose.MatrixOpenVr = matrix;

                var origin = new Point3d(matrix[3] * 1000.0, -matrix[11] * 1000.0, matrix[7] * 1000.0);

                Plane plane;
                if (string.Equals(pose.DeviceClass, "Lighthouse", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(pose.DeviceClass, "TrackingReference", StringComparison.OrdinalIgnoreCase))
                {
                    // Lighthouse Base Station:
                    //   Column 0 = Right across casing (X)
                    //   -Column 2 = Optical beam emission direction / forward (Y)
                    //   Column 1 = Top of casing (Z)
                    var xAxis = MapOpenVrVector(matrix[0], matrix[4], matrix[8]);
                    var yAxis = -MapOpenVrVector(matrix[2], matrix[6], matrix[10]);
                    plane = new Plane(origin, xAxis, yAxis);
                }
                else
                {
                    // Vive Tracker (matches official HTC CAD drawing with Y and Z switched for Rhino):
                    //   Tracker CAD X -> Rhino X (Right across the 2 rear lobes)
                    //   Tracker CAD Z -> Rhino Y (Forward towards front single lobe)
                    //   Tracker CAD Y -> Rhino Z (UP through top dome)
                    var xAxis = MapOpenVrVector(matrix[0], matrix[4], matrix[8]);
                    var yAxis = MapOpenVrVector(matrix[2], matrix[6], matrix[10]);
                    plane = new Plane(origin, xAxis, yAxis);

                    // User requested rotation around X by 90deg to align with Rhino viewport
                    plane.Rotate(RhinoMath.ToRadians(90.0), plane.XAxis, origin);
                }

                if (plane.IsValid)
                {
                    pose.Origin = origin;
                    pose.Plane = plane;
                    pose.CorrectedMatrix4X4 = Transform.PlaneToPlane(Plane.WorldXY, plane);
                    return true;
                }
            }
        }

        return false;
    }

    private static Vector3d MapOpenVrVector(double x, double y, double z) => new(x, -z, y);

    private static Bitmap LoadIcon()
    {
        var stream = typeof(ViveTcpRecorderComponentV2).Assembly.GetManifestResourceStream("ViveTcpRecorder.Resources.ViveStart.png")
            ?? typeof(ViveTcpRecorderComponentV2).Assembly.GetManifestResourceStream("ViveTcpRecorder.Resources.Listener.png");
        if (stream is null)
            return new Bitmap(24, 24);

        using (stream)
        {
            using var source = new Bitmap(stream);
            return new Bitmap(source, new Size(24, 24));
        }
    }

    private static bool GetProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static bool TryParseEndpoint(string value, out IPAddress address, out int port)
    {
        address = IPAddress.Loopback;
        port = PublisherPort;

        var span = value.AsSpan().Trim();
        var separator = span.LastIndexOf(':');
        if (separator >= 0)
        {
            var hostPart = span[..separator].ToString();
            var portPart = span[(separator + 1)..].ToString();

            if (!IPAddress.TryParse(hostPart, out address!))
                return false;

            return int.TryParse(portPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out port) && port is > 0 and <= 65535;
        }

        return IPAddress.TryParse(span.ToString(), out address!);
    }
}
