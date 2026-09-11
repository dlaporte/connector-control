using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ConnectorControl.Core.State;

/// <summary>The Mac's ObservableObject/@Published, in WPF terms: INotifyPropertyChanged without a framework.</summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }
        field = value;
        Raise(propertyName);
        return true;
    }

    protected void Raise([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    /// <summary>WPF re-reads every binding when the property name is empty.</summary>
    protected void RaiseAll() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));

    /// <summary>
    /// Whether a PropertyChanged notification is one a listener watching only <paramref name="name"/>
    /// needs to react to: that property, or a RaiseAll (empty/null name means everything changed).
    /// Public so a class in another assembly that is not itself an ObservableObject (TrayController)
    /// can share it too.
    /// </summary>
    public static bool Affects(PropertyChangedEventArgs e, string name) =>
        string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == name;
}
