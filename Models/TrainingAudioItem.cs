using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ClientAvalonia.Models;

public sealed class TrainingAudioItem : INotifyPropertyChanged
{
    private bool _isSelected;
    private string _speaker = "默认说话人";
    private string _detailText = string.Empty;

    public string Name { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;

    public string DetailText
    {
        get => _detailText;
        set
        {
            if (_detailText == value) return;
            _detailText = value;
            OnPropertyChanged();
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            OnPropertyChanged();
        }
    }

    public string Speaker
    {
        get => _speaker;
        set
        {
            if (_speaker == value) return;
            _speaker = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
