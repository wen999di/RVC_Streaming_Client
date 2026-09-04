using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ClientAvalonia.Models;

public sealed class TrainingSpeakerGroup : INotifyPropertyChanged
{
    private string _name;
    private bool _isExpanded;

    public TrainingSpeakerGroup(string name)
    {
        _name = name;
        Files.CollectionChanged += Files_OnCollectionChanged;
    }

    public ObservableCollection<TrainingAudioItem> Files { get; } = new();

    public string Name
    {
        get => _name;
        set
        {
            if (_name == value) return;
            _name = value;
            foreach (var file in Files)
            {
                file.Speaker = value;
            }
            OnPropertyChanged();
        }
    }

    public string FileCountText => $"{Files.Count} 个音频";

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ExpandRotation));
        }
    }

    public double ExpandRotation => IsExpanded ? 90.0 : 0.0;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Files_OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(FileCountText));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
