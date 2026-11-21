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

/// <summary>One row on the Keyboard &amp; mouse tab: a DualSense button or stick direction and its key.</summary>
public sealed class KbmRowViewModel(KbmAction action) : ObservableObject
{
    private string _key = "";
    private bool _isListening;

    public KbmAction Action { get; } = action;

    /// <summary>For the shared glyph + name template.</summary>
    public Option<DsOutput> Display { get; } = new(Labels.OutputOf(action), Labels.Of(action));

    /// <summary>A <see cref="KeyNames"/> name, or "" when unbound.</summary>
    public string Key
    {
        get => _key;
        set
        {
            if (!Set(ref _key, value ?? "")) return;
            OnPropertyChanged(nameof(KeyText));
            OnPropertyChanged(nameof(HasKey));
        }
    }

    public bool IsListening
    {
        get => _isListening;
        set { if (Set(ref _isListening, value)) OnPropertyChanged(nameof(KeyText)); }
    }

    public bool HasKey => _key.Length > 0;

    public string KeyText => IsListening ? "Press a key…" : _key.Length == 0 ? "Not set" : KeyNames.Display(_key);
}
