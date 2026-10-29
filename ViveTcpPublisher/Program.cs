using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Valve.VR;

namespace ViveTcpPublisher;

internal static class Program
{
    private const int DefaultPort = 7000;
    private const int DefaultRateHz = 90;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static async Task<int> Main(string[] args)
    {
        var options = Options.Parse(args);
        if (options.ShowHelp)
        {
            Options.PrintHelp();
            return 0;
        }

        EVRInitError initError = EVRInitError.None;
        CVRSystem? system = null;
        try
        {
            system = OpenVR.Init(ref initError, EVRApplicationType.VRApplication_Background);
            if (initError != EVRInitError.None || system is null)
            {
                Console.Error.WriteLine($"Unable to connect to SteamVR: {initError}. Start SteamVR, verify the tracker is paired, then retry.");
                return 2;
            }

            var devices = EnumerateDevices(system);
            if (options.ListDevices)
            {
                PrintDevices(devices);
                return devices.Count == 0 ? 3 : 0;
            }

            PrintDevices(devices);

            DeviceInfo? primaryTracker = SelectTracker(devices, options.Serial);
            if (primaryTracker is not null)
            {
                Console.WriteLine($"Primary Tracker: {primaryTracker.Value.Serial} (device {primaryTracker.Value.Index})");
            }
            else if (!string.IsNullOrWhiteSpace(options.Serial))
            {
                Console.Error.WriteLine($"Tracker with serial '{options.Serial}' not found.");
                return 3;
            }
            else
            {
                Console.WriteLine("Streaming all detected devices (no primary tracker selected).");
            }

            Console.WriteLine($"TCP: {options.Host}:{options.Port} at {options.RateHz} Hz");
            Console.WriteLine("Coordinate system in packets: OpenVR, metres, right-handed (+X right, +Y up, +Z backward). Press Ctrl+C to stop.");

            using var cancel = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Cancel(); };

            await using var server = new PoseTcpServer(options.Host, options.Port);
            await server.StartAsync(cancel.Token);
            await PublishPosesAsync(system, primaryTracker, options.RateHz, server, cancel.Token);
            return 0;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (SocketException ex)
        {
            Console.Error.WriteLine($"TCP server error: {ex.Message}");
            return 4;
        }
        catch (DllNotFoundException ex)
        {
            Console.Error.WriteLine($"OpenVR runtime DLL could not be loaded: {ex.Message}");
            Console.Error.WriteLine("Re-download the complete ViveTcpPublisher distribution and confirm SteamVR is installed.");
            return 5;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Unexpected error: {ex.Message}");
            return 1;
        }
        finally
        {
            if (system is not null)
                OpenVR.Shutdown();
        }
    }

    private static async Task PublishPosesAsync(CVRSystem system, DeviceInfo? primaryTracker, int rateHz, PoseTcpServer server, CancellationToken cancellationToken)
    {
        var poses = new TrackedDevicePose_t[OpenVR.k_unMaxTrackedDeviceCount];
        var started = Stopwatch.StartNew();
        var interval = TimeSpan.FromSeconds(1.0 / rateHz);
        using var ticker = new PeriodicTimer(interval);
        long sequence = 0;

        do
        {
            system.GetDeviceToAbsoluteTrackingPose(ETrackingUniverseOrigin.TrackingUniverseStanding, 0f, poses);

            var devicePackets = new List<DevicePosePacket>();
            DevicePosePacket? rootDev = null;

            for (uint i = 0; i < OpenVR.k_unMaxTrackedDeviceCount; i++)
            {
                var pose = poses[i];
                if (!pose.bDeviceIsConnected)
                    continue;

                var devClass = system.GetTrackedDeviceClass(i);
                if (devClass == ETrackedDeviceClass.Invalid)
                    continue;

                string className = devClass switch
                {
                    ETrackedDeviceClass.GenericTracker => "Tracker",
                    ETrackedDeviceClass.TrackingReference => "Lighthouse",
                    ETrackedDeviceClass.Controller => "Controller",
                    ETrackedDeviceClass.HMD => "HMD",
                    _ => devClass.ToString()
                };

                var serial = GetStringProperty(system, i, ETrackedDeviceProperty.Prop_SerialNumber_String) ?? $"device-{i}";
                var model = GetStringProperty(system, i, ETrackedDeviceProperty.Prop_ModelNumber_String) ?? string.Empty;

                var matrix = pose.mDeviceToAbsoluteTracking;
                var matrixArr = new[] {
                    matrix.m0, matrix.m1, matrix.m2, matrix.m3,
                    matrix.m4, matrix.m5, matrix.m6, matrix.m7,
                    matrix.m8, matrix.m9, matrix.m10, matrix.m11
                };
                var posArr = new[] { matrix.m3, matrix.m7, matrix.m11 };
                var rotArr = QuaternionFromMatrix(matrix);

                bool trigger = false;
                float triggerVal = 0f;
                bool grip = false;
                bool menu = false;
                bool trackpad = false;
                ulong buttonMask = 0;

                if (devClass == ETrackedDeviceClass.GenericTracker || devClass == ETrackedDeviceClass.Controller)
                {
                    var ctrlState = new VRControllerState_t();
                    if (system.GetControllerState(i, ref ctrlState, (uint)System.Runtime.InteropServices.Marshal.SizeOf<VRControllerState_t>()))
                    {
                        buttonMask = ctrlState.ulButtonPressed;
                        triggerVal = ctrlState.rAxis1.x;
                        trigger = (buttonMask & (1UL << (int)EVRButtonId.k_EButton_SteamVR_Trigger)) != 0 || triggerVal > 0.1f;
                        grip = (buttonMask & (1UL << (int)EVRButtonId.k_EButton_Grip)) != 0;
                        menu = (buttonMask & (1UL << (int)EVRButtonId.k_EButton_ApplicationMenu)) != 0;
                        trackpad = (buttonMask & (1UL << (int)EVRButtonId.k_EButton_SteamVR_Touchpad)) != 0;
                    }
                }

                var devPacket = new DevicePosePacket(
                    DeviceIndex: i,
                    Id: serial,
                    DeviceClass: className,
                    Model: model,
                    Connected: pose.bDeviceIsConnected,
                    Tracked: pose.bDeviceIsConnected && pose.bPoseIsValid,
                    TrackingResult: pose.eTrackingResult.ToString(),
                    Position: posArr,
                    Rotation: rotArr,
                    Matrix: matrixArr,
                    Trigger: trigger,
                    TriggerValue: triggerVal,
                    Grip: grip,
                    Menu: menu,
                    Trackpad: trackpad,
                    ButtonMask: buttonMask);

                devicePackets.Add(devPacket);

                if (primaryTracker is not null && i == primaryTracker.Value.Index)
                {
                    rootDev = devPacket;
                }
            }

            // Fallback for root fields if no primary tracker explicitly matched
            rootDev ??= devicePackets.FirstOrDefault(d => d.DeviceClass == "Tracker")
                     ?? devicePackets.FirstOrDefault();

            var packet = new MultiDevicePosePacket(
                V: 2,
                Seq: sequence++,
                Time: started.Elapsed.TotalSeconds,
                UtcUnixMilliseconds: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Id: rootDev?.Id ?? "UNKNOWN",
                Connected: rootDev?.Connected ?? false,
                Tracked: rootDev?.Tracked ?? false,
                TrackingResult: rootDev?.TrackingResult ?? "Uninitialized",
                Position: rootDev?.Position ?? new float[] { 0, 0, 0 },
                Rotation: rootDev?.Rotation ?? new float[] { 0, 0, 0, 1 },
                Matrix: rootDev?.Matrix ?? new float[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0 },
                Trigger: rootDev?.Trigger ?? false,
                TriggerValue: rootDev?.TriggerValue ?? 0f,
                Devices: devicePackets);

            await server.BroadcastAsync(JsonSerializer.SerializeToUtf8Bytes(packet, JsonOptions), cancellationToken);
        }
        while (await ticker.WaitForNextTickAsync(cancellationToken));
    }

    private static List<DeviceInfo> EnumerateDevices(CVRSystem system)
    {
        var result = new List<DeviceInfo>();
        for (uint index = 0; index < OpenVR.k_unMaxTrackedDeviceCount; index++)
        {
            if (!system.IsTrackedDeviceConnected(index))
                continue;

            var devClass = system.GetTrackedDeviceClass(index);
            if (devClass == ETrackedDeviceClass.Invalid)
                continue;

            string className = devClass switch
            {
                ETrackedDeviceClass.GenericTracker => "Tracker",
                ETrackedDeviceClass.TrackingReference => "Lighthouse",
                ETrackedDeviceClass.Controller => "Controller",
                ETrackedDeviceClass.HMD => "HMD",
                _ => devClass.ToString()
            };

            var serial = GetStringProperty(system, index, ETrackedDeviceProperty.Prop_SerialNumber_String) ?? $"device-{index}";
            var model = GetStringProperty(system, index, ETrackedDeviceProperty.Prop_ModelNumber_String) ?? string.Empty;

            result.Add(new DeviceInfo(index, serial, model, className));
        }
        return result;
    }

    private static string? GetStringProperty(CVRSystem system, uint deviceIndex, ETrackedDeviceProperty property)
    {
        var error = ETrackedPropertyError.TrackedProp_Success;
        var required = system.GetStringTrackedDeviceProperty(deviceIndex, property, null!, 0, ref error);
        if (error != ETrackedPropertyError.TrackedProp_Success || required == 0)
            return null;

        var buffer = new StringBuilder((int)required);
        system.GetStringTrackedDeviceProperty(deviceIndex, property, buffer, required, ref error);
        return error == ETrackedPropertyError.TrackedProp_Success ? buffer.ToString() : null;
    }

    private static DeviceInfo? SelectTracker(IReadOnlyList<DeviceInfo> devices, string? serial)
    {
        var trackers = devices.Where(d => d.Class == "Tracker").ToList();
        if (string.IsNullOrWhiteSpace(serial))
            return trackers.Count > 0 ? trackers[0] : null;

        return trackers.FirstOrDefault(t => string.Equals(t.Serial, serial, StringComparison.OrdinalIgnoreCase));
    }

    private static void PrintDevices(IReadOnlyCollection<DeviceInfo> devices)
    {
        if (devices.Count == 0)
        {
            Console.WriteLine("No SteamVR devices are currently connected.");
            return;
        }

        Console.WriteLine($"Connected SteamVR devices ({devices.Count}):");
        foreach (var dev in devices)
            Console.WriteLine($"  [{dev.Class}] {dev.Serial} (device {dev.Index}, {dev.Model})");
    }

    private static float[] QuaternionFromMatrix(HmdMatrix34_t matrix)
    {
        var m00 = matrix.m0; var m01 = matrix.m1; var m02 = matrix.m2;
        var m10 = matrix.m4; var m11 = matrix.m5; var m12 = matrix.m6;
        var m20 = matrix.m8; var m21 = matrix.m9; var m22 = matrix.m10;
        float x, y, z, w;
        var trace = m00 + m11 + m22;
        if (trace > 0)
        {
            var s = MathF.Sqrt(trace + 1f) * 2f;
            w = 0.25f * s; x = (m21 - m12) / s; y = (m02 - m20) / s; z = (m10 - m01) / s;
        }
        else if (m00 > m11 && m00 > m22)
        {
            var s = MathF.Sqrt(1f + m00 - m11 - m22) * 2f;
            w = (m21 - m12) / s; x = 0.25f * s; y = (m01 + m10) / s; z = (m02 + m20) / s;
        }
        else if (m11 > m22)
        {
            var s = MathF.Sqrt(1f + m11 - m00 - m22) * 2f;
            w = (m02 - m20) / s; x = (m01 + m10) / s; y = 0.25f * s; z = (m12 + m21) / s;
        }
        else
        {
            var s = MathF.Sqrt(1f + m22 - m00 - m11) * 2f;
            w = (m10 - m01) / s; x = (m02 + m20) / s; y = (m12 + m21) / s; z = 0.25f * s;
        }
        return new[] { x, y, z, w };
    }

    private readonly record struct DeviceInfo(uint Index, string Serial, string Model, string Class);

    private sealed record DevicePosePacket(
        uint DeviceIndex,
        string Id,
        string DeviceClass,
        string Model,
        bool Connected,
        bool Tracked,
        string TrackingResult,
        float[] Position,
        float[] Rotation,
        float[] Matrix,
        bool Trigger = false,
        float TriggerValue = 0f,
        bool Grip = false,
        bool Menu = false,
        bool Trackpad = false,
        ulong ButtonMask = 0);

    private sealed record MultiDevicePosePacket(
        int V,
        long Seq,
        double Time,
        long UtcUnixMilliseconds,
        string Id,
        bool Connected,
        bool Tracked,
        string TrackingResult,
        float[] Position,
        float[] Rotation,
        float[] Matrix,
        bool Trigger,
        float TriggerValue,
        List<DevicePosePacket> Devices);

    private sealed record Options(IPAddress Host, int Port, int RateHz, string? Serial, bool ListDevices, bool ShowHelp)
    {
        public static Options Parse(string[] args)
        {
            var host = IPAddress.Loopback;
            var port = DefaultPort;
            var rate = DefaultRateHz;
            string? serial = null;
            var list = false;
            var help = false;
            for (var i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--host" when i + 1 < args.Length && IPAddress.TryParse(args[++i], out var parsedHost): host = parsedHost; break;
                    case "--port" when i + 1 < args.Length && int.TryParse(args[++i], NumberStyles.None, CultureInfo.InvariantCulture, out var parsedPort) && parsedPort is > 0 and <= 65535: port = parsedPort; break;
                    case "--rate" when i + 1 < args.Length && int.TryParse(args[++i], NumberStyles.None, CultureInfo.InvariantCulture, out var parsedRate) && parsedRate is > 0 and <= 500: rate = parsedRate; break;
                    case "--serial" when i + 1 < args.Length: serial = args[++i]; break;
                    case "--list": list = true; break;
                    case "--help" or "-h" or "/?": help = true; break;
                    default: throw new ArgumentException($"Unknown or incomplete option: {args[i]}. Use --help for usage.");
                }
            }
            return new Options(host, port, rate, serial, list, help);
        }

        public static void PrintHelp() => Console.WriteLine("""
ViveTcpPublisher - OpenVR multi-device tracker & lighthouse pose publisher

Usage:
  ViveTcpPublisher.exe --list
  ViveTcpPublisher.exe [--serial SERIAL] [--host 127.0.0.1] [--port 7000] [--rate 90]

Streams all connected SteamVR devices (trackers, lighthouses, controllers, HMD) over TCP.
Each TCP message is one UTF-8 JSON object terminated by a newline.
""");
    }
}

internal sealed class PoseTcpServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly object _gate = new();
    private readonly List<TcpClient> _clients = new();
    private Task? _acceptLoop;

    public PoseTcpServer(IPAddress host, int port) => _listener = new TcpListener(host, port);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _listener.Start();
        _acceptLoop = AcceptLoopAsync(cancellationToken);
        return Task.CompletedTask;
    }

    public async Task BroadcastAsync(byte[] json, CancellationToken cancellationToken)
    {
        var message = new byte[json.Length + 1];
        Buffer.BlockCopy(json, 0, message, 0, json.Length);
        message[^1] = (byte)'\n';
        TcpClient[] clients;
        lock (_gate) clients = _clients.ToArray();
        foreach (var client in clients)
        {
            try
            {
                await client.GetStream().WriteAsync(message, cancellationToken);
            }
            catch (IOException)
            {
                Remove(client);
            }
            catch (ObjectDisposedException)
            {
                Remove(client);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _listener.Stop();
        lock (_gate)
        {
            foreach (var client in _clients) client.Dispose();
            _clients.Clear();
        }
        if (_acceptLoop is not null)
        {
            try { await _acceptLoop; }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            catch (SocketException) { }
        }
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var client = await _listener.AcceptTcpClientAsync(cancellationToken);
            client.NoDelay = true;
            lock (_gate) _clients.Add(client);
            Console.WriteLine($"Grasshopper receiver connected: {client.Client.RemoteEndPoint}");
        }
    }

    private void Remove(TcpClient client)
    {
        lock (_gate) _clients.Remove(client);
        client.Dispose();
    }
}
