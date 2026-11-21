using System.Diagnostics;
using System.Net.Sockets;

namespace Xbox2Ps.Core;

public enum BridgeState { Stopped, Starting, WaitingForController, Running, Error }

/// <summary>
/// The mapping loop: polls the Xbox controller (and, when the profile turns it on, the keyboard
/// and mouse), drives a VIIPER virtual DualSense, and forwards rumble back. Runs on its own thread
/// between <see cref="Start"/> and <see cref="Stop"/>.
/// </summary>
public sealed class Bridge : IDisposable
{
    private const int KbmStepMs = 4;

    private readonly object _gate = new();
    private volatile CompiledProfile _profile = CompiledProfile.Default;
    private volatile bool _running;
    private volatile int _slot = -1;
    private Thread? _worker;

    private volatile int _hotkey = 0x77; // F8
    private volatile bool _autoCapture = true;
    private KbmInput? _kbm;
    private CompiledProfile? _kbmProfile;
    private CompiledProfile? _kbmFailedFor;

    private readonly object _outputGate = new();
    private readonly byte[] _output = new byte[Mapper.PacketSize];
    private bool _hasOutput;

    /// <summary>Raised on the bridge's thread.</summary>
    public event Action<BridgeState, string?>? StateChanged;

    /// <summary>Status lines, raised on the bridge's thread.</summary>
    public event Action<string>? Log;

    /// <summary>Keyboard and mouse capture changed; null when keyboard and mouse aren't in use. Raised on a background thread.</summary>
    public event Action<CaptureState?>? CaptureChanged;

    public BridgeState State { get; private set; } = BridgeState.Stopped;

    /// <summary>The slot being mapped, or -1.</summary>
    public int CurrentSlot => _slot;

    /// <summary>Whether the last virtual pad was the full USB composite DualSense (null until one was made).</summary>
    public bool? IsFullDualSense { get; private set; }

    /// <summary>Keyboard and mouse capture, or null when they aren't in use.</summary>
    public CaptureState? Capture => _kbm?.State;

    public bool IsActive
    {
        get { lock (_gate) return _worker is { IsAlive: true }; }
    }

    /// <summary>Takes effect on the next poll, also while running.</summary>
    public void SetProfile(Profile p) => _profile = CompiledProfile.From(p);

    /// <summary>The capture hotkey (virtual-key code) and whether to capture while PS Remote Play is in front.</summary>
    public void SetCaptureOptions(int hotkey, bool autoCapture)
    {
        _hotkey = hotkey;
        _autoCapture = autoCapture;
        _kbm?.SetOptions(hotkey, autoCapture);
    }

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

    /// <summary>Copies the packet last sent to the virtual DualSense; false when nothing is being sent.</summary>
    public bool TryCopyOutput(byte[] dest)
    {
        lock (_outputGate)
        {
            if (!_hasOutput || State != BridgeState.Running) return false;
            Array.Copy(_output, dest, Mapper.PacketSize);
            return true;
        }
    }

    private void Info(string msg) => Log?.Invoke(msg);

    private void SetState(BridgeState state, string? message)
    {
        State = state;
        StateChanged?.Invoke(state, message);
    }

    /// <summary>Starts or stops the keyboard and mouse reader to match the profile.</summary>
    private void SyncKbm(CompiledProfile profile)
    {
        if (profile.KbmEnabled && _kbm is null)
        {
            if (ReferenceEquals(profile, _kbmFailedFor)) return; // already failed; try again after the next edit
            try { _kbm = new KbmInput(_hotkey, _autoCapture); }
            catch (Exception ex)
            {
                _kbmFailedFor = profile;
                Info($"Keyboard and mouse couldn't start: {ex.Message}");
                return;
            }
            _kbm.SetBindings(profile.KbmCodes);
            _kbm.StateChanged += s =>
            {
                Info(s switch
                {
                    CaptureState.Captured => "Keyboard and mouse captured.",
                    CaptureState.Paused when _autoCapture => "Keyboard and mouse paused.",
                    CaptureState.Paused => "Keyboard and mouse released.",
                    _ => "Keyboard and mouse released; waiting for PS Remote Play.",
                });
                CaptureChanged?.Invoke(s);
            };
            _kbmProfile = profile;
            Info("Keyboard and mouse are on.");
            CaptureChanged?.Invoke(_kbm.State);
        }
        else if (!profile.KbmEnabled && _kbm is not null)
        {
            StopKbm();
            Info("Keyboard and mouse are off.");
        }
        else if (_kbm is not null && !ReferenceEquals(profile, _kbmProfile))
        {
            _kbm.SetBindings(profile.KbmCodes);
            _kbmProfile = profile;
        }
    }

    private void StopKbm()
    {
        if (_kbm is null) return;
        _kbm.Dispose();
        _kbm = null;
        _kbmProfile = null;
        CaptureChanged?.Invoke(null);
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

            // Wait for a controller before plugging in the virtual pad, so games don't see an idle
            // DualSense while nothing is connected. With keyboard and mouse on, there's no need to wait.
            bool waiting = false;
            while (_running && (_slot = XInput.FindController(requestedSlot)) < 0 && !_profile.KbmEnabled)
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
            Info(_slot >= 0 ? $"Mapping Xbox controller {_slot + 1} -> virtual DualSense." : "Mapping keyboard and mouse -> virtual DualSense.");
            SetState(BridgeState.Running, null);

            var feedback = new Thread(() => FeedbackLoop(ns, full)) { IsBackground = true, Name = "xbox2ps rumble" };
            feedback.Start();

            var controllerPacket = new byte[Mapper.PacketSize];
            var packet = new byte[Mapper.PacketSize];
            var lastSent = new byte[Mapper.PacketSize];
            Mapper.Neutral(controllerPacket);
            Mapper.Neutral(packet);
            uint lastPacket = uint.MaxValue;
            CompiledProfile? lastProfile = null;
            var frame = new KbmFrame();
            var mouseState = new MouseStickState();
            (double X, double Y) mouse = (0, 0);
            var clock = Stopwatch.StartNew();
            double lastKbmStep = 0;
            long lastSend = -1000, nextScan = 0;

            while (_running)
            {
                var profile = _profile;
                SyncKbm(profile);
                long now = clock.ElapsedMilliseconds;
                bool changed = !ReferenceEquals(profile, lastProfile);

                int slot = _slot;
                if (slot >= 0 && XInput.TryGetState((uint)slot, out var state))
                {
                    if (state.dwPacketNumber != lastPacket || changed)
                    {
                        lastPacket = state.dwPacketNumber;
                        Mapper.Build(controllerPacket, state.Gamepad, profile);
                        changed = true;
                    }
                }
                else
                {
                    if (slot >= 0)
                    {
                        // Controller dropped: release everything, keep the virtual pad plugged in.
                        Info("Xbox controller disconnected. Waiting for it to come back...");
                        if (!profile.KbmEnabled) SetState(BridgeState.WaitingForController, "Xbox controller disconnected.");
                        Mapper.Neutral(controllerPacket);
                        changed = true;
                        _slot = slot = -1;
                        lastPacket = uint.MaxValue;
                    }
                    if (now >= nextScan)
                    {
                        // Scanning empty slots is slow, so not on every poll.
                        nextScan = now + 250;
                        _slot = slot = XInput.FindController(requestedSlot);
                        if (slot >= 0)
                        {
                            Info($"Xbox controller {slot + 1} connected.");
                            SetState(BridgeState.Running, null);
                        }
                    }
                }
                lastProfile = profile;

                // Keyboard and mouse at a steady pace, so mouse speed is measured over even steps.
                var kbm = _kbm;
                bool step = false;
                double elapsed = clock.Elapsed.TotalMilliseconds;
                if (kbm is not null && elapsed - lastKbmStep >= KbmStepMs)
                {
                    kbm.Read(frame);
                    if (frame.Captured && profile.Kbm.Mouse != MouseTarget.Off)
                        mouse = MouseStick.Step(ref mouseState, frame.MouseDx, frame.MouseDy, elapsed - lastKbmStep, profile.Kbm);
                    else
                    {
                        mouseState = default;
                        mouse = (0, 0);
                    }
                    lastKbmStep = elapsed;
                    step = true;
                }

                if (changed || step)
                {
                    Array.Copy(controllerPacket, packet, Mapper.PacketSize);
                    if (kbm is not null) Mapper.ApplyKbm(packet, frame, profile, mouse);
                    changed = !packet.AsSpan().SequenceEqual(lastSent);
                }

                // Send on change, and at least every 50 ms so the stream stays alive.
                if (changed || now - lastSend >= 50)
                {
                    try { ns.Write(packet); }
                    catch (IOException) { throw new ViiperException("Lost the connection to the VIIPER server."); }
                    packet.CopyTo(lastSent, 0);
                    lastSend = now;
                    lock (_outputGate)
                    {
                        packet.CopyTo(_output, 0);
                        _hasOutput = true;
                    }
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
            lock (_outputGate) _hasOutput = false;
            StopKbm();
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
