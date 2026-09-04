using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ClientAvalonia.Models;

public sealed class ServerFileItem : INotifyPropertyChanged
{
    private string _name = string.Empty;
    private long _size;
    private DateTimeOffset _modifiedAt;
    private bool _isUploading;
    private long _sentBytes;
    private long _totalBytes;
    private string _status = string.Empty;
    private bool _isVoiceModelPth;
    private string _voiceModelTooltip = string.Empty;
    private bool _isEditing;
    private string _editingName = string.Empty;
    private bool _isUploadFolder;
    private bool _isExpanded;
    private bool _uploadCompleted;
    private bool _uploadFailed;
    private bool _isFolder;
    private bool _isModelRootFolder;
    private string _displayName = string.Empty;
    private string _parentPath = string.Empty;
    private double _treeIndent;
    private int _childCount;
    private double _treeOpacity = 1.0;
    private double _treeOffsetY;

    public ObservableCollection<ServerFileItem> UploadChildren { get; } = new();

    public ServerFileItem? UploadParent { get; set; }

    public string DisplayName
    {
        get => string.IsNullOrWhiteSpace(_displayName) ? Name : _displayName;
        set
        {
            _displayName = value;
            OnPropertyChanged();
        }
    }

    public string ParentPath
    {
        get => _parentPath;
        set
        {
            _parentPath = value;
            OnPropertyChanged();
        }
    }

    public double TreeIndent
    {
        get => _treeIndent;
        set
        {
            _treeIndent = value;
            OnPropertyChanged();
        }
    }

    public bool IsFolder
    {
        get => _isFolder;
        set
        {
            _isFolder = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanExpand));
            OnPropertyChanged(nameof(ShowRegularFolderIcon));
            OnPropertyChanged(nameof(ExpandGlyph));
            OnPropertyChanged(nameof(DetailText));
            OnPropertyChanged(nameof(SizeText));
            OnPropertyChanged(nameof(ModifiedText));
        }
    }

    public bool IsModelRootFolder
    {
        get => _isModelRootFolder;
        set
        {
            _isModelRootFolder = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowRegularFolderIcon));
        }
    }

    public bool ShowRegularFolderIcon => CanExpand && !IsModelRootFolder;

    public int ChildCount
    {
        get => _childCount;
        set
        {
            _childCount = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DetailText));
            OnPropertyChanged(nameof(ModifiedText));
        }
    }

    public double TreeOpacity
    {
        get => _treeOpacity;
        set
        {
            _treeOpacity = value;
            OnPropertyChanged();
        }
    }

    public double TreeOffsetY
    {
        get => _treeOffsetY;
        set
        {
            _treeOffsetY = value;
            OnPropertyChanged();
        }
    }

    public string Name
    {
        get => _name;
        set
        {
            _name = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DisplayName));
            OnPropertyChanged(nameof(DetailText));
            UploadParent?.RefreshFolderProgress();
        }
    }

    public long Size
    {
        get => _size;
        set
        {
            _size = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DetailText));
            OnPropertyChanged(nameof(SizeText));
            UploadParent?.RefreshFolderProgress();
        }
    }

    public DateTimeOffset ModifiedAt
    {
        get => _modifiedAt;
        set
        {
            _modifiedAt = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DetailText));
            OnPropertyChanged(nameof(ModifiedText));
            UploadParent?.RefreshFolderProgress();
        }
    }

    public bool IsUploading
    {
        get => _isUploading;
        set
        {
            _isUploading = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DetailText));
            OnPropertyChanged(nameof(ModifiedText));
            UploadParent?.RefreshFolderProgress();
        }
    }

    public long SentBytes
    {
        get => _sentBytes;
        set
        {
            _sentBytes = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Progress));
            OnPropertyChanged(nameof(DetailText));
            UploadParent?.RefreshFolderProgress();
        }
    }

    public long TotalBytes
    {
        get => _totalBytes;
        set
        {
            _totalBytes = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Progress));
            OnPropertyChanged(nameof(DetailText));
            UploadParent?.RefreshFolderProgress();
        }
    }

    public bool IsUploadFolder
    {
        get => _isUploadFolder;
        set
        {
            _isUploadFolder = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ExpandGlyph));
            OnPropertyChanged(nameof(ExpandRotation));
            OnPropertyChanged(nameof(ShowUploadChildren));
            OnPropertyChanged(nameof(DetailText));
            OnPropertyChanged(nameof(CanExpand));
            OnPropertyChanged(nameof(ShowRegularFolderIcon));
        }
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            _isExpanded = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ExpandGlyph));
            OnPropertyChanged(nameof(ExpandRotation));
            OnPropertyChanged(nameof(ShowUploadChildren));
        }
    }

    public bool UploadCompleted
    {
        get => _uploadCompleted;
        set
        {
            _uploadCompleted = value;
            OnPropertyChanged();
            UploadParent?.RefreshFolderProgress();
        }
    }

    public bool UploadFailed
    {
        get => _uploadFailed;
        set
        {
            _uploadFailed = value;
            OnPropertyChanged();
            UploadParent?.RefreshFolderProgress();
        }
    }

    public bool CanExpand => IsFolder || IsUploadFolder;

    public string ExpandGlyph => IsExpanded ? "▾" : "▸";

    public double ExpandRotation => IsExpanded ? 90.0 : 0.0;

    public bool ShowUploadChildren => IsUploadFolder && IsExpanded;

    public string Status
    {
        get => _status;
        set
        {
            _status = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DetailText));
            OnPropertyChanged(nameof(ModifiedText));
        }
    }

    public bool IsVoiceModelPth
    {
        get => _isVoiceModelPth;
        set
        {
            _isVoiceModelPth = value;
            OnPropertyChanged();
        }
    }

    public string VoiceModelTooltip
    {
        get => _voiceModelTooltip;
        set
        {
            _voiceModelTooltip = value;
            OnPropertyChanged();
        }
    }

    public double Progress
    {
        get
        {
            if (TotalBytes > 0)
            {
                return Math.Clamp((double)SentBytes / TotalBytes, 0, 1);
            }
            if (IsUploadFolder && UploadChildren.Count > 0)
            {
                var finished = 0;
                foreach (var child in UploadChildren)
                {
                    if (child.UploadCompleted || child.UploadFailed) finished++;
                }
                return (double)finished / UploadChildren.Count;
            }
            return 0;
        }
    }

    public string DetailText
    {
        get
        {
            if (IsFolder)
            {
                return $"{ChildCount} 个文件";
            }
            if (IsUploading)
            {
                if (IsUploadFolder)
                {
                    var completed = 0;
                    var failed = 0;
                    foreach (var child in UploadChildren)
                    {
                        if (child.UploadCompleted) completed++;
                        if (child.UploadFailed) failed++;
                    }
                    var failedText = failed > 0 ? $"，失败 {failed}" : string.Empty;
                    var bytesText = TotalBytes > 0
                        ? $" · {FormatBytes(SentBytes)}/{FormatBytes(TotalBytes)}"
                        : string.Empty;
                    return $"{Status} · {completed}/{UploadChildren.Count} 个文件{failedText}{bytesText}";
                }
                var percent = (int)Math.Round(Progress * 100);
                return $"{Status}  {FormatBytes(SentBytes)}/{FormatBytes(TotalBytes)} ({percent}%)";
            }

            if (ModifiedAt > DateTimeOffset.MinValue)
            {
                return $"{FormatBytes(Size)}  {ModifiedAt:yyyy-MM-dd HH:mm:ss}";
            }

            return !string.IsNullOrWhiteSpace(Status) ? Status : FormatBytes(Size);
        }
    }

    public bool IsEditing { get => _isEditing; set { _isEditing = value; OnPropertyChanged(); } }

    public string EditingName { get => _editingName; set { _editingName = value; OnPropertyChanged(); } }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void RefreshFolderProgress()
    {
        if (!IsUploadFolder) return;
        long sent = 0;
        long total = 0;
        foreach (var child in UploadChildren)
        {
            sent += child.SentBytes;
            total += child.TotalBytes;
        }
        _sentBytes = sent;
        _totalBytes = total;
        OnPropertyChanged(nameof(SentBytes));
        OnPropertyChanged(nameof(TotalBytes));
        OnPropertyChanged(nameof(Progress));
        OnPropertyChanged(nameof(DetailText));
    }

    public string SizeText => IsFolder ? "—" : FormatBytes(Size);

    public string ModifiedText
    {
        get
        {
            if (IsFolder)
            {
                return $"{ChildCount} 个文件";
            }

            if (ModifiedAt > DateTimeOffset.MinValue)
            {
                return ModifiedAt.ToString("yyyy-MM-dd HH:mm:ss");
            }

            return Status;
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes} B";
        }

        double value = bytes;
        string[] units = ["KB", "MB", "GB", "TB"];
        int unitIndex = -1;
        do
        {
            value /= 1024;
            unitIndex++;
        } while (value >= 1024 && unitIndex < units.Length - 1);

        return $"{value:0.##} {units[unitIndex]}";
    }
}
