using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Xbox2Ps.ViewModels;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}

/// <summary>A dropdown entry: the value and the text shown for it.</summary>
public sealed record Option<T>(T Value, string Label)
{
    /// <summary>What screen readers (and UI Automation) read for a dropdown entry.</summary>
    public override string ToString() => Label;
}

/// <summary>Traffic-light state for the header's status dots.</summary>
public enum StatusLevel { Off, Ok, Warn, Bad }
