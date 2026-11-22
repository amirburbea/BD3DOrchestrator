using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace BD3DOrchestrator;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetField<TValue>(ref TValue field, TValue value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<TValue>.Default.Equals(field, value))
        {
            return false;
        }
        field = value;
        this.PropertyChanged?.Invoke(this, new(propertyName));
        return true;
    }
}
