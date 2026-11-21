using System.Buffers.Binary;

namespace Xbox2Ps.Core;

/// <summary>A profile flattened into arrays, cheap enough to use on every poll.</summary>
public sealed class CompiledProfile
{
    internal readonly struct Stick(bool invertX, bool invertY, int deadzone)
    {
        public readonly bool InvertX = invertX, InvertY = invertY;
        public readonly int Deadzone = deadzone;
    }

    internal readonly DsOutput[] Map = new DsOutput[Mapper.InputCount];
    internal readonly (int Mask, DsOutput Output)[] Combos;
    internal readonly int Threshold;
    internal readonly bool Swap;
    internal readonly Stick Left, Right;
    internal readonly bool RumbleEnabled;
    internal readonly int RumbleStrength;

    /// <summary>Keyboard and mouse: on, the bound (action, key code) pairs, and the mouse settings.</summary>
    public readonly bool KbmEnabled;
    internal readonly (KbmAction Action, int Code)[] KbmBindings;
    internal readonly KbmSettings Kbm;

    /// <summary>The key codes to keep from other apps while captured.</summary>
    public IEnumerable<int> KbmCodes => KbmBindings.Select(b => b.Code).Distinct();

    public static CompiledProfile Default { get; } = From(Profile.CreateDefault());

    private CompiledProfile(Profile p)
    {
        Kbm = p.Kbm;
        KbmEnabled = p.Kbm.Enabled;
        KbmBindings = p.Kbm.Bindings
            .Select(b => (b.Key, KeyNames.Code(b.Value)))
            .Where(b => b.Item2 >= 0)
            .ToArray();
        foreach (var (input, output) in p.Buttons) Map[(int)input] = output;
        Combos = p.Combos.Select(c => ((1 << (int)c.First) | (1 << (int)c.Second), c.Output)).ToArray();
        Threshold = p.TriggerThreshold;
        Swap = p.SwapSticks;
        Left = new Stick(p.LeftStick.InvertX, p.LeftStick.InvertY, p.LeftStick.Deadzone);
        Right = new Stick(p.RightStick.InvertX, p.RightStick.InvertY, p.RightStick.Deadzone);
        RumbleEnabled = p.RumbleEnabled;
        RumbleStrength = p.RumbleStrength;
    }

    public static CompiledProfile From(Profile p) => new(p.Clone());
}

/// <summary>
/// Turns an XInput gamepad state into VIIPER's 33-byte DualSense input packet (little-endian):
/// 0 LX, 1 LY, 2 RX, 3 RY (int8, -128 = left/up) | 4..7 buttons | 8 d-pad bits | 9 L2 | 10 R2 |
/// 11..20 touch | 21..32 gyro/accel.
/// </summary>
public static class Mapper
{
    public const int PacketSize = 33;
    public const int InputCount = 17;

    // DualSense button bits, from VIIPER's device/dualsense/const.go.
    internal const uint Square = 0x0010, Cross = 0x0020, Circle = 0x0040, Triangle = 0x0080;
    internal const uint L1 = 0x0100, R1 = 0x0200, L2 = 0x0400, R2 = 0x0800;
    internal const uint Create = 0x1000, Options = 0x2000, L3 = 0x4000, R3 = 0x8000;
    internal const uint PS = 0x00010000, Touchpad = 0x00020000, Mute = 0x00040000;
    internal const byte DpadUp = 0x01, DpadDown = 0x02, DpadLeft = 0x04, DpadRight = 0x08;

    // XInput button mask for each XboxInput, in enum order. The triggers are analog (0).
    private static readonly ushort[] Masks =
    [
        XInput.A, XInput.B, XInput.X, XInput.Y,
        XInput.LB, XInput.RB, 0, 0,
        XInput.LS, XInput.RS,
        XInput.BACK, XInput.START, XInput.GUIDE,
        XInput.DPAD_UP, XInput.DPAD_DOWN, XInput.DPAD_LEFT, XInput.DPAD_RIGHT,
    ];

    public static bool IsPressed(in XInput.XINPUT_GAMEPAD g, XboxInput input, int threshold) => input switch
    {
        XboxInput.LT => g.bLeftTrigger > threshold,
        XboxInput.RT => g.bRightTrigger > threshold,
        _ => (g.wButtons & Masks[(int)input]) != 0,
    };

    private ref struct Out
    {
        public uint Buttons;
        public byte Dpad;
        public int L2, R2;

        /// <summary>Presses <paramref name="o"/>; for L2/R2, <paramref name="analog"/> is the trigger depth.</summary>
        public void Press(DsOutput o, int analog = 255)
        {
            switch (o)
            {
                case DsOutput.Cross: Buttons |= Mapper.Cross; break;
                case DsOutput.Circle: Buttons |= Mapper.Circle; break;
                case DsOutput.Square: Buttons |= Mapper.Square; break;
                case DsOutput.Triangle: Buttons |= Mapper.Triangle; break;
                case DsOutput.L1: Buttons |= Mapper.L1; break;
                case DsOutput.R1: Buttons |= Mapper.R1; break;
                case DsOutput.L2: Buttons |= Mapper.L2; L2 = Math.Max(L2, analog); break;
                case DsOutput.R2: Buttons |= Mapper.R2; R2 = Math.Max(R2, analog); break;
                case DsOutput.L3: Buttons |= Mapper.L3; break;
                case DsOutput.R3: Buttons |= Mapper.R3; break;
                case DsOutput.Create: Buttons |= Mapper.Create; break;
                case DsOutput.Options: Buttons |= Mapper.Options; break;
                case DsOutput.PS: Buttons |= Mapper.PS; break;
                case DsOutput.Touchpad: Buttons |= Mapper.Touchpad; break;
                case DsOutput.Mute: Buttons |= Mapper.Mute; break;
                case DsOutput.DpadUp: Dpad |= Mapper.DpadUp; break;
                case DsOutput.DpadDown: Dpad |= Mapper.DpadDown; break;
                case DsOutput.DpadLeft: Dpad |= Mapper.DpadLeft; break;
                case DsOutput.DpadRight: Dpad |= Mapper.DpadRight; break;
            }
        }
    }

    public static void Build(byte[] p, in XInput.XINPUT_GAMEPAD g, CompiledProfile c)
    {
        int pressed = 0;
        for (int i = 0; i < InputCount; i++)
            if (IsPressed(g, (XboxInput)i, c.Threshold)) pressed |= 1 << i;

        var o = new Out();

        // Combos first; their inputs don't also fire their own mappings while held.
        int suppressed = 0;
        foreach (var (mask, output) in c.Combos)
        {
            if ((pressed & mask) != mask) continue;
            o.Press(output);
            suppressed |= mask;
        }

        for (int i = 0; i < InputCount; i++)
        {
            if ((suppressed & (1 << i)) != 0) continue;
            var output = c.Map[i];
            if (output == DsOutput.None) continue;
            bool isPressed = (pressed & (1 << i)) != 0;

            if (output is DsOutput.L2 or DsOutput.R2 && (XboxInput)i is XboxInput.LT or XboxInput.RT)
            {
                // Trigger to trigger: the analog depth always passes through, the digital bit only past the threshold.
                int depth = (XboxInput)i == XboxInput.LT ? g.bLeftTrigger : g.bRightTrigger;
                if (output == DsOutput.L2) o.L2 = Math.Max(o.L2, depth); else o.R2 = Math.Max(o.R2, depth);
                if (isPressed) o.Buttons |= output == DsOutput.L2 ? L2 : R2;
            }
            else if (isPressed)
            {
                o.Press(output);
            }
        }

        short lx = g.sThumbLX, ly = g.sThumbLY, rx = g.sThumbRX, ry = g.sThumbRY;
        ApplyDeadzone(ref lx, ref ly, c.Left.Deadzone);
        ApplyDeadzone(ref rx, ref ry, c.Right.Deadzone);
        sbyte oLX = Axis(lx, c.Left.InvertX), oLY = Axis(ly, !c.Left.InvertY);
        sbyte oRX = Axis(rx, c.Right.InvertX), oRY = Axis(ry, !c.Right.InvertY);
        if (c.Swap) { (oLX, oRX) = (oRX, oLX); (oLY, oRY) = (oRY, oLY); }

        p[0] = (byte)oLX;
        p[1] = (byte)oLY;
        p[2] = (byte)oRX;
        p[3] = (byte)oRY;
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(4), o.Buttons);
        p[8] = o.Dpad;
        p[9] = (byte)o.L2;
        p[10] = (byte)o.R2;
        Rest(p);
    }

    /// <summary>
    /// Adds the keyboard and mouse on top of a packet already built from the controller:
    /// buttons combine, L2/R2 take the higher value, sticks add up (clamped).
    /// <paramref name="mouse"/> is the mouse's stick deflection from <see cref="MouseStick.Step"/>.
    /// </summary>
    public static void ApplyKbm(byte[] p, KbmFrame frame, CompiledProfile c, (double X, double Y) mouse)
    {
        if (!c.KbmEnabled || !frame.Captured) return;

        var o = new Out
        {
            Buttons = BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(4)),
            Dpad = p[8],
            L2 = p[9],
            R2 = p[10],
        };
        double lx = 0, ly = 0, rx = 0, ry = 0;
        foreach (var (action, code) in c.KbmBindings)
        {
            if (!frame.Down[code]) continue;
            switch (action)
            {
                case KbmAction.LeftStickUp: ly -= 1; break;
                case KbmAction.LeftStickDown: ly += 1; break;
                case KbmAction.LeftStickLeft: lx -= 1; break;
                case KbmAction.LeftStickRight: lx += 1; break;
                case KbmAction.RightStickUp: ry -= 1; break;
                case KbmAction.RightStickDown: ry += 1; break;
                case KbmAction.RightStickLeft: rx -= 1; break;
                case KbmAction.RightStickRight: rx += 1; break;
                default: o.Press(ToOutput(action)); break;
            }
        }

        // Opposite keys cancel; diagonals stay on the circle like a real stick.
        ClampToCircle(ref lx, ref ly);
        ClampToCircle(ref rx, ref ry);
        switch (c.Kbm.Mouse)
        {
            case MouseTarget.RightStick: rx += mouse.X; ry += mouse.Y; break;
            case MouseTarget.LeftStick: lx += mouse.X; ly += mouse.Y; break;
        }

        p[0] = AddAxis(p[0], lx);
        p[1] = AddAxis(p[1], ly);
        p[2] = AddAxis(p[2], rx);
        p[3] = AddAxis(p[3], ry);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(4), o.Buttons);
        p[8] = o.Dpad;
        p[9] = (byte)o.L2;
        p[10] = (byte)o.R2;
    }

    private static DsOutput ToOutput(KbmAction a) => a switch
    {
        KbmAction.Cross => DsOutput.Cross,
        KbmAction.Circle => DsOutput.Circle,
        KbmAction.Square => DsOutput.Square,
        KbmAction.Triangle => DsOutput.Triangle,
        KbmAction.L1 => DsOutput.L1,
        KbmAction.R1 => DsOutput.R1,
        KbmAction.L2 => DsOutput.L2,
        KbmAction.R2 => DsOutput.R2,
        KbmAction.L3 => DsOutput.L3,
        KbmAction.R3 => DsOutput.R3,
        KbmAction.Create => DsOutput.Create,
        KbmAction.Options => DsOutput.Options,
        KbmAction.PS => DsOutput.PS,
        KbmAction.Touchpad => DsOutput.Touchpad,
        KbmAction.Mute => DsOutput.Mute,
        KbmAction.DpadUp => DsOutput.DpadUp,
        KbmAction.DpadDown => DsOutput.DpadDown,
        KbmAction.DpadLeft => DsOutput.DpadLeft,
        KbmAction.DpadRight => DsOutput.DpadRight,
        _ => DsOutput.None,
    };

    private static void ClampToCircle(ref double x, ref double y)
    {
        x = Math.Clamp(x, -1, 1);
        y = Math.Clamp(y, -1, 1);
        double len = Math.Sqrt(x * x + y * y);
        if (len > 1) { x /= len; y /= len; }
    }

    // Adds a deflection (-1..1, + = right/down) to a packet axis byte (int8, -128 = left/up).
    private static byte AddAxis(byte current, double add)
    {
        if (add == 0) return current;
        int v = (sbyte)current + (int)Math.Round(add * 127);
        return (byte)(sbyte)Math.Clamp(v, -128, 127);
    }

    /// <summary>Everything released, sticks centred.</summary>
    public static void Neutral(byte[] p)
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
    internal static sbyte Axis(short v, bool invert = false)
    {
        int x = v >> 8;
        if (invert) x = -x - 1;
        return (sbyte)Math.Clamp(x, -128, 127);
    }

    /// <summary>Radial deadzone, rescaled so the stick still reaches full tilt.</summary>
    internal static void ApplyDeadzone(ref short x, ref short y, int percent)
    {
        if (percent <= 0) return;
        double fx = x, fy = y;
        double m = Math.Sqrt(fx * fx + fy * fy);
        double r = percent / 100.0 * short.MaxValue;
        if (m <= r) { x = 0; y = 0; return; }
        double scale = (m - r) / (short.MaxValue - r) * short.MaxValue / m;
        x = (short)Math.Clamp(Math.Round(fx * scale), short.MinValue, short.MaxValue);
        y = (short)Math.Clamp(Math.Round(fy * scale), short.MinValue, short.MaxValue);
    }

    /// <summary>Motor speeds for XInputSetState from the DualSense rumble values.</summary>
    /// <remarks>Small = high-frequency (right) motor, large = low-frequency (left) motor.</remarks>
    public static (ushort Left, ushort Right) Motors(byte small, byte large, CompiledProfile c)
    {
        if (!c.RumbleEnabled) return (0, 0);
        return ((ushort)(large * 257 * c.RumbleStrength / 100), (ushort)(small * 257 * c.RumbleStrength / 100));
    }

    /// <summary>The DualSense buttons a packet holds down, for showing in the UI.</summary>
    public static List<DsOutput> PressedOutputs(byte[] p)
    {
        uint b = BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(4));
        byte d = p[8];
        var list = new List<DsOutput>();
        void Add(bool on, DsOutput o) { if (on) list.Add(o); }
        Add((b & Cross) != 0, DsOutput.Cross);
        Add((b & Circle) != 0, DsOutput.Circle);
        Add((b & Square) != 0, DsOutput.Square);
        Add((b & Triangle) != 0, DsOutput.Triangle);
        Add((b & L1) != 0, DsOutput.L1);
        Add((b & R1) != 0, DsOutput.R1);
        Add((b & L2) != 0, DsOutput.L2);
        Add((b & R2) != 0, DsOutput.R2);
        Add((b & L3) != 0, DsOutput.L3);
        Add((b & R3) != 0, DsOutput.R3);
        Add((b & Create) != 0, DsOutput.Create);
        Add((b & Options) != 0, DsOutput.Options);
        Add((b & PS) != 0, DsOutput.PS);
        Add((b & Touchpad) != 0, DsOutput.Touchpad);
        Add((b & Mute) != 0, DsOutput.Mute);
        Add((d & DpadUp) != 0, DsOutput.DpadUp);
        Add((d & DpadDown) != 0, DsOutput.DpadDown);
        Add((d & DpadLeft) != 0, DsOutput.DpadLeft);
        Add((d & DpadRight) != 0, DsOutput.DpadRight);
        return list;
    }
}
