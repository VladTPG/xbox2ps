using Xbox2Ps.Core;

namespace Xbox2Ps.ViewModels;

/// <summary>Names shown in the UI for inputs and outputs.</summary>
public static class Labels
{
    public static string Of(XboxInput input) => input switch
    {
        XboxInput.A => "A",
        XboxInput.B => "B",
        XboxInput.X => "X",
        XboxInput.Y => "Y",
        XboxInput.LB => "LB (left bumper)",
        XboxInput.RB => "RB (right bumper)",
        XboxInput.LT => "LT (left trigger)",
        XboxInput.RT => "RT (right trigger)",
        XboxInput.LS => "Left stick click",
        XboxInput.RS => "Right stick click",
        XboxInput.View => "View",
        XboxInput.Menu => "Menu",
        XboxInput.Guide => "Xbox button",
        XboxInput.DpadUp => "D-pad up",
        XboxInput.DpadDown => "D-pad down",
        XboxInput.DpadLeft => "D-pad left",
        XboxInput.DpadRight => "D-pad right",
        _ => input.ToString(),
    };

    public static string Of(DsOutput output) => output switch
    {
        DsOutput.None => "Nothing",
        DsOutput.Cross => "Cross",
        DsOutput.Circle => "Circle",
        DsOutput.Square => "Square",
        DsOutput.Triangle => "Triangle",
        DsOutput.L1 => "L1",
        DsOutput.R1 => "R1",
        DsOutput.L2 => "L2",
        DsOutput.R2 => "R2",
        DsOutput.L3 => "L3",
        DsOutput.R3 => "R3",
        DsOutput.Create => "Create",
        DsOutput.Options => "Options",
        DsOutput.PS => "PS button",
        DsOutput.Touchpad => "Touchpad click",
        DsOutput.Mute => "Mute",
        DsOutput.DpadUp => "D-pad up",
        DsOutput.DpadDown => "D-pad down",
        DsOutput.DpadLeft => "D-pad left",
        DsOutput.DpadRight => "D-pad right",
        _ => output.ToString(),
    };

    public static IReadOnlyList<Option<DsOutput>> Outputs { get; } =
        Enum.GetValues<DsOutput>().Select(o => new Option<DsOutput>(o, Of(o))).ToList();

    /// <summary>The shared dropdown entry for an output (so the preview reuses the same objects).</summary>
    public static Option<DsOutput> OptionOf(DsOutput output) => Outputs[(int)output];

    public static IReadOnlyList<Option<XboxInput>> Inputs { get; } =
        Enum.GetValues<XboxInput>().Select(i => new Option<XboxInput>(i, Of(i))).ToList();
}
