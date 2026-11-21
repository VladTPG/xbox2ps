using System.Text.Json;
using System.Text.Json.Serialization;

namespace Xbox2Ps.Core;

/// <summary>Every physical input on an Xbox controller that can be remapped.</summary>
public enum XboxInput
{
    A, B, X, Y,
    LB, RB, LT, RT,
    LS, RS,
    View, Menu, Guide,
    DpadUp, DpadDown, DpadLeft, DpadRight,
}

/// <summary>What a mapped input presses on the virtual DualSense.</summary>
public enum DsOutput
{
    None,
    Cross, Circle, Square, Triangle,
    L1, R1, L2, R2,
    L3, R3,
    Create, Options, PS, Touchpad, Mute,
    DpadUp, DpadDown, DpadLeft, DpadRight,
}

/// <summary>Two Xbox inputs held together press <see cref="Output"/> instead of their own mappings.</summary>
public sealed class Combo
{
    public XboxInput First { get; set; }
    public XboxInput Second { get; set; }
    public DsOutput Output { get; set; }

    public Combo() { }
    public Combo(XboxInput first, XboxInput second, DsOutput output) => (First, Second, Output) = (first, second, output);
}

public sealed class StickSettings
{
    public bool InvertX { get; set; }
    /// <summary>An extra flip on top of the Y conversion every stick gets.</summary>
    public bool InvertY { get; set; }
    /// <summary>Radial deadzone, percent of full tilt (0-50).</summary>
    public int Deadzone { get; set; }
}

/// <summary>A named, saved mapping. The defaults reproduce the original console xbox2ps exactly.</summary>
public sealed class Profile
{
    public const int MaxDeadzone = 50;
    public const int MaxThreshold = 254;

    public string Name { get; set; } = "Default";
    public Dictionary<XboxInput, DsOutput> Buttons { get; set; } = DefaultButtons();
    public List<Combo> Combos { get; set; } = DefaultCombos();
    public StickSettings LeftStick { get; set; } = new();
    public StickSettings RightStick { get; set; } = new();
    public bool SwapSticks { get; set; }
    /// <summary>LT/RT count as pressed when their value is above this (0-254).</summary>
    public int TriggerThreshold { get; set; } = 30;
    public bool RumbleEnabled { get; set; } = true;
    /// <summary>Percent (0-100).</summary>
    public int RumbleStrength { get; set; } = 100;

    public static Profile CreateDefault(string name = "Default") => new() { Name = name };

    public static Dictionary<XboxInput, DsOutput> DefaultButtons() => new()
    {
        [XboxInput.A] = DsOutput.Cross,
        [XboxInput.B] = DsOutput.Circle,
        [XboxInput.X] = DsOutput.Square,
        [XboxInput.Y] = DsOutput.Triangle,
        [XboxInput.LB] = DsOutput.L1,
        [XboxInput.RB] = DsOutput.R1,
        [XboxInput.LT] = DsOutput.L2,
        [XboxInput.RT] = DsOutput.R2,
        [XboxInput.LS] = DsOutput.L3,
        [XboxInput.RS] = DsOutput.R3,
        [XboxInput.View] = DsOutput.Create,
        [XboxInput.Menu] = DsOutput.Options,
        [XboxInput.Guide] = DsOutput.PS,
        [XboxInput.DpadUp] = DsOutput.DpadUp,
        [XboxInput.DpadDown] = DsOutput.DpadDown,
        [XboxInput.DpadLeft] = DsOutput.DpadLeft,
        [XboxInput.DpadRight] = DsOutput.DpadRight,
    };

    public static List<Combo> DefaultCombos() => [new Combo(XboxInput.View, XboxInput.Menu, DsOutput.Touchpad)];

    /// <summary>Fills in anything missing and clamps everything into range, so any loaded file is usable.</summary>
    public Profile Normalize()
    {
        Name = string.IsNullOrWhiteSpace(Name) ? "Default" : Name.Trim();
        Buttons ??= [];
        foreach (var (input, output) in DefaultButtons())
            Buttons.TryAdd(input, output);
        foreach (var key in Buttons.Keys.Where(k => !Enum.IsDefined(k)).ToList())
            Buttons.Remove(key);
        foreach (var key in Buttons.Keys.Where(k => !Enum.IsDefined(Buttons[k])).ToList())
            Buttons[key] = DsOutput.None;

        Combos = (Combos ?? [])
            .Where(c => c is not null && c.First != c.Second && Enum.IsDefined(c.First) && Enum.IsDefined(c.Second) && Enum.IsDefined(c.Output))
            .ToList();

        LeftStick ??= new();
        RightStick ??= new();
        LeftStick.Deadzone = Math.Clamp(LeftStick.Deadzone, 0, MaxDeadzone);
        RightStick.Deadzone = Math.Clamp(RightStick.Deadzone, 0, MaxDeadzone);
        TriggerThreshold = Math.Clamp(TriggerThreshold, 0, MaxThreshold);
        RumbleStrength = Math.Clamp(RumbleStrength, 0, 100);
        return this;
    }

    public Profile Clone() => FromJson(ToJson());

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>Parses a profile; unknown fields are ignored and missing ones get defaults.</summary>
    /// <exception cref="JsonException">The text isn't a valid profile.</exception>
    public static Profile FromJson(string json)
    {
        var p = JsonSerializer.Deserialize<Profile>(json, Json) ?? throw new JsonException("The file is empty.");
        return p.Normalize();
    }
}
