using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ClientAvalonia.Models;

public sealed record HubRepositoryFile(string Path, long Size, string Oid);

public sealed record HubRepositorySnapshot(
    string Provider,
    string RepoId,
    string Revision,
    string DefaultDestination,
    long TotalBytes,
    IReadOnlyList<HubRepositoryFile> Files);

public sealed record HubDownloadRequest(
    string Provider,
    string RepoId,
    string Revision,
    string Destination,
    IReadOnlyList<string> Paths);

public sealed record HubDownloadProgress(
    string Path,
    int FileIndex,
    int FileCount,
    long CompletedBytes,
    long TotalBytes,
    string State);

public sealed record HubDownloadResult(
    string Destination,
    IReadOnlyList<string> Files,
    long TotalBytes);

public sealed class HubRepositoryNode : INotifyPropertyChanged
{
    private bool _isSelected;
    private bool _suppressSelectionPropagation;
    private bool _isExpanded;
    private double _treeIndent;
    private double _treeOpacity = 1.0;
    private double _treeOffsetY;

    public string Name { get; init; } = string.Empty;
    public string Path { get; init; } = string.Empty;
    public bool IsFolder { get; init; }
    public long Size { get; set; }
    public HubRepositoryNode? Parent { get; init; }
    public ObservableCollection<HubRepositoryNode> Children { get; } = new();

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

    public bool CanExpand => IsFolder && Children.Count > 0;

    public double ExpandRotation => IsExpanded ? 90.0 : 0.0;

    public double TreeIndent
    {
        get => _treeIndent;
        set
        {
            if (Math.Abs(_treeIndent - value) < 0.01) return;
            _treeIndent = value;
            OnPropertyChanged();
        }
    }

    public double TreeOpacity
    {
        get => _treeOpacity;
        set
        {
            if (Math.Abs(_treeOpacity - value) < 0.001) return;
            _treeOpacity = value;
            OnPropertyChanged();
        }
    }

    public double TreeOffsetY
    {
        get => _treeOffsetY;
        set
        {
            if (Math.Abs(_treeOffsetY - value) < 0.001) return;
            _treeOffsetY = value;
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
            if (_suppressSelectionPropagation) return;

            foreach (var child in Children)
            {
                child.SetSelectedFromParent(value);
            }
            Parent?.RefreshSelectionFromChildren();
        }
    }

    public string DetailText => IsFolder
        ? $"{CountFiles()} 个文件 · {FormatBytes(Size)}"
        : FormatBytes(Size);

    public event PropertyChangedEventHandler? PropertyChanged;

    public IEnumerable<HubRepositoryNode> EnumerateFiles()
    {
        if (!IsFolder)
        {
            yield return this;
            yield break;
        }

        foreach (var child in Children)
        {
            foreach (var file in child.EnumerateFiles())
            {
                yield return file;
            }
        }
    }

    private int CountFiles() => Children.Sum(child => child.IsFolder ? child.CountFiles() : 1);

    private void SetSelectedFromParent(bool value)
    {
        _suppressSelectionPropagation = true;
        try
        {
            _isSelected = value;
            OnPropertyChanged(nameof(IsSelected));
            foreach (var child in Children)
            {
                child.SetSelectedFromParent(value);
            }
        }
        finally
        {
            _suppressSelectionPropagation = false;
        }
    }

    private void RefreshSelectionFromChildren()
    {
        if (Children.Count == 0) return;
        _suppressSelectionPropagation = true;
        try
        {
            var selected = Children.All(child => child.IsSelected);
            if (_isSelected != selected)
            {
                _isSelected = selected;
                OnPropertyChanged(nameof(IsSelected));
            }
        }
        finally
        {
            _suppressSelectionPropagation = false;
        }
        Parent?.RefreshSelectionFromChildren();
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024L * 1024) return $"{bytes / 1024d:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024d * 1024):F2} MB";
        return $"{bytes / (1024d * 1024 * 1024):F2} GB";
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
