using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace NewApiOrb.Models;

/// <summary>设置面板里的一行字段：可勾选、可上下移动。</summary>
public sealed class FieldItem : INotifyPropertyChanged
{
    public string Key { get; init; } = "";
    public string Label { get; init; } = "";
    public string Hint { get; init; } = "";

    private bool _checked;
    public bool Checked
    {
        get => _checked;
        set
        {
            if (_checked == value) return;
            _checked = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
