using System.Diagnostics;
using System.Net.Sockets;

namespace Xbox2Ps.Core;

public enum BridgeState { Stopped, Starting, WaitingForController, Running, Error }

/// <summary>
/// The mapping loop: polls the Xbox controller, drives a VIIPER virtual DualSense, and forwards
/// rumble back. Runs on its own thread between <see cref="Start"/> and <see cref="Stop"/>.
/// </summary>
public sealed class Bridge : IDisposable
{
    private readonly object _gate = new();
    private volatile CompiledProfile _profile = CompiledProfile.Default;
    private volatile bool _running;
    private volatile int _slot = -1;
    private Thread? _worker;

    /// <summary>Raised on the bridge's thread.</summary>
    public event Action<BridgeState, string?>? StateChanged;

    /// <summary>Status lines, raised on the bridge's thread.</summary>
    public event Action<string>? Log;

    public BridgeState State { get; private set; } = BridgeState.Stopped;

    /// <summary>The slot being mapped, or -1.</summary>
    public int CurrentSlot => _slot;

    /// <summary>Whether the last virtual pad was the full USB composite DualSense (null until one was made).</summary>
    public bool? IsFullDualSense { get; private set; }

    public bool IsActive
    {
        get { lock (_gate) return _worker is { IsAlive: true }; }
    }

    /// <summary>Takes effect on the next poll, also while running.</summary>
    public void SetProfile(Profile p) => _profile = CompiledProfile.From(p);

    /// <param name="requestedSlot">0-3, or null for the first connected controller.</param>
    public void Start(int? requestedSlot)
    {
        lock (_gate)
        {
            if (_worker is { IsAlive: true }) return;
            _running = true;
            _worker = new Thread(() => Work(requestedSlot)) { IsBackground = true, Name = "xbox2ps bridge" };
            _worker.Start();
        }
    }

    /// <summary>Stops the loop and unplugs the virtual pad. Blocks for up to a few seconds.</summary>
    public void Stop()
    {
        Thread? w;
        lock (_gate) { _running = false; w = _worker; }
        w?.Join(5000);
    }

    public void Dispose() => Stop();

    private void Info(string msg) => Log?.Invoke(msg);

    private void SetState(BridgeState state, string? message)
    {
        State = state;
        StateChanged?.Invoke(state, message);
    }

    private void Work(int? requestedSlot)
    {
        int busId = -1;
        TcpClient? stream = null;
        bool failed = false;
        XInput.timeBeginPeriod(1);
        try
        {
            SetState(BridgeState.Starting, "Connecting to VIIPER...");
            if (!Viiper.EnsureServer(Info))
                throw new ViiperException(Viiper.IsInstalled
                    ? $"Can't reach the VIIPER server on port {Viiper.ApiPort}."
                    : "VIIPER isn't installed.");

            busId = Viiper.CreateBus();

            // Wait for a controller before plugging in the virtual pad, so games don't
            // see an idle DualSense while nothing is connected.
            bool waiting = false;
            while (_running && (_slot = XInput.FindController(requestedSlot)) < 0)
            {
                if (!waiting) SetState(BridgeState.WaitingForController, "Waiting for an Xbox controller...");
                waiting = true;
                Thread.Sleep(250);
            }
            if (!_running) return;

            // Prefer the "full" DualSense (audio + HID composite, HID on interface 3, exactly
            // like real hardware). Older VIIPER builds only have the HID-only "dualsense",
            // which PS Remote Play doesn't detect.
            bool full = true;
            string devId;
            try { devId = Viiper.AddDevice(busId, "dualsense_full"); }
            catch (Exception)
            {
                full = false;
                devId = Viiper.AddDevice(busId, "dualsense");
            }
            IsFullDualSense = full;

            stream = Viiper.OpenDeviceStream(busId, devId);
            var ns = stream.GetStream();
            Info($"Virtual DualSense{(full ? " (full USB composite)" : "")} created (USB/IP busid {busId}-{devId}).");
            if (!full)
                Info("This VIIPER version only offers the basic DualSense, which PS Remote Play doesn't detect. Update VIIPER.");
            Info($"Mapping Xbox controller {_slot + 1} -> virtual DualSense.");
            SetState(BridgeState.Running, null);

            var feedback = new Thread(() => FeedbackLoop(ns, full)) { IsBackground = true, Name = "xbox2ps rumble" };
            feedback.Start();

            var packet = new byte[Mapper.PacketSize];
            uint lastPacket = uint.MaxValue;
            CompiledProfile lastProfile = _profile;
            long lastSend = 0;
            var clock = Stopwatch.StartNew();

            while (_running)
            {
                int slot = _slot;
                var profile = _profile;
                bool changed;
                if (slot >= 0 && XInput.TryGetState((uint)slot, out var state))
                {
                    // A profile edit changes the output even when the controller didn't.
                    changed = state.dwPacketNumber != lastPacket || !ReferenceEquals(profile, lastProfile);
                    if (changed)
                    {
                        lastPacket = state.dwPacketNumber;
                        lastProfile = profile;
                        Mapper.Build(packet, state.Gamepad, profile);
                    }
                }
                else
                {
                    // Controller dropped: release everything, keep the virtual pad plugged in.
                    if (slot >= 0)
                    {
                        Info("Xbox controller disconnected. Waiting for it to come back...");
                        SetState(BridgeState.WaitingForController, "Xbox controller disconnected.");
                    }
                    Mapper.Neutral(packet);
                    changed = slot >= 0;
                    _slot = slot = XInput.FindController(requestedSlot);
                    lastPacket = uint.MaxValue;
                    if (slot >= 0)
                    {
                        Info($"Xbox controller {slot + 1} is back.");
                        SetState(BridgeState.Running, null);
                    }
                    else Thread.Sleep(100);
                }

                // Send on change, and at least every 50 ms so the stream stays alive.
                if (changed || clock.ElapsedMilliseconds - lastSend >= 50)
                {
                    try { ns.Write(packet); }
                    catch (IOException) { throw new ViiperException("Lost the connection to the VIIPER server."); }
                    lastSend = clock.ElapsedMilliseconds;
                }
                Thread.Sleep(1);
            }
        }
        catch (Exception ex)
        {
            failed = true;
            Info(ex.Message);
            SetState(BridgeState.Error, ex.Message);
        }
        finally
        {
            XInput.timeEndPeriod(1);
            stream?.Dispose();
            if (busId >= 0) { try { Viiper.RemoveBus(busId); } catch { } }
            XInput.StopVibration(_slot);
            _slot = -1;
            _running = false;
            if (!failed)
            {
                Info("Stopped. The virtual DualSense is unplugged.");
                SetState(BridgeState.Stopped, null);
            }
        }
    }

    // Feedback packets from VIIPER:
    //   "dualsense" (v0.9.0): 6 bytes  [0]=small motor [1]=large motor [2..5]=LEDs
    //   "dualsense_full":    37 bytes  [0..2]=update flags [3]=small motor [4]=large motor ...
    private void FeedbackLoop(NetworkStream ns, bool full)
    {
        var buf = new byte[full ? 37 : 6];
        try
        {
            while (_running)
            {
                ns.ReadExactly(buf);
                int s = _slot;
                if (s < 0) continue;
                byte small, large;
                if (full)
                {
                    // Only touch the motors when the game actually updated rumble.
                    bool rumble = (buf[0] & 0x03) != 0 || (buf[2] & 0x04) != 0;
                    if (!rumble) continue;
                    small = buf[3]; large = buf[4];
                }
                else { small = buf[0]; large = buf[1]; }
                var (left, right) = Mapper.Motors(small, large, _profile);
                XInput.SetVibration(s, left, right);
            }
        }
        catch { /* stream closed */ }
    }
}
