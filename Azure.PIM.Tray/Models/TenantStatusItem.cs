using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Azure.PIM.Tray.Models;

public class TenantStatusItem : INotifyPropertyChanged
{
    private string _status = "Idle";

    public string TenantId   { get; init; } = "";
    public string TenantName { get; init; } = "";

    public string Status
    {
        get => _status;
        set { _status = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
