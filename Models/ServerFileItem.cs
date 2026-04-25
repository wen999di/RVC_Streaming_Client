using System;
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

    public string Name
    {
        get => _name;
        set
        {
            _name = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DetailText));
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
        }
    }

    public string Status
    {
        get => _status;
        set
        {
            _status = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DetailText));
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

    public double Progress => TotalBytes > 0 ? Math.Clamp((double)SentBytes / TotalBytes, 0, 1) : 0;

    public string DetailText
    {
        get
        {
            if (IsUploading)
            {
                var percent = (int)Math.Round(Progress * 100);
                return $"{Status}  {FormatBytes(SentBytes)}/{FormatBytes(TotalBytes)} ({percent}%)";
            }

            if (Size > 0)
            {
                return $"{FormatBytes(Size)}  {ModifiedAt:yyyy-MM-dd HH:mm:ss}";
            }

            return Status;
        }
    }

    public bool IsEditing { get => _isEditing; set { _isEditing = value; OnPropertyChanged(); } }

    public string EditingName { get => _editingName; set { _editingName = value; OnPropertyChanged(); } }

    public event PropertyChangedEventHandler? PropertyChanged;

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