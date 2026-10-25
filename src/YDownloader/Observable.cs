using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace YDownloader;

/// <summary>Basis für Objekte, an die die Oberfläche bindet.</summary>
public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>Für Eigenschaften, die viermal pro Sekunde wechseln: vorab angelegte Argumente, keine Allokation.</summary>
    protected void Raise(PropertyChangedEventArgs args) => PropertyChanged?.Invoke(this, args);

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }
}
