using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Xbox2Ps.Core;

/// <summary>Where keyboard and mouse capture stands.</summary>
public enum CaptureState
{
    /// <summary>Automatic mode, but PS Remote Play isn't the window in front.</summary>
    Waiting,
    /// <summary>Paused with the hotkey (or, without automatic mode, not switched on yet).</summary>
    Paused,
    /// <summary>Keys and mouse drive the DualSense; mapped keys and the mouse are kept from other apps.</summary>
    Captured,
}

/// <summary>The keyboard and mouse at one moment, as the mapper sees them.</summary>
public sealed class KbmFrame
{
    /// <summary>Indexed by <see cref="KeyNames"/> codes.</summary>
    public readonly bool[] Down = new bool[KeyNames.CodeCount];
    /// <summary>Raw mouse movement since the previous frame.</summary>
    public int MouseDx, MouseDy;
    public bool Captured;
}

/// <summary>
/// Reads the keyboard and mouse, and captures them while PS Remote Play is in front (or when the
/// hotkey says so). Low-level hooks see every key and click and swallow the mapped ones while
/// captured; Raw Input reports mouse movement even while the hook keeps the cursor still.
/// Everything runs on one dedicated thread with its own message loop.
/// </summary>
public sealed unsafe class KbmInput : IDisposable
{
    private const string RemotePlayProcess = "RemotePlay";
    private const uint WmApplyOptions = 0x8001; // WM_APP + 1
    private const uint RecheckMs = 250;
    /// <summary>Size of the invisible window the parked cursor sits on, which hides it.</summary>
    private const int CoverSize = 24;

    private readonly bool[] _down = new bool[KeyNames.CodeCount];
    private volatile bool[] _bound = new bool[KeyNames.CodeCount];
    private int _dx, _dy;
    private long _wheelUpUntil, _wheelDownUntil;

    private volatile bool _captured;
    private bool _paused, _manualOn, _hotkeyHeld;
    private volatile int _hotkey;
    private volatile bool _autoCapture;
    private bool _hasSavedCursor;
    private POINT _savedCursor;
    private POINT _park, _parkBase;
    private bool _parkAlt, _cursorHidden;

    // Remote-control apps (Parsec, Remote Desktop) send the mouse as screen positions instead of
    // movement; this turns them into movement and says when to re-centre.
    private PointerTracker _pointer = new(200, 400);
    private readonly Dictionary<uint, string> _processNames = [];

    private static int _instances;

    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private volatile bool _quit;
    private string? _startError;
    private uint _threadId;
    private IntPtr _hwnd, _cover, _keyboardHook, _mouseHook, _foregroundHook;

    // Kept in fields so the GC never collects a delegate Windows still calls.
    private readonly WndProcDelegate _wndProc;
    private readonly HookProcDelegate _keyboardProc, _mouseProc;
    private readonly WinEventDelegate _foregroundProc;

    /// <summary>Raised on the input thread whenever the capture state changes.</summary>
    public event Action<CaptureState>? StateChanged;

    public CaptureState State { get; private set; } = CaptureState.Waiting;
    public bool IsCaptured => _captured;

    /// <param name="hotkey">Virtual-key code that pauses and resumes capture.</param>
    /// <param name="autoCapture">Capture whenever PS Remote Play is the window in front.</param>
    public KbmInput(int hotkey, bool autoCapture)
    {
        _hotkey = hotkey;
        _autoCapture = autoCapture;
        _wndProc = WndProc;
        _keyboardProc = KeyboardProc;
        _mouseProc = MouseProc;
        _foregroundProc = (_, _, hwnd, _, _, _, _) => Evaluate(hwnd);
        _thread = new Thread(Run) { IsBackground = true, Name = "xbox2ps keyboard+mouse" };
        _thread.Start();
        if (!_ready.Wait(3000))
        {
            Dispose();
            throw new InvalidOperationException("The keyboard and mouse reader didn't start in time.");
        }
        if (_startError is not null)
        {
            Dispose();
            throw new InvalidOperationException(_startError);
        }
    }

    public void SetOptions(int hotkey, bool autoCapture)
    {
        _hotkey = hotkey;
        _autoCapture = autoCapture;
        if (_hwnd != IntPtr.Zero) PostMessage(_hwnd, WmApplyOptions, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>Which key codes are mapped, so they're swallowed while captured. Unmapped keys still reach Windows.</summary>
    public void SetBindings(IEnumerable<int> codes)
    {
        var bound = new bool[KeyNames.CodeCount];
        foreach (int c in codes) if (c is >= 0 and < KeyNames.CodeCount) bound[c] = true;
        _bound = bound;
    }

    /// <summary>Fills <paramref name="frame"/> and resets the accumulated mouse movement.</summary>
    public void Read(KbmFrame frame)
    {
        int dx = Interlocked.Exchange(ref _dx, 0), dy = Interlocked.Exchange(ref _dy, 0);
        frame.Captured = _captured;
        if (!frame.Captured)
        {
            Array.Clear(frame.Down);
            frame.MouseDx = frame.MouseDy = 0;
            return;
        }
        Array.Copy(_down, frame.Down, KeyNames.CodeCount);
        long now = Environment.TickCount64;
        frame.Down[KeyNames.WheelUp] = now < Interlocked.Read(ref _wheelUpUntil);
        frame.Down[KeyNames.WheelDown] = now < Interlocked.Read(ref _wheelDownUntil);
        frame.MouseDx = dx;
        frame.MouseDy = dy;
    }

    public void Dispose()
    {
        _quit = true;
        if (_threadId != 0) PostThreadMessage(_threadId, 0x0012 /* WM_QUIT */, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(2000);
    }

    // ---- The input thread ----

    private void Run()
    {
        _threadId = GetCurrentThreadId();
        IntPtr module = GetModuleHandle(null);
        // A new class for every reader, unregistered when it ends: a class outlives its thread, and
        // a reused one would send messages to a window procedure that has already been collected.
        string className = $"xbox2ps-kbm-{Environment.ProcessId}-{Interlocked.Increment(ref _instances)}";
        var wc = new WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = _wndProc,
            hInstance = module,
            lpszClassName = className,
        };
        if (RegisterClassEx(ref wc) == 0)
        {
            _startError = $"Couldn't register the input window (error {Marshal.GetLastPInvokeError()}).";
            _ready.Set();
            return;
        }

        try
        {
            _hwnd = CreateWindowEx(0, className, "xbox2ps keyboard+mouse", 0, 0, 0, 0, 0, new IntPtr(-3) /* HWND_MESSAGE */, IntPtr.Zero, module, IntPtr.Zero);
            if (_hwnd == IntPtr.Zero)
            {
                _startError = $"Couldn't create the input window (error {Marshal.GetLastPInvokeError()}).";
                _ready.Set();
                return;
            }

            // While captured the cursor is parked on this tiny, practically invisible window, which
            // hides it. A hidden cursor also tells streaming apps such as Parsec that a game has the
            // mouse, so they send movement rather than positions.
            _cover = CreateWindowEx(0x00000008 /* TOPMOST */ | 0x00000080 /* TOOLWINDOW */ | 0x08000000 /* NOACTIVATE */ | 0x00080000 /* LAYERED */,
                className, "", 0x80000000 /* WS_POPUP */, 0, 0, CoverSize, CoverSize, IntPtr.Zero, IntPtr.Zero, module, IntPtr.Zero);
            if (_cover != IntPtr.Zero) SetLayeredWindowAttributes(_cover, 0, 1, 0x02 /* LWA_ALPHA */);

            var mouse = new[] { new RAWINPUTDEVICE { usUsagePage = 1, usUsage = 2, dwFlags = 0x100 /* RIDEV_INPUTSINK */, hwndTarget = _hwnd } };
            RegisterRawInputDevices(mouse, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
            _keyboardHook = SetWindowsHookEx(13 /* WH_KEYBOARD_LL */, _keyboardProc, module, 0);
            _mouseHook = SetWindowsHookEx(14 /* WH_MOUSE_LL */, _mouseProc, module, 0);
            _foregroundHook = SetWinEventHook(3 /* EVENT_SYSTEM_FOREGROUND */, 3, IntPtr.Zero, _foregroundProc, 0, 0, 0 /* WINEVENT_OUTOFCONTEXT */);
            if (_keyboardHook == IntPtr.Zero || _mouseHook == IntPtr.Zero)
                _startError = "Windows refused the keyboard and mouse hooks.";
            else
            {
                // Windows can report no foreground window for a moment while focus moves, so a switch
                // could be missed; a light re-check a few times a second catches it.
                SetTimer(_hwnd, 1u, RecheckMs, IntPtr.Zero);
                Evaluate();
            }
            _ready.Set();

            if (_startError is null && !_quit)
            {
                while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
                {
                    TranslateMessage(ref msg);
                    DispatchMessage(ref msg);
                }
            }

            SetCaptured(false);
            KillTimer(_hwnd, 1u);
            if (_foregroundHook != IntPtr.Zero) UnhookWinEvent(_foregroundHook);
            if (_keyboardHook != IntPtr.Zero) UnhookWindowsHookEx(_keyboardHook);
            if (_mouseHook != IntPtr.Zero) UnhookWindowsHookEx(_mouseHook);
            if (_cover != IntPtr.Zero) DestroyWindow(_cover);
            DestroyWindow(_hwnd);
        }
        finally
        {
            UnregisterClass(className, module);
        }
    }

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (hwnd == _cover && _cover != IntPtr.Zero)
        {
            if (msg == 0x0020 /* WM_SETCURSOR */) { SetCursor(IntPtr.Zero); return new IntPtr(1); }
            if (msg == 0x0021 /* WM_MOUSEACTIVATE */) return new IntPtr(3); // MA_NOACTIVATE
            return DefWindowProc(hwnd, msg, wParam, lParam);
        }
        if (msg == 0x00FF /* WM_INPUT */)
        {
            if (_captured) ReadRawMouse(lParam);
        }
        else if (msg == WmApplyOptions || msg == 0x0113 /* WM_TIMER */)
        {
            Evaluate();
            if (_captured) KeepCursorHidden();
            return IntPtr.Zero;
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private void ReadRawMouse(IntPtr handle)
    {
        uint size = 0, header = (uint)sizeof(RAWINPUTHEADER);
        GetRawInputData(handle, 0x10000003 /* RID_INPUT */, null, ref size, header);
        if (size == 0 || size > 256) return;
        byte* buf = stackalloc byte[(int)size];
        if (GetRawInputData(handle, 0x10000003, buf, ref size, header) != size) return;
        var hdr = (RAWINPUTHEADER*)buf;
        if (hdr->dwType != 0 /* RIM_TYPEMOUSE */) return;
        var m = (RAWMOUSE*)(buf + header);
        if ((m->usFlags & 0x01) == 0) // MOUSE_MOVE_RELATIVE: a real mouse
        {
            Interlocked.Add(ref _dx, m->lLastX);
            Interlocked.Add(ref _dy, m->lLastY);
            return;
        }

        // A position, 0-65535 across the whole desktop or the primary screen.
        if (m->lLastX == 0 && m->lLastY == 0 && m->usButtonFlags != 0) return; // a click without a position
        double x, y;
        if ((m->usFlags & 0x02) != 0) // MOUSE_VIRTUAL_DESKTOP
        {
            x = GetSystemMetrics(76) + m->lLastX / 65535.0 * GetSystemMetrics(78);
            y = GetSystemMetrics(77) + m->lLastY / 65535.0 * GetSystemMetrics(79);
        }
        else
        {
            x = m->lLastX / 65535.0 * GetSystemMetrics(0);
            y = m->lLastY / 65535.0 * GetSystemMetrics(1);
        }
        var (dx, dy, recentre) = _pointer.Feed(x, y, Environment.TickCount64);
        if (dx != 0) Interlocked.Add(ref _dx, dx);
        if (dy != 0) Interlocked.Add(ref _dy, dy);
        if (recentre)
        {
            // Alternate between two park points 2 px apart, so the host cursor really moves and
            // the remote-control app sees it (and moves the client's pointer back).
            _parkAlt = !_parkAlt;
            _park = new POINT { X = _parkBase.X + (_parkAlt ? 2 : 0), Y = _parkBase.Y };
            _pointer.MovePark(_park.X, _park.Y);
            SetCursorPos(_park.X, _park.Y);
        }
    }

    private IntPtr KeyboardProc(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var k = (KBDLLHOOKSTRUCT*)lParam;
            int msg = (int)wParam;
            bool down = msg is 0x0100 or 0x0104;        // WM_KEYDOWN, WM_SYSKEYDOWN
            bool up = msg is 0x0101 or 0x0105;          // WM_KEYUP, WM_SYSKEYUP
            int vk = (int)k->vkCode;
            if (vk is > 0 and < 256 && (down || up))
            {
                if (vk == _hotkey)
                {
                    if (down && !_hotkeyHeld) { _hotkeyHeld = true; ToggleWithHotkey(); }
                    if (up) _hotkeyHeld = false;
                    return 1; // the hotkey is xbox2ps's alone
                }
                _down[vk] = down;
                if (_captured && _bound[vk] && !AlwaysPassThrough(vk)) return 1;
            }
        }
        return CallNextHookEx(_keyboardHook, code, wParam, lParam);
    }

    // Alt and Windows keys always reach Windows, and so does Tab or Esc while Alt is held,
    // so Alt+Tab, Alt+Esc and Windows shortcuts keep working while captured.
    private bool AlwaysPassThrough(int vk)
    {
        if (vk is 0xA4 or 0xA5 or 0x5B or 0x5C) return true; // Alt, Windows
        bool alt = _down[0xA4] || _down[0xA5];
        return alt && vk is 0x09 or 0x1B;
    }

    private IntPtr MouseProc(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var m = (MSLLHOOKSTRUCT*)lParam;
            switch ((int)wParam)
            {
                case 0x0201: _down[0x01] = true; break;   // left
                case 0x0202: _down[0x01] = false; break;
                case 0x0204: _down[0x02] = true; break;   // right
                case 0x0205: _down[0x02] = false; break;
                case 0x0207: _down[0x04] = true; break;   // middle
                case 0x0208: _down[0x04] = false; break;
                case 0x020B: _down[(m->mouseData >> 16) == 1 ? 0x05 : 0x06] = true; break;  // X buttons
                case 0x020C: _down[(m->mouseData >> 16) == 1 ? 0x05 : 0x06] = false; break;
                case 0x020A:                                // wheel: a short press per notch
                    short delta = (short)(m->mouseData >> 16);
                    long until = Environment.TickCount64 + 80;
                    if (delta > 0) Interlocked.Exchange(ref _wheelUpUntil, until);
                    else if (delta < 0) Interlocked.Exchange(ref _wheelDownUntil, until);
                    break;
            }
            // While captured the cursor stays parked and clicks don't reach Remote Play. Cursor
            // moves xbox2ps makes itself (SetCursorPos) don't pass through here.
            if (_captured) return 1;
        }
        return CallNextHookEx(_mouseHook, code, wParam, lParam);
    }

    private void ToggleWithHotkey()
    {
        if (_autoCapture) _paused = !_paused;
        else _manualOn = !_manualOn;
        Evaluate();
    }

    /// <summary>Works out whether to capture now. Runs on the input thread.</summary>
    /// <param name="fg">The window that just came to the front, if known.</param>
    private void Evaluate(IntPtr fg = default)
    {
        uint pid = 0;
        if (fg == IntPtr.Zero) fg = GetForegroundWindow();
        if (fg != IntPtr.Zero) GetWindowThreadProcessId(fg, out pid);
        bool ownWindow = pid == (uint)Environment.ProcessId;
        bool remotePlay = !ownWindow && string.Equals(ProcessName(pid), RemotePlayProcess, StringComparison.OrdinalIgnoreCase);

        bool capture;
        CaptureState state;
        if (_autoCapture)
        {
            capture = remotePlay && !_paused;
            state = capture ? CaptureState.Captured : _paused ? CaptureState.Paused : CaptureState.Waiting;
        }
        else
        {
            capture = _manualOn && !ownWindow;
            state = capture ? CaptureState.Captured : CaptureState.Paused;
        }

        SetCaptured(capture);
        if (state != State)
        {
            State = state;
            StateChanged?.Invoke(state);
        }
    }

    private string ProcessName(uint pid)
    {
        if (pid == 0) return "";
        if (_processNames.TryGetValue(pid, out var name)) return name;
        try { name = Process.GetProcessById((int)pid).ProcessName; }
        catch (Exception) { name = ""; }
        if (_processNames.Count > 256) _processNames.Clear();
        _processNames[pid] = name;
        return name;
    }

    /// <summary>
    /// Windows only works out which window is under the cursor when it moves, so the parked cursor
    /// can keep the look of the window it came from, or end up under a window raised above the cover.
    /// Puts the cover back on top and nudges the cursor onto it. SetCursorPos doesn't pass through
    /// the low-level hook, so the nudge isn't blocked.
    /// </summary>
    private void KeepCursorHidden()
    {
        var ci = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        if (!GetCursorInfo(ref ci) || (ci.flags & 1) == 0 || ci.hCursor == IntPtr.Zero) return;
        if (_cover != IntPtr.Zero)
            SetWindowPos(_cover, new IntPtr(-1) /* HWND_TOPMOST */, _park.X - CoverSize / 2, _park.Y - CoverSize / 2, CoverSize, CoverSize, 0x0010 /* NOACTIVATE */ | 0x0040 /* SHOWWINDOW */);
        SetCursorPos(_park.X - 1, _park.Y);
        SetCursorPos(_park.X, _park.Y);
        _pointer.ExpectSync(Environment.TickCount64);
    }

    private void SetCaptured(bool capture)
    {
        if (capture == _captured) return;
        if (capture)
        {
            // Park the cursor in the middle of the screen in front, on the cover window that hides
            // it. The middle leaves room in every direction for apps that send positions.
            if (GetCursorPos(out _savedCursor)) _hasSavedCursor = true;
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(MonitorFromWindow(GetForegroundWindow(), 2 /* nearest */), ref info))
            {
                var r = info.rcMonitor;
                _parkBase = new POINT { X = (r.Left + r.Right) / 2, Y = (r.Top + r.Bottom) / 2 };
                _park = _parkBase;
                _parkAlt = false;
                int size = Math.Min(r.Right - r.Left, r.Bottom - r.Top);
                _pointer = new PointerTracker(radius: size / 6.0, maxJump: size / 3.0);
                _pointer.Reset(_park.X, _park.Y, Environment.TickCount64);
                if (_cover != IntPtr.Zero)
                    SetWindowPos(_cover, new IntPtr(-1) /* HWND_TOPMOST */, _park.X - CoverSize / 2, _park.Y - CoverSize / 2, CoverSize, CoverSize, 0x0010 /* NOACTIVATE */ | 0x0040 /* SHOWWINDOW */);
                if (!_cursorHidden) { ShowCursor(false); _cursorHidden = true; }
                SetCursorPos(_park.X, _park.Y);
            }
            Interlocked.Exchange(ref _dx, 0);
            Interlocked.Exchange(ref _dy, 0);
            _captured = true;
        }
        else
        {
            _captured = false;
            if (_cover != IntPtr.Zero) ShowWindow(_cover, 0 /* SW_HIDE */);
            if (_cursorHidden) { ShowCursor(true); _cursorHidden = false; }
            if (_hasSavedCursor) SetCursorPos(_savedCursor.X, _savedCursor.Y);
            _hasSavedCursor = false;
        }
    }

    // ---- Win32 ----

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    private delegate IntPtr HookProcDelegate(int code, IntPtr wParam, IntPtr lParam);
    private delegate void WinEventDelegate(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public int cbSize;
        public uint style;
        public WndProcDelegate lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)] private struct RAWINPUTDEVICE { public ushort usUsagePage, usUsage; public uint dwFlags; public IntPtr hwndTarget; }
    [StructLayout(LayoutKind.Sequential)] private struct RAWINPUTHEADER { public uint dwType, dwSize; public IntPtr hDevice, wParam; }
    [StructLayout(LayoutKind.Sequential)] private struct RAWMOUSE { public ushort usFlags, pad, usButtonFlags, usButtonData; public uint ulRawButtons; public int lLastX, lLastY; public uint ulExtraInformation; }
    [StructLayout(LayoutKind.Sequential)] private struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr extra; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct CURSORINFO { public int cbSize, flags; public IntPtr hCursor; public POINT pt; }
    [StructLayout(LayoutKind.Sequential)] private struct MSLLHOOKSTRUCT { public POINT pt; public uint mouseData, flags, time; public IntPtr extra; }
    [StructLayout(LayoutKind.Sequential)] private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public POINT pt; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassEx(ref WNDCLASSEX wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool UnregisterClass(string cls, IntPtr instance);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateWindowEx(uint exStyle, string cls, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, uint count, uint size);
    [DllImport("user32.dll")] private static extern uint GetRawInputData(IntPtr handle, uint command, byte* data, ref uint size, uint headerSize);
    [DllImport("user32.dll")] private static extern IntPtr SetWindowsHookEx(int id, HookProcDelegate proc, IntPtr module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventDelegate proc, uint pid, uint thread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] private static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint thread, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint key, byte alpha, uint flags);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] private static extern int ShowCursor(bool show);
    [DllImport("user32.dll")] private static extern IntPtr SetCursor(IntPtr cursor);
    [DllImport("user32.dll")] private static extern bool GetCursorInfo(ref CURSORINFO info);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern UIntPtr SetTimer(IntPtr hwnd, UIntPtr id, uint ms, IntPtr proc);
    [DllImport("user32.dll")] private static extern bool KillTimer(IntPtr hwnd, UIntPtr id);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
}
