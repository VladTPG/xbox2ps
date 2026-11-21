// xbox2ps: reads an Xbox controller through XInput and drives a virtual
// DualSense (PS5 controller) through VIIPER (https://github.com/Alia5/VIIPER),
// which emulates a real USB DualSense via the usbip-win2 driver.
//
// Mapping
//   A/B/X/Y        -> Cross/Circle/Square/Triangle
//   LB/RB          -> L1/R1
//   LT/RT          -> L2/R2 (analog + digital)
//   LS/RS click    -> L3/R3
//   View           -> Create
//   Menu           -> Options
//   Guide (Xbox)   -> PS
//   View + Menu    -> Touchpad click
//   D-pad, sticks  -> D-pad, sticks
//   Rumble from the game is forwarded back to the Xbox controller.

using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

internal static class Program
{
    private const string Host = "127.0.0.1";
    private const int ApiPort = 3242;

    private static volatile bool _running = true;
    private static volatile int _slot = -1;

    private static int Main(string[] args)
    {
        Console.Title = "xbox2ps";
        Console.WriteLine("xbox2ps - Xbox controller -> virtual DualSense (via VIIPER)");
        Console.WriteLine("Press Ctrl+C to quit.\n");

        int? requestedSlot = null;
        if (args.Length > 0)
        {
            if (int.TryParse(args[0], out var s) && s is >= 0 and <= 3) requestedSlot = s;
            else
            {
                Console.WriteLine("Usage: xbox2ps [slot 0-3]");
                return 2;
            }
        }

        if (!EnsureServer())
        {
            Error("Can't reach the VIIPER server on port 3242.");
            Console.WriteLine("Install VIIPER from https://github.com/Alia5/VIIPER/releases (run: viiper.exe install),");
            Console.WriteLine("then start it with: viiper.exe server");
            Pause();
            return 1;
        }

        Console.CancelKeyPress += (_, e) => { e.Cancel = true; _running = false; };

        int busId = -1;
        Native.timeBeginPeriod(1);
        try
        {
            busId = Api.CreateBus();
            Run(busId, requestedSlot);
        }
        catch (Exception ex)
        {
            Error(ex.Message);
            Pause();
            return 1;
        }
        finally
        {
            Native.timeEndPeriod(1);
            if (busId >= 0) { try { Api.Request($"bus/remove {busId}"); } catch { } }
            int s = _slot;
            if (s >= 0) { var stop = new Native.XINPUT_VIBRATION(); Native.XInputSetState((uint)s, ref stop); }
        }
        Console.WriteLine("Bye.");
        return 0;
    }

    private static void Run(int busId, int? requestedSlot)
    {
        // Wait for a controller before plugging in the virtual pad, so games don't
        // see an idle DualSense while nothing is connected.
        bool waiting = false;
        while (_running && (_slot = FindController(requestedSlot)) < 0)
        {
            if (!waiting) Status("Waiting for an Xbox controller...");
            waiting = true;
            Thread.Sleep(500);
        }
        if (!_running) return;

        // Prefer the "full" DualSense (audio + HID composite, HID on interface 3, exactly
        // like real hardware). Older VIIPER builds only have the HID-only "dualsense".
        bool full = true;
        string devId;
        try { devId = Api.AddDevice(busId, "dualsense_full"); }
        catch (Exception)
        {
            full = false;
            devId = Api.AddDevice(busId, "dualsense");
        }
        using var stream = new TcpClient { NoDelay = true };
        stream.Connect(Host, ApiPort);
        var ns = stream.GetStream();
        ns.Write(Encoding.ASCII.GetBytes($"bus/{busId}/{devId}\0"));
        Status($"Virtual DualSense{(full ? " (full USB composite)" : "")} created (USB/IP busid {busId}-{devId}).");
        Status($"Mapping Xbox controller in slot {_slot} -> virtual DualSense.");

        var feedback = new Thread(() => FeedbackLoop(ns, full)) { IsBackground = true };
        feedback.Start();

        var packet = new byte[33];
        uint lastPacket = uint.MaxValue;
        long lastSend = 0;
        var clock = Stopwatch.StartNew();

        while (_running)
        {
            int slot = _slot;
            bool changed;
            if (slot >= 0 && Native.TryGetState((uint)slot, out var state))
            {
                changed = state.dwPacketNumber != lastPacket;
                if (changed) { lastPacket = state.dwPacketNumber; Build(packet, state.Gamepad); }
            }
            else
            {
                // Controller dropped: release everything, keep the virtual pad plugged in.
                if (slot >= 0) Status("Xbox controller disconnected. Waiting for it to come back...");
                Neutral(packet);
                changed = slot >= 0;
                _slot = slot = FindController(requestedSlot);
                lastPacket = uint.MaxValue;
                if (slot >= 0) Status($"Xbox controller back in slot {slot}.");
                else Thread.Sleep(100);
            }

            // Send on change, and at least every 50 ms so the stream stays alive.
            if (changed || clock.ElapsedMilliseconds - lastSend >= 50)
            {
                try { ns.Write(packet); }
                catch (IOException)
                {
                    throw new Exception("Lost the connection to the VIIPER server.");
                }
                lastSend = clock.ElapsedMilliseconds;
            }
            Thread.Sleep(1);
        }
    }

    // Feedback packets from VIIPER:
    //   "dualsense" (v0.9.0): 6 bytes  [0]=small motor [1]=large motor [2..5]=LEDs
    //   "dualsense_full":    37 bytes  [0..2]=update flags [3]=small motor [4]=large motor ...
    private static void FeedbackLoop(NetworkStream ns, bool full)
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
                // Small = high-frequency (right) motor, large = low-frequency (left) motor.
                var vib = new Native.XINPUT_VIBRATION
                {
                    wLeftMotorSpeed = (ushort)(large * 257),
                    wRightMotorSpeed = (ushort)(small * 257),
                };
                Native.XInputSetState((uint)s, ref vib);
            }
        }
        catch { /* stream closed */ }
    }

    // 33-byte VIIPER DualSense input packet (little-endian):
    // 0 LX,1 LY,2 RX,3 RY (int8, -128 = left/up) | 4..7 buttons | 8 dpad bits |
    // 9 L2 | 10 R2 | 11..20 touch | 21..32 gyro/accel
    private static void Build(byte[] p, Native.XINPUT_GAMEPAD g)
    {
        bool Has(ushort mask) => (g.wButtons & mask) != 0;
        bool view = Has(Native.BACK), menu = Has(Native.START);
        bool touchpad = view && menu;

        uint b = 0;
        if (Has(Native.X)) b |= 0x0010;            // Square
        if (Has(Native.A)) b |= 0x0020;            // Cross
        if (Has(Native.B)) b |= 0x0040;            // Circle
        if (Has(Native.Y)) b |= 0x0080;            // Triangle
        if (Has(Native.LB)) b |= 0x0100;           // L1
        if (Has(Native.RB)) b |= 0x0200;           // R1
        if (g.bLeftTrigger > 30) b |= 0x0400;      // L2 digital
        if (g.bRightTrigger > 30) b |= 0x0800;     // R2 digital
        if (view && !touchpad) b |= 0x1000;        // Create
        if (menu && !touchpad) b |= 0x2000;        // Options
        if (Has(Native.LS)) b |= 0x4000;           // L3
        if (Has(Native.RS)) b |= 0x8000;           // R3
        if (Has(Native.GUIDE)) b |= 0x00010000;    // PS
        if (touchpad) b |= 0x00020000;             // Touchpad click

        byte dpad = 0;
        if (Has(Native.DPAD_UP)) dpad |= 0x01;
        if (Has(Native.DPAD_DOWN)) dpad |= 0x02;
        if (Has(Native.DPAD_LEFT)) dpad |= 0x04;
        if (Has(Native.DPAD_RIGHT)) dpad |= 0x08;

        p[0] = (byte)Axis(g.sThumbLX);
        p[1] = (byte)Axis(g.sThumbLY, invert: true);
        p[2] = (byte)Axis(g.sThumbRX);
        p[3] = (byte)Axis(g.sThumbRY, invert: true);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(4), b);
        p[8] = dpad;
        p[9] = g.bLeftTrigger;
        p[10] = g.bRightTrigger;
        Rest(p);
    }

    private static void Neutral(byte[] p)
    {
        Array.Clear(p);
        Rest(p);
    }

    // Touch inactive, gyro still, accelerometer reading 1 g as if the pad lies flat.
    private static void Rest(byte[] p)
    {
        p.AsSpan(11, 22).Clear();
        BinaryPrimitives.WriteInt16LittleEndian(p.AsSpan(31), -8192);
    }

    // XInput: signed 16-bit, +Y = up. VIIPER: signed 8-bit, -128 = left/up.
    private static sbyte Axis(short v, bool invert = false)
    {
        int x = v >> 8;
        if (invert) x = -x - 1;
        return (sbyte)Math.Clamp(x, -128, 127);
    }

    private static int FindController(int? requested)
    {
        if (requested is int r) return Native.TryGetState((uint)r, out _) ? r : -1;
        for (uint i = 0; i < 4; i++)
            if (Native.TryGetState(i, out _)) return (int)i;
        return -1;
    }

    // Make sure the VIIPER server is up; start it if it's installed but not running.
    private static bool EnsureServer()
    {
        if (Api.Ping()) return true;
        string exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VIIPER", "viiper.exe");
        if (!File.Exists(exe)) return false;
        Status("Starting the VIIPER server...");
        Process.Start(new ProcessStartInfo(exe, "server") { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Minimized });
        for (int i = 0; i < 20; i++)
        {
            Thread.Sleep(250);
            if (Api.Ping()) return true;
        }
        return false;
    }

    private static void Status(string msg) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {msg}");

    private static void Error(string msg)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine(msg);
        Console.ResetColor();
    }

    private static void Pause()
    {
        if (Console.IsInputRedirected) return;
        Console.WriteLine("\nPress any key to close.");
        Console.ReadKey(true);
    }

    // VIIPER's TCP API: one request per connection, "path[ payload]\0", JSON reply, then close.
    private static class Api
    {
        public static string Request(string line)
        {
            using var c = new TcpClient();
            c.Connect(Host, ApiPort);
            var s = c.GetStream();
            s.Write(Encoding.UTF8.GetBytes(line + "\0"));
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            string reply = Encoding.UTF8.GetString(ms.ToArray()).Trim('\0', '\r', '\n', ' ');
            using var doc = JsonDocument.Parse(reply.Length == 0 ? "{}" : reply);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.Number && st.GetInt32() >= 400)
            {
                string detail = doc.RootElement.TryGetProperty("detail", out var d) ? d.ToString() : reply;
                throw new Exception($"VIIPER: {line.Split(' ')[0]} failed: {detail}");
            }
            return reply;
        }

        public static bool Ping()
        {
            try { Request("ping"); return true; } catch { return false; }
        }

        public static int CreateBus()
        {
            using var doc = JsonDocument.Parse(Request("bus/create"));
            return doc.RootElement.GetProperty("busId").GetInt32();
        }

        public static string AddDevice(int busId, string type)
        {
            using var doc = JsonDocument.Parse(Request($"bus/{busId}/add {{\"type\":\"{type}\"}}"));
            return doc.RootElement.GetProperty("devId").ToString();
        }
    }
}

internal static class Native
{
    public const ushort DPAD_UP = 0x0001, DPAD_DOWN = 0x0002, DPAD_LEFT = 0x0004, DPAD_RIGHT = 0x0008;
    public const ushort START = 0x0010, BACK = 0x0020, LS = 0x0040, RS = 0x0080;
    public const ushort LB = 0x0100, RB = 0x0200, GUIDE = 0x0400;
    public const ushort A = 0x1000, B = 0x2000, X = 0x4000, Y = 0x8000;

    [StructLayout(LayoutKind.Sequential)]
    public struct XINPUT_GAMEPAD
    {
        public ushort wButtons;
        public byte bLeftTrigger, bRightTrigger;
        public short sThumbLX, sThumbLY, sThumbRX, sThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct XINPUT_STATE
    {
        public uint dwPacketNumber;
        public XINPUT_GAMEPAD Gamepad;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct XINPUT_VIBRATION
    {
        public ushort wLeftMotorSpeed, wRightMotorSpeed;
    }

    // Ordinal 100 is the undocumented XInputGetStateEx, which also reports the Guide button.
    [DllImport("xinput1_4.dll", EntryPoint = "#100")]
    private static extern uint XInputGetStateEx(uint index, out XINPUT_STATE state);

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
    private static extern uint XInputGetStateStd(uint index, out XINPUT_STATE state);

    [DllImport("xinput1_4.dll")]
    public static extern uint XInputSetState(uint index, ref XINPUT_VIBRATION vibration);

    [DllImport("winmm.dll")] public static extern uint timeBeginPeriod(uint ms);
    [DllImport("winmm.dll")] public static extern uint timeEndPeriod(uint ms);

    private static bool _useEx = true;

    public static bool TryGetState(uint index, out XINPUT_STATE state)
    {
        if (_useEx)
        {
            try { return XInputGetStateEx(index, out state) == 0; }
            catch (EntryPointNotFoundException) { _useEx = false; }
        }
        return XInputGetStateStd(index, out state) == 0;
    }
}
