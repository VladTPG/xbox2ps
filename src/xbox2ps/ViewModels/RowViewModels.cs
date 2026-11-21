using Xbox2Ps.Core;

namespace Xbox2Ps.ViewModels;

/// <summary>One row on the Buttons tab: an Xbox input and what it presses.</summary>
public sealed class ButtonRowViewModel(XboxInput input, Action edited) : ObservableObject
{
    private DsOutput _output;
    private bool _isHeld;

    public XboxInput Input { get; } = input;
    public string Label => Labels.Of(Input);

    public DsOutput Output
    {
        get => _output;
        set { if (Set(ref _output, value)) edited(); }
    }

    /// <summary>Lit while the physical button is down.</summary>
    public bool IsHeld
    {
        get => _isHeld;
        set => Set(ref _isHeld, value);
    }

    /// <summary>Sets the value from a loaded profile without counting as an edit.</summary>
    internal void Load(DsOutput output)
    {
        _output = output;
        OnPropertyChanged(nameof(Output));
    }
}

/// <summary>One combo rule on the Combos tab.</summary>
public sealed class ComboViewModel(Action edited) : ObservableObject
{
    private XboxInput _first, _second;
    private DsOutput _output;
    private bool _isActive;

    public XboxInput First
    {
        get => _first;
        set { if (Set(ref _first, value)) { edited(); OnPropertyChanged(nameof(Problem)); } }
    }

    public XboxInput Second
    {
        get => _second;
        set { if (Set(ref _second, value)) { edited(); OnPropertyChanged(nameof(Problem)); } }
    }

    public DsOutput Output
    {
        get => _output;
        set { if (Set(ref _output, value)) edited(); }
    }

    public bool IsActive
    {
        get => _isActive;
        set => Set(ref _isActive, value);
    }

    /// <summary>Shown next to a rule that can't work.</summary>
    public string Problem => First == Second ? "Pick two different buttons." : "";

    internal static ComboViewModel From(Combo c, Action edited)
    {
        var vm = new ComboViewModel(edited) { _first = c.First, _second = c.Second, _output = c.Output };
        return vm;
    }

    internal Combo ToCombo() => new(First, Second, Output);
}
