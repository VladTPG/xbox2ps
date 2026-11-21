namespace Xbox2Ps.Core;

/// <summary>
/// Stable names for keyboard keys and mouse buttons, as stored in profiles ("W", "Space", "MouseLeft").
/// Codes are Windows virtual-key codes, plus two made-up codes for the mouse wheel.
/// </summary>
public static class KeyNames
{
    public const int WheelUp = 256, WheelDown = 257;
    public const int CodeCount = 258;

    private static readonly string?[] NameByCode = new string?[CodeCount];
    private static readonly string?[] DisplayByCode = new string?[CodeCount];
    private static readonly Dictionary<string, int> CodeByName = new(StringComparer.OrdinalIgnoreCase);

    static KeyNames()
    {
        void Add(int code, string name, string? display = null)
        {
            NameByCode[code] = name;
            DisplayByCode[code] = display ?? name;
            CodeByName[name] = code;
        }

        Add(0x01, "MouseLeft", "Left click");
        Add(0x02, "MouseRight", "Right click");
        Add(0x04, "MouseMiddle", "Middle click");
        Add(0x05, "MouseX1", "Mouse back");
        Add(0x06, "MouseX2", "Mouse forward");
        Add(WheelUp, "WheelUp", "Wheel up");
        Add(WheelDown, "WheelDown", "Wheel down");

        Add(0x08, "Backspace");
        Add(0x09, "Tab");
        Add(0x0D, "Enter");
        Add(0x13, "Pause");
        Add(0x14, "CapsLock", "Caps Lock");
        Add(0x1B, "Escape", "Esc");
        Add(0x20, "Space");
        Add(0x21, "PageUp", "Page Up");
        Add(0x22, "PageDown", "Page Down");
        Add(0x23, "End");
        Add(0x24, "Home");
        Add(0x25, "Left", "← Left");
        Add(0x26, "Up", "↑ Up");
        Add(0x27, "Right", "→ Right");
        Add(0x28, "Down", "↓ Down");
        Add(0x2D, "Insert");
        Add(0x2E, "Delete");
        for (int d = 0; d <= 9; d++) Add(0x30 + d, d.ToString());
        for (char c = 'A'; c <= 'Z'; c++) Add(c, c.ToString());
        for (int n = 0; n <= 9; n++) Add(0x60 + n, $"Numpad{n}", $"Numpad {n}");
        Add(0x6A, "NumpadMultiply", "Numpad *");
        Add(0x6B, "NumpadAdd", "Numpad +");
        Add(0x6D, "NumpadSubtract", "Numpad -");
        Add(0x6E, "NumpadDecimal", "Numpad .");
        Add(0x6F, "NumpadDivide", "Numpad /");
        for (int f = 1; f <= 24; f++) Add(0x6F + f, $"F{f}");
        Add(0x90, "NumLock", "Num Lock");
        Add(0x91, "ScrollLock", "Scroll Lock");
        Add(0xA0, "LeftShift", "Left Shift");
        Add(0xA1, "RightShift", "Right Shift");
        Add(0xA2, "LeftCtrl", "Left Ctrl");
        Add(0xA3, "RightCtrl", "Right Ctrl");
        Add(0xA4, "LeftAlt", "Left Alt");
        Add(0xA5, "RightAlt", "Right Alt");
        Add(0xBA, "Semicolon", ";");
        Add(0xBB, "Equals", "=");
        Add(0xBC, "Comma", ",");
        Add(0xBD, "Minus", "-");
        Add(0xBE, "Period", ".");
        Add(0xBF, "Slash", "/");
        Add(0xC0, "Backquote", "`");
        Add(0xDB, "LeftBracket", "[");
        Add(0xDC, "Backslash", "\\");
        Add(0xDD, "RightBracket", "]");
        Add(0xDE, "Quote", "'");
    }

    /// <summary>The code for a stored name, or -1 if it isn't one we know.</summary>
    public static int Code(string? name) => name is not null && CodeByName.TryGetValue(name, out int c) ? c : -1;

    /// <summary>The stored name for a code, or null if it isn't bindable.</summary>
    public static string? Name(int code) => code is >= 0 and < CodeCount ? NameByCode[code] : null;

    /// <summary>What to show in the UI for a stored name.</summary>
    public static string Display(string? name)
    {
        int code = Code(name);
        return code >= 0 ? DisplayByCode[code]! : "—";
    }

    public static bool IsMouse(int code) => code is 0x01 or 0x02 or 0x04 or 0x05 or 0x06 or WheelUp or WheelDown;
}
