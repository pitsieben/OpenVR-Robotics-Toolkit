using System.IO.Pipes;
using ViveClay.Protocol;

namespace ClayViewer;

/// <summary>
/// Named-pipe server on a background thread. Accepts one client at a time; when the client goes away
/// (Grasshopper closed, solution restarted, pipe broken) it simply waits for the next one.
/// Incoming messages go straight to the engine; replies are written under a lock.
/// </summary>
public sealed class PipeServer : IDisposable
{
    readonly ClayEngine _engine;
    readonly string _name;
    readonly object _writeLock = new();
    readonly Thread _thread;
    NamedPipeServerStream? _first;
    volatile NamedPipeServerStream? _current;
    volatile bool _stop;

    public PipeServer(ClayEngine engine, string name = ClayWire.PipeName)
    {
        _engine = engine; _name = name;
        _thread = new Thread(Run) { IsBackground = true, Name = "ClayPipeServer" };
    }

    NamedPipeServerStream Create() =>
        // Asynchronous (overlapped) so a pending Read never blocks our Write on the same pipe.
        new(_name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 1 << 20, 1 << 20);

    /// <summary>Creates the pipe immediately (throws IOException if another viewer already owns it) then serves in the background.</summary>
    public void Start()
    {
        _first = Create();
        _thread.Start();
    }

    public void Send(ClayMessage m)
    {
        var s = _current;
        if (s == null) return;
        lock (_writeLock)
        {
            try { ClayWire.Write(s, m); } catch { /* client vanished; the read loop will notice */ }
        }
    }

    void Run()
    {
        while (!_stop)
        {
            NamedPipeServerStream s;
            try { s = _first ?? Create(); _first = null; }
            catch (Exception ex) { Console.Error.WriteLine("[pipe] create failed: " + ex.Message); Thread.Sleep(1000); continue; }

            try
            {
                s.WaitForConnection();
                _current = s;
                _engine.Reply = Send;
                _engine.SetConnection("connected");
                Console.WriteLine("[pipe] client connected");
                Send(new HelloMsg { Name = "ClayViewer" });

                while (!_stop)
                {
                    var m = ClayWire.Read(s);
                    if (m == null || m is CloseMsg) break;
                    if (m is HelloMsg h && (h.Version < ClayWire.MinCompatibleVersion || h.Version > ClayWire.Version))
                        Send(new StatusMsg { Text = $"Protocol version mismatch: viewer {ClayWire.Version}, client {h.Version}." });
                    _engine.Submit(m);
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or EndOfStreamException or InvalidDataException)
            {
                Console.WriteLine("[pipe] connection ended: " + ex.Message);
            }
            catch (Exception ex) { Console.Error.WriteLine("[pipe] " + ex); }
            finally
            {
                _current = null;
                _engine.Reply = null;
                _engine.SetConnection("waiting for Grasshopper");
                try { s.Dispose(); } catch { }
                Console.WriteLine("[pipe] client disconnected");
            }
        }
    }

    public void Dispose()
    {
        _stop = true;
        Send(new CloseMsg());
        try { _current?.Dispose(); } catch { }
    }
}
