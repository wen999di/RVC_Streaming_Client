using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Media;

namespace ClientAvalonia.Models;

public sealed class VoiceModelItem : INotifyPropertyChanged
{
    public const string RawId = "__raw__";
    public const string ServerRawId = "__server_raw__";

    private string _id = string.Empty;
    private string _name = string.Empty;
    private string _pth = string.Empty;
    private string _index = string.Empty;
    private bool _isActive;
    private IBrush _statusBrush = Brushes.Gray;
    private string _statusHint = "未加载到显存";
    private bool _showStatusDot = true;
    private bool _isUserSelected;

    public string Id
    {
        get => _id;
        set
        {
            _id = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DetailText));
        }
    }

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

    public string Pth
    {
        get => _pth;
        set
        {
            _pth = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DetailText));
        }
    }

    public string Index
    {
        get => _index;
        set
        {
            _index = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DetailText));
        }
    }

    public bool IsActive
    {
        get => _isActive;
        set
        {
            _isActive = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DetailText));
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

    public bool ShowStatusDot
    {
        get => _showStatusDot;
        set
        {
            _showStatusDot = value;
            OnPropertyChanged();
        }
    }

    public bool IsUserSelected
    {
        get => _isUserSelected;
        set
        {
            _isUserSelected = value;
            OnPropertyChanged();
        }
    }

    public string DetailText
    {
        get
        {
            if (string.Equals(Id, RawId, StringComparison.Ordinal))
            {
                return "直接输出本地麦克风音频，不经过服务器";
            }

            if (string.Equals(Id, ServerRawId, StringComparison.Ordinal))
            {
                return "输出原声但经过服务器通路（调试用，不经过模型）";
            }

            var indexText = string.IsNullOrWhiteSpace(Index) ? "index: (无)" : $"index: {Index}";
            return $"pth: {Pth}    {indexText}".Trim();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}