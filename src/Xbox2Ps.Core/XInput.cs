using System.Runtime.InteropServices;

namespace Xbox2Ps.Core;

/// <summary>XInput through P/Invoke, including the undocumented Guide-button call.</summary>
public static class XInput
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

    /// <summary>The requested slot if it is connected, otherwise the first connected slot; -1 when none.</summary>
    public static int FindController(int? requested)
    {
        if (requested is int r) return TryGetState((uint)r, out _) ? r : -1;
        for (uint i = 0; i < 4; i++)
            if (TryGetState(i, out _)) return (int)i;
        return -1;
    }

    public static void SetVibration(int slot, ushort left, ushort right)
    {
        if (slot < 0) return;
        var vib = new XINPUT_VIBRATION { wLeftMotorSpeed = left, wRightMotorSpeed = right };
        XInputSetState((uint)slot, ref vib);
    }

    public static void StopVibration(int slot) => SetVibration(slot, 0, 0);
}
