// The mapping code from the original console xbox2ps (Program.cs, lines 194-252 at the
// "Starting point" commit), copied verbatim. The default profile must match it byte for byte.

using System.Buffers.Binary;
using Native = Xbox2Ps.Core.XInput;

namespace Xbox2Ps.Tests;

internal static class Legacy
{
    /// <summary>Entry point for the tests; everything below is the original, unchanged.</summary>
    public static void BuildPacket(byte[] p, Native.XINPUT_GAMEPAD g) => Build(p, g);

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
}
