using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Media;

namespace ClientAvalonia.Models;

public sealed class SlotBindingItem : INotifyPropertyChanged
{
    private string _slot = string.Empty;
    private string _fileName = string.Empty;
    private bool _isActive;
    private IBrush _statusBrush = Brushes.Gray;
    private string _statusHint = string.Empty;

    public string Slot
    {
        get => _slot;
        set
        {
            _slot = value;
            OnPropertyChanged();
        }
    }

    public string FileName
    {
        get => _fileName;
        set
        {
            _fileName = value;
            OnPropertyChanged();
        }
    }

    public bool IsActive
    {
        get => _isActive;
        set
        {
            _isActive = value;
            OnPropertyChanged();
        }
    }

    public IBrush StatusBrush
    {
        get => _statusBrush;
        set
        {
            _statusBrush = value;
            OnPropertyChanged();
        }
    }

    public string StatusHint
    {
        get => _statusHint;
        set
        {
            _statusHint = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}