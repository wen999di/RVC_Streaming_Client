using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Linq;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ClientAvalonia.Dialogs;
using ClientAvalonia.Models;
using ClientAvalonia.Services;
using Material.Icons;
using Material.Icons.Avalonia;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace ClientAvalonia;

public partial class MainWindow : Window
{
    private enum ModelState
    {
        NotReady,
        Loading,
        Ready,
        Error,
    }

    private sealed class LatencySample
    {
        public long TsNs { get; init; }
        public double TotalMs { get; init; }
        public double RttMs { get; init; }
        public double InferMs { get; init; }
    }

    private const int SampleRate = 16000;
    private const int Channels = 1;
    private const long NsPerSample = 1_000_000_000L / SampleRate;
    private const double LatencySampleWindowSeconds = 10.0;

    private readonly RvcClientService _client = new();
    private readonly ObservableCollection<VoiceModelItem> _voiceModelsSelection = new();
    private readonly ObservableCollection<VoiceModelItem> _voiceModelsManagement = new();
    private readonly ObservableCollection<ServerFileItem> _serverFiles = new();
    private readonly ObservableCollection<AudioDeviceItem> _audioInputDevices = new();
    private readonly ObservableCollection<AudioDeviceItem> _audioOutputDevices = new();
    private readonly ObservableCollection<LogFileItem> _serverLogFiles = new();
    private readonly ObservableCollection<SlotBindingItem> _hubertSlotItems = new();
    private readonly ObservableCollection<SlotBindingItem> _rmvpeSlotItems = new();
    private string _inlinePendingPth = string.Empty;
    private string _inlinePendingIndex = string.Empty;
    private readonly VoiceModelItem _rawVoiceModelItem = new() { Id = VoiceModelItem.RawId, Name = "输出原声", Pth = string.Empty, Index = string.Empty, IsActive = false, ShowStatusDot = false };
    private readonly VoiceModelItem _serverRawVoiceModelItem = new() { Id = VoiceModelItem.ServerRawId, Name = "输出原声(经服务器)", Pth = string.Empty, Index = string.Empty, IsActive = false, ShowStatusDot = false };
    private readonly JitterEstimator _jitterEstimator = new();
    private readonly ConcurrentQueue<byte[]> _audioSendQueue = new();
    private readonly SemaphoreSlim _audioSendSignal = new(0);
    private readonly List<LatencySample> _latencySamples = new();
    private readonly object _captureLock = new();
    private readonly List<ServerFileItem> _serverFilesRaw = new();
    private readonly List<ServerFileItem> _uploadingFiles = new();
    private readonly Dictionary<string, ServerFileItem> _serverFileCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _boundFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> _slotAllowedExt = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ServerFileItem> _uploadItemsById = new();
    private readonly ConcurrentDictionary<string, long> _uploadOffsetCorrections = new();
    private readonly SemaphoreSlim _uploadSerialLock = new(1, 1);

    private bool _suppressSlotSelectionChanged;
    private string? _selectedVoiceModelId;
    private string? _prevSelectedVoiceModelId;
    private bool _debugMode;
    private int _f12Count;
    private DateTime _lastF12Time;
    private Control? _lastHovered;
    private Point _dragStartPoint;
    private bool _dragStarted;
    private List<string>? _dragCandidates;
    private string? _selectedInputDeviceId;
    private string? _selectedOutputDeviceId;
    private string _fileSortMode = "time_desc";
    private bool _hideBoundFiles;
    private string _recentUnloadedVoiceModelId = string.Empty;
    private string? _pendingPreloadModelId;
    private readonly HashSet<string> _failedVoiceModelIds = new(StringComparer.Ordinal);
    private string _modelPath = string.Empty;
    private string _indexPath = string.Empty;
    private int _f0UpKey;
    private float _blockTime = 0.25f;
    private float _crossfadeLength = 0.04f;
    private float _extraTime = 2.0f;
    private int _serverStreamChunkMs = 20;
    private float _formantShift;
    private string _f0Method = "rmvpe";
    private float _indexRate = 0.5f;
    private float _silenceDbThreshold = -70.0f;
    private float _silenceGateAtten;
    private bool _inputNoiseReduce;
    private bool _outputNoiseReduce;
    private float _noiseReducePropDecrease = 0.9f;
    private float _rmsMixRate = 0.8f;

    private readonly Dictionary<string, object> _lastSentConfig = new();
    private long _configSeq;
    private long _lastSentConfigSeq;
    private DispatcherTimer? _realtimeConfigDebounceTimer;
    private int _realtimeConfigDebouncePending;

    private bool _useAdaptiveBuffer = true;
    private int _targetBufferLatency = 40;
    private int _maxBufferMs = 1000;
    private int _bufferCapacityMs = 5000;
    private int _networkSliceMs = 20;
    private int _silenceDropOffset = 20;
    private float _silenceThreshold = 0.005f;

    private WasapiCapture? _waveIn;
    private BufferedWaveProvider? _captureBuffer;
    private IWaveProvider? _captureProvider;
    private byte[] _captureReadBuffer = Array.Empty<byte>();
    private BufferedWaveProvider? _waveProvider;
    private IWavePlayer? _waveOut;
    private MMDevice? _outputDevice;
    private CancellationTokenSource? _streamingCts;
    private Task? _audioSendLoopTask;
    private int _audioSendQueueCount;
    private int _maxAudioSendQueuePackets = 25;
    private long _lastSendDropLogNs;
    private TaskCompletionSource<(string UploadId, string Name, long ReceivedBytes, long TotalBytes)>? _uploadReadyTcs;
    private TaskCompletionSource<(string UploadId, string FinalName)>? _uploadDoneTcs;

    private long _monoBaseTimestamp;
    private long _lastSentAudioTsNs;
    private long _streamStartNs;
    private int _streamSessionId;
    private double _emaTotalLatencyMs;
    private double _emaInferLatencyMs;
    private double _emaQueueLatencyMs;
    private const double LatencyEmaAlpha = 0.2;
    private bool _isPlaying;
    private bool _playbackStarted;
    private bool _bypassServerVoice;
    private bool _serverPassthroughVoice;

    // 波形显示
    // 输入：WASAPI 回调直接写 RMS（简单高效）
    // 输入/输出波形：各自使用原始音频环形缓冲 + 同一定时器消费，保证同步滚动
    // 输入侧：定时器消费时跳过积压，始终读最新数据，消除延迟感
    private readonly float[] _waveformInput = new float[400];
    private readonly float[] _waveformOutput = new float[400];
    private int _waveformInPos;
    private int _waveformOutPos;
    private readonly float[] _waveformAudioInBuf = new float[16000 * 4];  // 4秒输入原始音频（跳过积压）
    private int _waveformAudioInWritePos;
    private int _waveformAudioInReadPos;
    private readonly object _waveformAudioInLock = new();
    private readonly float[] _waveformAudioOutBuf = new float[16000 * 8]; // 8秒输出原始音频
    private int _waveformAudioOutWritePos;
    private int _waveformAudioOutReadPos;
    private readonly object _waveformAudioOutLock = new();
    private double _waveformMaxIn = 0.001;
    private double _waveformMaxOut = 0.001;
    private DispatcherTimer? _waveformTimer;
    private ModelState _modelState = ModelState.NotReady;
    private bool _uiInitialized;

    // 自定义页签头横条动画
    private TranslateTransform? _mainTabUnderlineTransform;
    private DispatcherTimer? _mainTabUnderlineTimer;
    private double _mainTabUnderlineFromX;
    private double _mainTabUnderlineToX;
    private double _mainTabUnderlineFromWidth;
    private double _mainTabUnderlineToWidth;
    private long _mainTabUnderlineStartTick;
    private const double MainTabUnderlineAnimMs = 220.0;

    public MainWindow()
    {
        Program.AppendStartupTrace("MainWindow: ctor enter");
        InitializeComponent();
        KeyDown += MainWindow_KeyDown;
        MainTabControl.SelectionChanged += OnMainTabControlSelectionChanged;
        Opened += (_, _) => Dispatcher.UIThread.Post(() => UpdateMainTabHeaderVisual(false), DispatcherPriority.Loaded);
        MainTabsHeaderGrid.SizeChanged += (_, _) => UpdateMainTabHeaderVisual(false);
        AddHandler(InputElement.PointerPressedEvent, GlobalPointerPressed_CommitSliderEdit, RoutingStrategies.Tunnel);
        PointerMoved += TrackHover;
        PointerExited += (_, _) => { _lastHovered?.Classes.Remove("hover"); _lastHovered = null; };
        Program.AppendStartupTrace("MainWindow: InitializeComponent completed");

        // Set up drag-drop handlers for slot borders
        foreach (var border in new[] { HubertSlotBorder, RmvpeSlotBorder })
        {
            border.AddHandler(DragDrop.DragOverEvent, SlotBorder_DragOver);
            border.AddHandler(DragDrop.DropEvent, SlotBorder_Drop);
            border.AddHandler(DragDrop.DragLeaveEvent, SlotBorder_DragLeave);
        }

        InlinePthDropBorder.AddHandler(DragDrop.DragOverEvent, InlinePthBorder_DragOver);
        InlinePthDropBorder.AddHandler(DragDrop.DropEvent, InlinePthBorder_Drop);
        InlinePthDropBorder.AddHandler(DragDrop.DragLeaveEvent, (_, _) => SetSlotHighlight(InlinePthDropBorder, false));
        InlineIndexDropBorder.AddHandler(DragDrop.DragOverEvent, InlineIndexBorder_DragOver);
        InlineIndexDropBorder.AddHandler(DragDrop.DropEvent, InlineIndexBorder_Drop);
        InlineIndexDropBorder.AddHandler(DragDrop.DragLeaveEvent, (_, _) => SetSlotHighlight(InlineIndexDropBorder, false));

        // Smooth corner / border transition when OS maximizes or restores
        PropertyChanged += (_, e) =>
        {
            if (e.Property != WindowStateProperty) return;
            bool isMax = WindowState == WindowState.Maximized;
            OuterBorder.CornerRadius = isMax ? new CornerRadius(0) : new CornerRadius(8);
            OuterBorder.BorderThickness = isMax ? new Thickness(0) : new Thickness(1);
            MaximizeIcon.IsVisible = !isMax;
            RestoreIcon.IsVisible = isMax;
        };

        _monoBaseTimestamp = Stopwatch.GetTimestamp();

        VoiceModelChipsControl.ItemsSource = _voiceModelsSelection;
        VoiceModelManagementListBox.ItemsSource = _voiceModelsManagement;
        ServerFilesListBox.ItemsSource = _serverFiles;
        InputDeviceComboBox.ItemsSource = _audioInputDevices;
        OutputDeviceComboBox.ItemsSource = _audioOutputDevices;
        ServerLogFilesComboBox.ItemsSource = _serverLogFiles;
        HubertSlotListBox.ItemsSource = _hubertSlotItems;
        RmvpeSlotListBox.ItemsSource = _rmvpeSlotItems;
        FileSortComboBox.SelectedIndex = 0;

        _client.LogReceived += Client_OnLogReceived;
        _client.ConnectionStateChanged += Client_OnConnectionStateChanged;
        _client.TextMessageReceived += Client_OnTextMessageReceived;
    _client.BinaryMessageReceived += Client_OnBinaryMessageReceived;

    _realtimeConfigDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
    _realtimeConfigDebounceTimer.Tick += async (_, _) => await FlushRealtimeConfigAsync();
        Program.AppendStartupTrace("MainWindow: debounce timer prepared");

        SeedPreviewData();
        Program.AppendStartupTrace("MainWindow: preview data seeded");
        InitializeSettingsUi();
        _uiInitialized = true;
        Program.AppendStartupTrace("MainWindow: settings initialized");
        RefreshAudioDevices();
        Program.AppendStartupTrace("MainWindow: audio devices refreshed");
        UpdateConnectionUi(false);
        Program.AppendStartupTrace("MainWindow: connection UI initialized");
        Log("Avalonia 客户端已启动。");
        Log("等待连接服务器。");

        // Persistent waveform timer — runs always so the waveform keeps scrolling even when idle
        StartWaveformTimer();

        Program.AppendStartupTrace("MainWindow: ctor completed");
    }

    private void SeedPreviewData()
    {
        _voiceModelsSelection.Clear();
        _voiceModelsSelection.Add(_rawVoiceModelItem);
        // ServerRaw hidden until debug mode (F12 × 5)
        _selectedVoiceModelId = VoiceModelItem.RawId;
        _bypassServerVoice = true; // default to bypass mode (matches initial UI selection)
    }

    private void InitializeSettingsUi()
    {
        F0UpKeySlider.Value = _f0UpKey;
        IndexRateSlider.Value = _indexRate;
        FormantSlider.Value = _formantShift;
        BlockTimeSlider.Value = _blockTime * 1000f;
        CrossfadeSlider.Value = _crossfadeLength * 1000f;
        ExtraTimeSlider.Value = _extraTime * 1000f;
        ServerStreamChunkSlider.Value = _serverStreamChunkMs;
        SilenceDbSlider.Value = _silenceDbThreshold;
        SilenceGateAttenSlider.Value = _silenceGateAtten;
        NoiseReduceStrengthSlider.Value = _noiseReducePropDecrease;
        RmsMixRateSlider.Value = _rmsMixRate;
        InputNoiseReduceSwitch.IsChecked = _inputNoiseReduce;
        OutputNoiseReduceSwitch.IsChecked = _outputNoiseReduce;
        TargetBufferSlider.Value = _targetBufferLatency;
        MaxBufferSlider.Value = _maxBufferMs;
        BufferCapacitySlider.Value = _bufferCapacityMs;
        NetworkSliceSlider.Value = _networkSliceMs;
        JitterFactorSlider.Value = _jitterEstimator.JitterFactor;
        JitterAlphaSlider.Value = _jitterEstimator.Alpha;
        JitterMaxBufferSlider.Value = _jitterEstimator.MaxBufferMs;
        MinBufferSlider.Value = _jitterEstimator.MinBufferMs;
        SetSegmentedToggle(AutoBufferBtn, _useAdaptiveBuffer);
        SetSegmentedToggle(ManualBufferBtn, !_useAdaptiveBuffer);
        SetAnimatedVisibility(AutoBufferPanel, _useAdaptiveBuffer);
        SetAnimatedVisibility(ManualBufferPanel, !_useAdaptiveBuffer);
        SetSegmentedToggle(F0RmvpeBtn, _f0Method == "rmvpe");
        SetSegmentedToggle(F0FcpeBtn, _f0Method == "fcpe");
        SetAnimatedVisibility(SyncErrorPanel, false);
        RefreshSliderValueTexts();
        UpdateBlockTimeValidationUi();
    }

    private void RefreshSliderValueTexts()
    {
        F0UpKeyValueText.Text = $"{F0UpKeySlider.Value:F0}";
        IndexRateValueText.Text = IndexRateSlider.Value.ToString("0.00");
        FormantValueText.Text = FormantSlider.Value.ToString("0.00");
        BlockTimeValueText.Text = $"{BlockTimeSlider.Value:F0} ms";
        CrossfadeValueText.Text = $"{CrossfadeSlider.Value:F0} ms";
        ExtraTimeValueText.Text = $"{ExtraTimeSlider.Value:F0} ms";
        ServerStreamChunkValueText.Text = $"{ServerStreamChunkSlider.Value:F0} ms";
        SilenceDbValueText.Text = $"{SilenceDbSlider.Value:F0} dB";
        SilenceGateAttenValueText.Text = SilenceGateAttenSlider.Value.ToString("0.00");
        NoiseReduceStrengthValueText.Text = NoiseReduceStrengthSlider.Value.ToString("0.00");
        RmsMixRateValueText.Text = RmsMixRateSlider.Value.ToString("0.00");

        JitterFactorValueText.Text = JitterFactorSlider.Value.ToString("0.0");
        JitterAlphaValueText.Text = JitterAlphaSlider.Value.ToString("0.00");
        JitterMaxBufferValueText.Text = $"{JitterMaxBufferSlider.Value:F0} ms";
        MinBufferValueText.Text = $"{MinBufferSlider.Value:F0} ms";
        TargetBufferValueText.Text = $"{TargetBufferSlider.Value:F0} ms";
        MaxBufferValueText.Text = $"{MaxBufferSlider.Value:F0} ms";
        BufferCapacityValueText.Text = $"{BufferCapacitySlider.Value:F0} ms";
        NetworkSliceValueText.Text = $"{NetworkSliceSlider.Value:F0} ms";
    }

    private static void SetAnimatedVisibility(Control control, bool isVisible)
    {
        control.Classes.Set("collapsed", !isVisible);
        control.IsEnabled = isVisible;
        control.IsHitTestVisible = isVisible;
    }

    // ── 自定义页签头横条动画 ─────────────────────────────────────────────────────────

    private static double EaseInOut(double t) =>
        t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;

    private Button? GetMainTabHeaderButton(int index) => index switch
    {
        0 => MainTabHeaderBtn0,
        1 => MainTabHeaderBtn1,
        2 => MainTabHeaderBtn2,
        _ => null,
    };

    private void MainTabHeaderButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string tag) return;
        if (!int.TryParse(tag, out var idx)) return;
        if (idx < 0 || idx > 2) return;
        MainTabControl.SelectedIndex = idx;
    }

    private void OnMainTabControlSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        UpdateMainTabHeaderVisual(true);
    }

    private void UpdateMainTabHeaderVisual(bool animate)
    {
        var idx = MainTabControl.SelectedIndex;
        if (idx < 0) idx = 0;

        MainTabHeaderBtn0.Classes.Set("active", idx == 0);
        MainTabHeaderBtn1.Classes.Set("active", idx == 1);
        MainTabHeaderBtn2.Classes.Set("active", idx == 2);

        var selectedButton = GetMainTabHeaderButton(idx);
        if (selectedButton == null || MainTabUnderline == null) return;

        var origin = selectedButton.TranslatePoint(new Point(0, 0), MainTabsHeaderGrid);
        if (!origin.HasValue) return;

        var targetX = origin.Value.X;
        var targetWidth = Math.Max(0, selectedButton.Bounds.Width);
        if (targetWidth <= 0) return;

        if (_mainTabUnderlineTransform == null)
        {
            _mainTabUnderlineTransform = new TranslateTransform();
            MainTabUnderline.RenderTransform = _mainTabUnderlineTransform;
            MainTabUnderline.Width = targetWidth;
            _mainTabUnderlineTransform.X = targetX;
            return;
        }

        if (!animate)
        {
            _mainTabUnderlineTransform.X = targetX;
            MainTabUnderline.Width = targetWidth;
            return;
        }

        _mainTabUnderlineFromX = _mainTabUnderlineTransform.X;
        _mainTabUnderlineToX = targetX;
        _mainTabUnderlineFromWidth = MainTabUnderline.Width;
        _mainTabUnderlineToWidth = targetWidth;
        _mainTabUnderlineStartTick = Stopwatch.GetTimestamp();

        if (_mainTabUnderlineTimer == null)
        {
            _mainTabUnderlineTimer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(16)
            };
            _mainTabUnderlineTimer.Tick += OnMainTabUnderlineTick;
        }
        _mainTabUnderlineTimer.Start();
    }

    private void OnMainTabUnderlineTick(object? sender, EventArgs e)
    {
        if (_mainTabUnderlineTransform == null || MainTabUnderline == null)
            return;

        var elapsedMs = (Stopwatch.GetTimestamp() - _mainTabUnderlineStartTick)
                        / (double)Stopwatch.Frequency * 1000.0;
        var t = Math.Min(1.0, elapsedMs / MainTabUnderlineAnimMs);
        var k = EaseInOut(t);

        _mainTabUnderlineTransform.X = _mainTabUnderlineFromX + (_mainTabUnderlineToX - _mainTabUnderlineFromX) * k;
        MainTabUnderline.Width = _mainTabUnderlineFromWidth + (_mainTabUnderlineToWidth - _mainTabUnderlineFromWidth) * k;

        if (t >= 1.0)
        {
            _mainTabUnderlineTimer!.Stop();
            _mainTabUnderlineTransform.X = _mainTabUnderlineToX;
            MainTabUnderline.Width = _mainTabUnderlineToWidth;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────

    private async void ConnectionToggleButton_OnClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            ConnectionToggleButton.IsEnabled = false;
            if (_client.IsConnected)
            {
                await _client.DisconnectAsync();
                UpdateConnectionUi(false);
                return;
            }

            var serverUri = ServerUriTextBox.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(serverUri))
            {
                Log("请输入有效的服务器地址。");
                return;
            }

            if (!Uri.TryCreate(serverUri, UriKind.Absolute, out _))
            {
                Log("无效的 URI 格式。");
                return;
            }

            await _client.ConnectAsync(serverUri);
            UpdateConnectionUi(true);
            await RequestInitialDataAsync();
        }
        catch (Exception ex)
        {
            Log($"连接失败: {ex.Message}");
            ShowErrorToast("连接失败");
            UpdateConnectionUi(false);
        }
        finally
        {
            ConnectionToggleButton.IsEnabled = true;
        }
    }

    private void StreamingToggleButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_isPlaying)
        {
            StopStreaming();
            return;
        }

        try
        {
            StartStreaming();
        }
        catch (Exception ex)
        {
            Log($"启动变声失败: {ex.Message}");
            UpdateStreamingUi(false);
        }
    }

    private void RefreshAudioDevices_OnClick(object? sender, RoutedEventArgs e)
    {
        RefreshAudioDevices();
        Log("已刷新音频设备列表。");
    }

    private async void RefreshServerFiles_OnClick(object? sender, RoutedEventArgs e)
    {
        await _client.SendCommandAsync(new { command = "files_list" });
    }

    private async void UploadFile_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_isPlaying)
        {
            Log("上传会占用同一条 WebSocket 发送通道，请先停止变声。");
            return;
        }

        var files = await PickFilesAsync("选择上传文件", allowMultiple: true);
        foreach (var filePath in files)
        {
            await UploadFileToServerAsync(filePath);
        }
    }

    private void AddVoiceModel_OnClick(object? sender, RoutedEventArgs e)
    {
        _inlinePendingPth = string.Empty;
        _inlinePendingIndex = string.Empty;
        InlineModelNameBox.Text = string.Empty;
        InlinePthText.Text = "拖入 .pth 文件（必选）";
        InlineIndexText.Text = "拖入 .index 文件（可选）";
        SetSlotHighlight(InlinePthDropBorder, false);
        SetSlotHighlight(InlineIndexDropBorder, false);
        InlineAddVoiceModelCard.IsVisible = true;
        AddVoiceModelButton.IsEnabled = false;
        InlineModelNameBox.Focus();
        InlineModelNameBox.CaretIndex = InlineModelNameBox.Text?.Length ?? 0;
    }

    private void InlineCancelVoiceModel_OnClick(object? sender, RoutedEventArgs e)
    {
        InlineAddVoiceModelCard.IsVisible = false;
        AddVoiceModelButton.IsEnabled = true;
    }

    private async void InlineConfirmVoiceModel_OnClick(object? sender, RoutedEventArgs e)
    {
        var name = (InlineModelNameBox.Text ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            InlineModelNameBox.Focus();
            return;
        }

        if (string.IsNullOrWhiteSpace(_inlinePendingPth))
        {
            SetSlotHighlight(InlinePthDropBorder, true);
            return;
        }

        var pthName = Path.GetFileName(_inlinePendingPth);
        var indexName = string.IsNullOrWhiteSpace(_inlinePendingIndex) ? string.Empty : Path.GetFileName(_inlinePendingIndex);

        InlineAddVoiceModelCard.IsVisible = false;
        AddVoiceModelButton.IsEnabled = true;

        if (!_serverFileCache.ContainsKey(pthName))
        {
            if (!File.Exists(_inlinePendingPth))
            {
                Log($"服务器未找到 {pthName}，且本地路径无效，请先上传该文件。");
                return;
            }
            await UploadFileToServerAsync(_inlinePendingPth);
        }

        if (!string.IsNullOrWhiteSpace(_inlinePendingIndex) && !_serverFileCache.ContainsKey(indexName))
        {
            if (!File.Exists(_inlinePendingIndex))
            {
                Log($"服务器未找到 {indexName}，且本地路径无效，请先上传该文件。");
                return;
            }
            await UploadFileToServerAsync(_inlinePendingIndex);
        }

        await _client.SendCommandAsync(new { command = "voice_model_add", name, pth = pthName, index = indexName });
        await _client.SendCommandAsync(new { command = "voice_model_list" });
    }

    private void InlinePthBorder_DragOver(object? sender, DragEventArgs e)
    {
        if (e.DataTransfer.Contains(DataFormat.Text))
        {
            e.DragEffects = DragDropEffects.Copy;
            SetSlotHighlight(InlinePthDropBorder, true);
        }
        else
        {
            e.DragEffects = DragDropEffects.None;
        }
    }

    private void VoiceModels_DragOver(object? sender, DragEventArgs e)
    {
        if (e.DataTransfer.Contains(DataFormat.Text))
        {
            var text = e.DataTransfer.TryGetText() ?? string.Empty;
            var hasPth = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                             .Any(f => f.Trim().EndsWith(".pth", StringComparison.OrdinalIgnoreCase));
            e.DragEffects = hasPth ? DragDropEffects.Copy : DragDropEffects.None;
            if (hasPth) SetSlotHighlight(VoiceModelsDropZoneBorder, true);
        }
        else
        {
            e.DragEffects = DragDropEffects.None;
        }
    }

    private void VoiceModels_DragLeave(object? sender, RoutedEventArgs e)
    {
        SetSlotHighlight(VoiceModelsDropZoneBorder, false);
    }

    private void VoiceModels_Drop(object? sender, DragEventArgs e)
    {
        SetSlotHighlight(VoiceModelsDropZoneBorder, false);

        var text = e.DataTransfer.TryGetText();
        if (string.IsNullOrWhiteSpace(text)) return;

        var filenames = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                            .Select(f => f.Trim()).Where(f => !string.IsNullOrWhiteSpace(f)).ToList();

        var pth = filenames.FirstOrDefault(f => f.EndsWith(".pth", StringComparison.OrdinalIgnoreCase));
        var index = filenames.FirstOrDefault(f => f.EndsWith(".index", StringComparison.OrdinalIgnoreCase));

        if (string.IsNullOrWhiteSpace(pth)) return;

        // Pre-fill inline add card
        var pthFileName = Path.GetFileName(pth);
        var inferredName = Path.GetFileNameWithoutExtension(pth);

        _inlinePendingPth = pth;
        _inlinePendingIndex = index ?? string.Empty;

        InlineModelNameBox.Text = inferredName;
        InlinePthText.Text = pthFileName;
        InlineIndexText.Text = string.IsNullOrWhiteSpace(index) ? "拖入 .index 文件（可选）" : Path.GetFileName(index);

        SetSlotHighlight(InlinePthDropBorder, false);
        SetSlotHighlight(InlineIndexDropBorder, false);

        InlineAddVoiceModelCard.IsVisible = true;
        AddVoiceModelButton.IsEnabled = false;
        InlineModelNameBox.Focus();
        InlineModelNameBox.CaretIndex = inferredName?.Length ?? 0;
    }

    private void InlinePthBorder_Drop(object? sender, DragEventArgs e)
    {
        SetSlotHighlight(InlinePthDropBorder, false);
        var text = e.DataTransfer.TryGetText();
        if (string.IsNullOrWhiteSpace(text)) return;

        var name = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                       .Select(t => t.Trim()).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));
        if (string.IsNullOrWhiteSpace(name)) return;

        var fileName = Path.GetFileName(name);
        if (!fileName.EndsWith(".pth", StringComparison.OrdinalIgnoreCase)) return;

        _inlinePendingPth = name;
        InlinePthText.Text = fileName;

        if (string.IsNullOrWhiteSpace(InlineModelNameBox.Text))
            InlineModelNameBox.Text = Path.GetFileNameWithoutExtension(fileName);
    }

    private void InlineIndexBorder_DragOver(object? sender, DragEventArgs e)
    {
        if (e.DataTransfer.Contains(DataFormat.Text))
        {
            e.DragEffects = DragDropEffects.Copy;
            SetSlotHighlight(InlineIndexDropBorder, true);
        }
        else
        {
            e.DragEffects = DragDropEffects.None;
        }
    }

    private void InlineIndexBorder_Drop(object? sender, DragEventArgs e)
    {
        SetSlotHighlight(InlineIndexDropBorder, false);
        var text = e.DataTransfer.TryGetText();
        if (string.IsNullOrWhiteSpace(text)) return;

        var name = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                       .Select(t => t.Trim()).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));
        if (string.IsNullOrWhiteSpace(name)) return;

        var fileName = Path.GetFileName(name);
        if (!fileName.EndsWith(".index", StringComparison.OrdinalIgnoreCase)) return;

        _inlinePendingIndex = name;
        InlineIndexText.Text = fileName;
    }

    private void VoiceModelManagementListBox_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
    }

    private async void RemoveVoiceModel_OnContextMenuClick(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: VoiceModelItem model })
        {
            await _client.SendCommandAsync(new { command = "voice_model_remove", id = model.Id });
            await _client.SendCommandAsync(new { command = "voice_model_list" });
        }
    }

    private async void RemoveVoiceModel_OnClick(object? sender, RoutedEventArgs e)
    {
        if (VoiceModelManagementListBox.SelectedItem is not VoiceModelItem model)
        {
            return;
        }

        await _client.SendCommandAsync(new { command = "voice_model_remove", id = model.Id });
        await _client.SendCommandAsync(new { command = "voice_model_list" });
    }

    private void ServerFilesListBox_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        var selectedCount = ServerFilesListBox.SelectedItems?.Count ?? 0;
        DeleteFileButton.IsEnabled = selectedCount > 0;
        RenameFileButton.IsEnabled = selectedCount == 1;
    }

    private void DeleteSelectedFile_OnClick(object? sender, RoutedEventArgs e)
    {
        var selectedItems = ServerFilesListBox.SelectedItems?.OfType<ServerFileItem>().ToList() ?? [];
        if (selectedItems.Count == 0) return;

        if (selectedItems.Any(item => item.IsUploading))
        {
            Log("选中的文件中包含正在上传的文件，无法删除。");
            return;
        }

        var boundItems = selectedItems.Where(item => _boundFiles.Contains(item.Name)).ToList();
        if (boundItems.Count > 0)
        {
            Log(boundItems.Count == 1
                ? $"文件 {boundItems[0].Name} 已被绑定，请先移除绑定。"
                : $"以下 {boundItems.Count} 个文件已被绑定，请先移除绑定。");
            return;
        }

        var btn = sender as Avalonia.Controls.Control;
        if (this.Resources.TryGetValue("DeleteConfirmFlyout", out var r) && r is Avalonia.Controls.Flyout flyout)
        {
            if (flyout.Content is Avalonia.Controls.Panel tb && tb.Children.FirstOrDefault(c => c.Name == "DeleteConfirmTextBlock") is Avalonia.Controls.TextBlock textBlock)
            {
                textBlock.Text = $"确定要删除选中的 {selectedItems.Count} 个文件吗？";
            }
            if (btn != null) flyout.ShowAt(btn);
        }
    }

    private void CancelDelete_OnClick(object? sender, RoutedEventArgs e)
    {
        if (this.Resources.TryGetValue("DeleteConfirmFlyout", out var r) && r is Avalonia.Controls.Flyout flyout) flyout.Hide();
    }

    private async void ConfirmDelete_OnClick(object? sender, RoutedEventArgs e)
    {
        if (this.Resources.TryGetValue("DeleteConfirmFlyout", out var r) && r is Avalonia.Controls.Flyout flyout) flyout.Hide();

        var selectedItems = ServerFilesListBox.SelectedItems?.OfType<ServerFileItem>().ToList() ?? [];
        foreach (var item in selectedItems)
        {
            await _client.SendCommandAsync(new { command = "files_delete", name = item.Name });
        }
        await _client.SendCommandAsync(new { command = "files_list" });
    }

    private void RenameSelectedFile_OnClick(object? sender, RoutedEventArgs e)
    {
        if (ServerFilesListBox.SelectedItems?.Count != 1 || ServerFilesListBox.SelectedItem is not ServerFileItem item)
        {
            return;
        }

        if (item.IsUploading)
        {
            Log("该文件正在上传中，无法改名。");
            return;
        }

        item.EditingName = item.Name;
        item.IsEditing = true;

        DispatcherTimer.RunOnce(() =>
        {
            var container = ServerFilesListBox.ContainerFromItem(item);
            var tb = container?.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(t => t.IsVisible);
            if (tb != null)
            {
                tb.Focus();
                var text = tb.Text ?? "";
                var dotIndex = text.LastIndexOf('.');
                tb.CaretIndex = dotIndex > 0 ? dotIndex : text.Length;
            }
        }, TimeSpan.FromMilliseconds(80));
    }

    private async void CommitRename(ServerFileItem item, string newName)
    {
        item.IsEditing = false;
        if (string.IsNullOrWhiteSpace(newName) || string.Equals(newName, item.Name, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        await _client.SendCommandAsync(new { command = "files_rename", old_name = item.Name, new_name = newName });
    }

    private void RenameTextBox_OnKeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (sender is Avalonia.Controls.TextBox textBox && textBox.DataContext is ServerFileItem item)
        {
            if (e.Key == Avalonia.Input.Key.Enter)
            {
                CommitRename(item, textBox.Text ?? "");
                e.Handled = true;
            }
            else if (e.Key == Avalonia.Input.Key.Escape)
            {
                item.IsEditing = false;
                e.Handled = true;
            }
        }
    }

    private void RenameTextBox_OnLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is Avalonia.Controls.TextBox textBox && textBox.DataContext is ServerFileItem { IsEditing: true } item)
        {
            CommitRename(item, textBox.Text ?? "");
        }
    }


    private async void SlotItem_OnContextMenuRemoveClick(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: SlotBindingItem item })
        {
            await _client.SendCommandAsync(new { command = "model_remove_from_slot", slot = item.Slot, filename = item.FileName });
        }
    }

    private async void SlotListBox_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressSlotSelectionChanged)
        {
            return;
        }

        if (sender is ListBox listBox && listBox.SelectedItem is SlotBindingItem item)
        {
            await _client.SendCommandAsync(new { command = "model_activate_in_slot", slot = item.Slot, filename = item.FileName });
        }
    }


    // ---- Drag-drop: file list → slots ----

    private PointerPressedEventArgs? _pendingDragEvent;

    private void ServerFileItem_PointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(sender as Visual);
        if (point.Properties.IsLeftButtonPressed)
        {
            _dragStartPoint = e.GetPosition(sender as Visual);
            _dragStarted = false;
            _pendingDragEvent = e;

            // Capture selection NOW before ListBox pointer handling can change it.
            // If the pressed item is already in the current multi-selection, keep all selected;
            // otherwise the ListBox will switch to only this item (handled in PointerMoved fallback).
            var pressedItem = (sender as Control)?.DataContext as ServerFileItem;
            var currentSelection = ServerFilesListBox.SelectedItems?.OfType<ServerFileItem>().ToList() ?? [];
            _dragCandidates = pressedItem != null && currentSelection.Contains(pressedItem)
                ? currentSelection.Select(x => x.Name).Where(x => !string.IsNullOrWhiteSpace(x)).ToList()
                : null; // will fall back to SelectedItems or single item in PointerMoved
        }
    }

    private async void ServerFileItem_PointerMoved(object? sender, Avalonia.Input.PointerEventArgs e)
    {
        if (_dragStarted) return;
        if (_pendingDragEvent == null) return;
        var point = e.GetCurrentPoint(sender as Visual);
        if (!point.Properties.IsLeftButtonPressed)
        {
            _pendingDragEvent = null;
            return;
        }

        var pos = e.GetPosition(sender as Visual);
        if (Math.Abs(pos.X - _dragStartPoint.X) < 4 && Math.Abs(pos.Y - _dragStartPoint.Y) < 4)
            return;

        _dragStarted = true;
        e.Handled = true;

        var selected = _dragCandidates
            ?? ServerFilesListBox.SelectedItems?.OfType<ServerFileItem>()
                .Select(x => x.Name).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();

        if (selected is not { Count: > 0 } && sender is Control ctrl && ctrl.DataContext is ServerFileItem singleItem)
        {
            selected = new List<string> { singleItem.Name };
        }

        if (selected is not { Count: > 0 }) return;

        var data = new DataTransfer();
        data.Add(DataTransferItem.CreateText(string.Join("\n", selected)));
        var dragEvent = _pendingDragEvent;
        _pendingDragEvent = null;
        await DragDrop.DoDragDropAsync(dragEvent, data, DragDropEffects.Copy);

        // Drag operation ended — clear any stale slot highlights
        // (DragLeave is not always fired when the drag is cancelled or released outside a drop target)
        foreach (var b in new[] { HubertSlotBorder, RmvpeSlotBorder, VoiceModelsDropZoneBorder, InlinePthDropBorder, InlineIndexDropBorder })
            SetSlotHighlight(b, false);
    }

    private void SlotBorder_DragOver(object? sender, Avalonia.Input.DragEventArgs e)
    {
        if (sender is not Border border || border.Tag is not string slot) return;

        if (e.DataTransfer.Contains(DataFormat.Text))
        {
            e.DragEffects = DragDropEffects.Copy;
            var text = e.DataTransfer.TryGetText() ?? string.Empty;
            var filenames = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                               .Select(f => f.Trim()).Where(f => !string.IsNullOrWhiteSpace(f)).ToList();
            var anyInvalid = filenames.Any(f => !IsFilenameAllowedForSlot(slot, f));
            SetSlotHighlight(border, true, anyInvalid);
        }
        else
        {
            e.DragEffects = DragDropEffects.None;
        }
    }

    private void SlotBorder_DragLeave(object? sender, EventArgs e)
    {
        SetSlotHighlight(sender, false);
    }

    private async void SlotBorder_Drop(object? sender, Avalonia.Input.DragEventArgs e)
    {
        SetSlotHighlight(sender, false);

        if (sender is not Border border || border.Tag is not string slot)
            return;

        var text = e.DataTransfer.TryGetText();
        if (string.IsNullOrWhiteSpace(text)) return;

        var filenames = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var filename in filenames)
        {
            var name = filename.Trim();
            if (string.IsNullOrWhiteSpace(name)) continue;

            if (!IsFilenameAllowedForSlot(slot, name))
            {
                Log($"文件 {name} 的扩展名不符合槽位 {slot} 要求。");
                continue;
            }

            await _client.SendCommandAsync(new { command = "model_add_to_slot", slot, filename = name });
        }
    }

    private void SetSlotHighlight(object? sender, bool active, bool invalid = false)
    {
        if (sender is not Border border) return;

        border.Classes.Remove("drag-valid");
        border.Classes.Remove("drag-invalid");

        if (active)
            border.Classes.Add(invalid ? "drag-invalid" : "drag-valid");
    }

    private void FileSortComboBox_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (FileSortComboBox.SelectedItem is ComboBoxItem item)
        {
            _fileSortMode = item.Tag?.ToString() ?? "time_desc";
            RefreshServerFilesView();
        }
    }

    private void HideBoundFilesCheckBox_OnClick(object? sender, RoutedEventArgs e)
    {
        _hideBoundFiles = HideBoundFilesCheckBox.IsChecked == true;
        RefreshServerFilesView();
    }

    private async void SaveServerLog_OnClick(object? sender, RoutedEventArgs e)
    {
        var content = ServerLogTextBox.Text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(content))
        {
            Log("没有可保存的服务端日志内容。");
            return;
        }

        var topLevel = TopLevel.GetTopLevel(this);
        var file = topLevel?.StorageProvider is null
            ? null
            : await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "保存服务端日志",
                SuggestedFileName = ServerLogFilesComboBox.SelectedItem is LogFileItem selected ? selected.FileName : "server_log.txt",
                FileTypeChoices = [new FilePickerFileType("日志文件") { Patterns = ["*.log", "*.txt"] }],
            });

        var path = file?.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(path))
        {
            await File.WriteAllTextAsync(path, content);
            Log($"服务端日志已保存到: {path}");
        }
    }

    private async void RefreshLogs_OnClick(object? sender, RoutedEventArgs e)
    {
        await _client.SendCommandAsync(new { command = "list_logs" });
    }

    private async void ClearOldLogs_OnClick(object? sender, RoutedEventArgs e)
    {
        var confirm = new ConfirmWindow("清空历史日志", "确定删除除当前日志以外的所有历史日志文件吗？此操作不可撤销。");
        var result = await confirm.ShowDialog<bool?>(this);
        if (result == true)
        {
            await _client.SendCommandAsync(new { command = "clear_old_logs" });
        }
    }

    private void ClearLogs_OnClick(object? sender, RoutedEventArgs e)
    {
        LogTextBox.Text = string.Empty;
        Log("日志已清空。");
    }

    private async Task ApplyServerSettingsAsync()
    {
        if (!_uiInitialized) return;

        if (!ValidateBlockTimeConfig()) return;

        _f0UpKey = (int)Math.Round(F0UpKeySlider.Value);
        _blockTime = (float)BlockTimeSlider.Value / 1000f;
        _crossfadeLength = (float)CrossfadeSlider.Value / 1000f;
        _extraTime = (float)ExtraTimeSlider.Value / 1000f;
        _serverStreamChunkMs = (int)Math.Round(ServerStreamChunkSlider.Value);
        _formantShift = (float)FormantSlider.Value;
        _silenceDbThreshold = (float)SilenceDbSlider.Value;
        _silenceGateAtten = (float)SilenceGateAttenSlider.Value;
        _inputNoiseReduce = InputNoiseReduceSwitch.IsChecked == true;
        _outputNoiseReduce = OutputNoiseReduceSwitch.IsChecked == true;
        _noiseReducePropDecrease = (float)NoiseReduceStrengthSlider.Value;
        _rmsMixRate = (float)RmsMixRateSlider.Value;
        _f0Method = F0FcpeBtn.Classes.Contains("active") ? "fcpe" : "rmvpe";
        _indexRate = (float)IndexRateSlider.Value;

        if (_bypassServerVoice) return;
        if (!_client.IsConnected) return;

        await SendConfigurationAsync(true);
        SetAnimatedVisibility(SyncErrorPanel, false);
    }

    private void ApplyLocalSettings()
    {
        if (!_uiInitialized)
        {
            return;
        }

        if (TargetBufferSlider == null
            || MaxBufferSlider == null
            || BufferCapacitySlider == null
            || NetworkSliceSlider == null
            || AutoBufferBtn == null
            || BlockTimeSlider == null
            || JitterFactorSlider == null
            || MinBufferSlider == null
            || JitterMaxBufferSlider == null
            || JitterAlphaSlider == null)
        {
            // Controls may not be fully initialized during early ValueChanged callbacks.
            return;
        }

        if (!ValidateBlockTimeConfig())
        {
            Log("分块时间不能大于手动目标缓冲区延迟。");
            return;
        }

        _targetBufferLatency = (int)Math.Round(TargetBufferSlider.Value);
        _maxBufferMs = (int)Math.Round(MaxBufferSlider.Value);
        _bufferCapacityMs = (int)Math.Round(BufferCapacitySlider.Value);
        _networkSliceMs = (int)Math.Round(NetworkSliceSlider.Value);
        _useAdaptiveBuffer = AutoBufferBtn.Classes.Contains("active");
        _jitterEstimator.BlockTimeMs = BlockTimeSlider.Value;
        _jitterEstimator.JitterFactor = JitterFactorSlider.Value;
        _jitterEstimator.MinBufferMs = MinBufferSlider.Value;
        _jitterEstimator.MaxBufferMs = JitterMaxBufferSlider.Value;
        _jitterEstimator.Alpha = JitterAlphaSlider.Value;

        if (_waveProvider != null)
        {
            _waveProvider.BufferDuration = TimeSpan.FromMilliseconds(_bufferCapacityMs);
        }
    }

    private async void RetrySync_OnClick(object? sender, RoutedEventArgs e)
    {
        SyncErrorText.Text = "同步中...";
        await ApplyServerSettingsAsync();
        if (!SyncErrorPanel.Classes.Contains("collapsed"))
            SyncErrorText.Text = "同步失败，点击重试";
        else
            Log("服务端参数同步成功。");
    }

    private async void VoiceModelChip_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string id)
        {
            return;
        }

        var vm = _voiceModelsSelection.FirstOrDefault(v => string.Equals(v.Id, id, StringComparison.Ordinal));
        if (vm is null)
        {
            return;
        }

        _prevSelectedVoiceModelId = _selectedVoiceModelId;
        _selectedVoiceModelId = id;
        UpdateVoiceModelSelectionState();

        if (string.Equals(id, VoiceModelItem.RawId, StringComparison.Ordinal))
        {
            _bypassServerVoice = true;
            _serverPassthroughVoice = false;
            ModelStatusTextBlock.Text = "原声";
            Log("已切换到本地原声模式。实时音频链路待迁移。");
            UpdateStreamingToggleEnabled();
            return;
        }

        if (string.Equals(id, VoiceModelItem.ServerRawId, StringComparison.Ordinal))
        {
            _bypassServerVoice = false;
            _serverPassthroughVoice = true;
            ModelStatusTextBlock.Text = "原声（服务端）";
            UpdateStreamingToggleEnabled();
            if (_client.IsConnected)
            {
                await SendConfigurationAsync(true);
            }
            Log("已切换到服务端原声通路模式。");
            return;
        }

        _bypassServerVoice = false;
        _serverPassthroughVoice = false;
        _modelPath = vm.Pth;
        _indexPath = vm.Index;
        ModelStatusTextBlock.Text = vm.Name;
        UpdateStreamingToggleEnabled();
        if (_client.IsConnected)
        {
            // Show blue "activating" state immediately — voice_models response will set correct final state
            var targetVmManage = _voiceModelsManagement.FirstOrDefault(v => string.Equals(v.Id, id, StringComparison.Ordinal));
            if (targetVmManage != null)
            {
                _failedVoiceModelIds.Remove(id);
                    // Only show loading state if model is not already loaded
                    if (targetVmManage.StatusHint != "已加载到显存，可立即使用")
                    {
                        targetVmManage.StatusBrush = new SolidColorBrush(Color.Parse("#2196F3"));
                        targetVmManage.StatusHint = "激活中…";
                    }
            }
            await SendConfigurationAsync(true);
        }
        await _client.SendCommandAsync(new { command = "voice_model_activate", id = vm.Id });
    }

    private async void VoiceModelStatusDot_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        e.Handled = true;
        if (sender is not Control control || control.Tag is not string id)
        {
            return;
        }

        if (string.Equals(id, VoiceModelItem.RawId, StringComparison.Ordinal)
            || string.Equals(id, VoiceModelItem.ServerRawId, StringComparison.Ordinal))
        {
            return;
        }

        if (!_client.IsConnected)
        {
            Log("请先连接服务器，再加载模型到显存。");
            return;
        }

        try
        {
            // 立即把该模型的状态灯变蓝，表示正在请求加载
            var targetVm = _voiceModelsManagement.FirstOrDefault(vm => string.Equals(vm.Id, id, StringComparison.Ordinal));
            if (targetVm != null)
            {
                targetVm.StatusBrush = new SolidColorBrush(Color.Parse("#2196F3"));
                targetVm.StatusHint = "加载中…";
            }
            _failedVoiceModelIds.Remove(id);
            _pendingPreloadModelId = id;

            await _client.SendCommandAsync(new { command = "voice_model_preload", id });
            Log("已请求将模型加载到显存。");
        }
        catch (Exception ex)
        {
            Log($"请求加载模型失败: {ex.Message}");
        }
    }

    private async void ServerLogFilesComboBox_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (SyncCurrentLogCheckBox.IsChecked == true)
        {
            return;
        }

        await ReadSelectedServerLogAsync();
    }

    private async void ReadSelectedServerLog_OnClick(object? sender, RoutedEventArgs e)
    {
        await ReadSelectedServerLogAsync();
    }

    private async void SyncCurrentLogCheckBox_OnClick(object? sender, RoutedEventArgs e)
    {
        bool enabled = SyncCurrentLogCheckBox.IsChecked == true;
        ServerLogFilesComboBox.IsEnabled = !enabled;

        if (enabled)
        {
            await _client.SendCommandAsync(new { command = "watch_log", action = "start" });
            Log("已开启实时日志同步。");
            return;
        }

        await _client.SendCommandAsync(new { command = "watch_log", action = "stop" });
        Log("已关闭实时日志同步。");
    }

    private void F0UpKeySlider_OnValueChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        F0UpKeyValueText?.Text = Math.Round(e.NewValue).ToString("0");
        if (!_uiInitialized) return;
        _f0UpKey = (int)Math.Round(e.NewValue);
        ScheduleRealtimeConfigSend();
    }

    // ── Slider inline text editing ────────────────────────────────────────────────

    private static string GetSliderRawText(Slider slider)
    {
        // Format the value without display suffix, ready for the user to edit.
        return slider.Name switch
        {
            "F0UpKeySlider" or "BlockTimeSlider" or "CrossfadeSlider"
                or "ExtraTimeSlider" or "ServerStreamChunkSlider" or "SilenceDbSlider"
                or "JitterMaxBufferSlider" or "MinBufferSlider" or "TargetBufferSlider"
                or "MaxBufferSlider" or "BufferCapacitySlider" or "NetworkSliceSlider"
                => ((int)Math.Round(slider.Value)).ToString(),
            _ => slider.Value.ToString("F2"),
        };
    }

    private void SliderValueText_DoubleTapped(object? sender, Avalonia.Input.TappedEventArgs e)
    {
        if (sender is not TextBlock tb || tb.Tag is not string sliderName) return;
        var slider = this.FindControl<Slider>(sliderName);
        if (slider == null) return;

        var editName = sliderName.Replace("Slider", "ValueEdit");
        var editBox = this.FindControl<TextBox>(editName);
        if (editBox == null) return;

        editBox.Text = GetSliderRawText(slider);
        tb.IsVisible = false;
        editBox.IsVisible = true;
        editBox.Focus();
        editBox.CaretIndex = editBox.Text?.Length ?? 0;
            // Show corresponding unit label
            var unitLabelName = sliderName.Replace("Slider", "UnitLabel");
            var unitLabel = this.FindControl<TextBlock>(unitLabelName);
            if (unitLabel != null) unitLabel.IsVisible = true;
    }

    private void CommitSliderEdit(TextBox tb)
    {
        if (tb.Tag is not string sliderName) return;
        var textBlockName = sliderName.Replace("Slider", "ValueText");
        var textBlock = this.FindControl<TextBlock>(textBlockName);
        var slider = this.FindControl<Slider>(sliderName);

        if (slider != null
            && double.TryParse(tb.Text?.Trim(), System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var value))
        {
            value = Math.Max(slider.Minimum, Math.Min(slider.Maximum, value));
            slider.Value = value;  // triggers the existing OnValueChanged which updates the display text
        }

        tb.IsVisible = false;
        if (textBlock != null) textBlock.IsVisible = true;
            // Hide corresponding unit label
            var unitLabelName = sliderName.Replace("Slider", "UnitLabel");
            var unitLabel = this.FindControl<TextBlock>(unitLabelName);
            if (unitLabel != null) unitLabel.IsVisible = false;
    }

    private void CancelSliderEdit(TextBox tb)
    {
        if (tb.Tag is not string sliderName) return;
        var textBlockName = sliderName.Replace("Slider", "ValueText");
        var textBlock = this.FindControl<TextBlock>(textBlockName);
        tb.IsVisible = false;
        if (textBlock != null) textBlock.IsVisible = true;
            // Hide corresponding unit label
            var unitLabelName = sliderName.Replace("Slider", "UnitLabel");
            var unitLabel = this.FindControl<TextBlock>(unitLabelName);
            if (unitLabel != null) unitLabel.IsVisible = false;
    }

    private void SliderValueEdit_KeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (sender is not TextBox tb) return;
        if (e.Key == Avalonia.Input.Key.Enter)
        {
            CommitSliderEdit(tb);
            e.Handled = true;
        }
        else if (e.Key == Avalonia.Input.Key.Escape)
        {
            CancelSliderEdit(tb);
            e.Handled = true;
        }
    }

    private void SliderValueEdit_LostFocus(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is TextBox tb)
            CommitSliderEdit(tb);
    }

    private void GlobalPointerPressed_CommitSliderEdit(object? sender, PointerPressedEventArgs e)
    {
        var activeEdit = this
            .GetVisualDescendants()
            .OfType<TextBox>()
            .FirstOrDefault(tb => tb.IsVisible && tb.Classes.Contains("slider-value-edit"));

        if (activeEdit == null)
        {
            return;
        }

        if (e.Source is Visual source)
        {
            if (ReferenceEquals(source, activeEdit)
                || source.GetVisualAncestors().Any(ancestor => ReferenceEquals(ancestor, activeEdit)))
            {
                return;
            }
        }

        CommitSliderEdit(activeEdit);
    }

    // ─────────────────────────────────────────────────────────────────────────────

    private void IndexRateSlider_OnValueChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        IndexRateValueText?.Text = e.NewValue.ToString("0.00");
        if (!_uiInitialized) return;
        _indexRate = (float)e.NewValue;
        ScheduleRealtimeConfigSend();
    }

    private void FormantSlider_OnValueChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        FormantValueText?.Text = e.NewValue.ToString("0.00");
        if (!_uiInitialized) return;
        _formantShift = (float)e.NewValue;
        ScheduleRealtimeConfigSend();
    }

    private void BlockTimeSlider_OnValueChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        BlockTimeValueText?.Text = $"{e.NewValue:F0} ms";
        if (!_uiInitialized) return;
        _jitterEstimator.BlockTimeMs = e.NewValue;
        UpdateBlockTimeValidationUi();
        _ = ApplyServerSettingsAsync();
    }

    private void CrossfadeSlider_OnValueChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        CrossfadeValueText?.Text = $"{e.NewValue:F0} ms";
        if (!_uiInitialized) return;
        _ = ApplyServerSettingsAsync();
    }

    private void ExtraTimeSlider_OnValueChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        ExtraTimeValueText?.Text = $"{e.NewValue:F0} ms";
        if (!_uiInitialized) return;
        _ = ApplyServerSettingsAsync();
    }

    private void ServerStreamChunkSlider_OnValueChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        ServerStreamChunkValueText?.Text = $"{e.NewValue:F0} ms";
        if (!_uiInitialized) return;
        _ = ApplyServerSettingsAsync();
    }

    private void SilenceDbSlider_OnValueChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        SilenceDbValueText?.Text = $"{e.NewValue:F0} dB";
        if (!_uiInitialized) return;
        _ = ApplyServerSettingsAsync();
    }

    private void SilenceGateAttenSlider_OnValueChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        SilenceGateAttenValueText?.Text = e.NewValue.ToString("0.00");
        if (!_uiInitialized) return;
        _ = ApplyServerSettingsAsync();
    }

    private void NoiseReduceStrengthSlider_OnValueChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        NoiseReduceStrengthValueText?.Text = e.NewValue.ToString("0.00");
        if (!_uiInitialized) return;
        _ = ApplyServerSettingsAsync();
    }

    private void RmsMixRateSlider_OnValueChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        RmsMixRateValueText?.Text = e.NewValue.ToString("0.00");
        if (!_uiInitialized) return;
        _ = ApplyServerSettingsAsync();
    }

    private void NoiseReduce_OnChange(object? sender, RoutedEventArgs e)
    {
        if (!_uiInitialized) return;
        _ = ApplyServerSettingsAsync();
    }

    private void F0Method_OnClick(object? sender, RoutedEventArgs e)
    {
        if (!_uiInitialized) return;
        if (sender is not Button btn) return;
        if (ClassesContains(btn, "active")) return;

        var isRmvpe = btn == F0RmvpeBtn;
        SetSegmentedToggle(F0RmvpeBtn, isRmvpe);
        SetSegmentedToggle(F0FcpeBtn, !isRmvpe);
        _ = ApplyServerSettingsAsync();
    }

    private void BufferMode_OnClick(object? sender, RoutedEventArgs e)
    {
        if (!_uiInitialized) return;
        if (sender is not Button btn) return;
        if (ClassesContains(btn, "active")) return;

        var isAuto = btn == AutoBufferBtn;
        SetSegmentedToggle(AutoBufferBtn, isAuto);
        SetSegmentedToggle(ManualBufferBtn, !isAuto);
        SetAnimatedVisibility(AutoBufferPanel, isAuto);
        SetAnimatedVisibility(ManualBufferPanel, !isAuto);
        UpdateBlockTimeValidationUi();
        ApplyLocalSettings();
    }

    private static void SetSegmentedToggle(Button btn, bool active)
    {
        if (active)
            btn.Classes.Add("active");
        else
            btn.Classes.Remove("active");
    }

    private static bool ClassesContains(Control control, string className)
    {
        return control.Classes.Contains(className);
    }

    private void JitterFactorSlider_OnValueChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        JitterFactorValueText?.Text = e.NewValue.ToString("0.0");
        if (!_uiInitialized) return;
        ApplyLocalSettings();
    }

    private void JitterAlphaSlider_OnValueChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        JitterAlphaValueText?.Text = e.NewValue.ToString("0.00");
        if (!_uiInitialized) return;
        ApplyLocalSettings();
    }

    private void JitterMaxBufferSlider_OnValueChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        JitterMaxBufferValueText?.Text = $"{e.NewValue:F0} ms";
        if (!_uiInitialized) return;
        ApplyLocalSettings();
    }

    private void MinBufferSlider_OnValueChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        MinBufferValueText?.Text = $"{e.NewValue:F0} ms";
        if (!_uiInitialized) return;
        ApplyLocalSettings();
    }

    private void TargetBufferSlider_OnValueChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        TargetBufferValueText?.Text = $"{e.NewValue:F0} ms";
        if (!_uiInitialized) return;
        UpdateBlockTimeValidationUi();
        ApplyLocalSettings();
    }

    private void MaxBufferSlider_OnValueChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        MaxBufferValueText?.Text = $"{e.NewValue:F0} ms";
        if (!_uiInitialized) return;
        ApplyLocalSettings();
    }

    private void BufferCapacitySlider_OnValueChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        BufferCapacityValueText?.Text = $"{e.NewValue:F0} ms";
        if (!_uiInitialized) return;
        ApplyLocalSettings();
    }

    private void NetworkSliceSlider_OnValueChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        NetworkSliceValueText?.Text = $"{e.NewValue:F0} ms";
        if (!_uiInitialized) return;
        ApplyLocalSettings();
    }

    private bool ValidateBlockTimeConfig()
    {
        if (AutoBufferBtn == null || BlockTimeSlider == null || TargetBufferSlider == null)
        {
            // During XAML initialization some controls may not be ready yet.
            return true;
        }

        if (AutoBufferBtn.Classes.Contains("active"))
        {
            return true;
        }

        return BlockTimeSlider.Value <= TargetBufferSlider.Value;
    }

    private void UpdateBlockTimeValidationUi()
    {
        if (TargetBufferErrorPanel == null)
        {
            return;
        }

        SetAnimatedVisibility(TargetBufferErrorPanel, !ValidateBlockTimeConfig());
    }

    protected override void OnClosed(EventArgs e)
    {
        _client.LogReceived -= Client_OnLogReceived;
        _client.ConnectionStateChanged -= Client_OnConnectionStateChanged;
        _client.TextMessageReceived -= Client_OnTextMessageReceived;
        _client.BinaryMessageReceived -= Client_OnBinaryMessageReceived;
        StopStreaming();
        _ = _client.DisposeAsync();
        base.OnClosed(e);
    }

    private void Client_OnLogReceived(object? sender, string message)
    {
        Dispatcher.UIThread.Post(() => Log(message));
    }

    private void Client_OnConnectionStateChanged(object? sender, bool isConnected)
    {
        Dispatcher.UIThread.Post(() => UpdateConnectionUi(isConnected));
    }

    private void Client_OnTextMessageReceived(object? sender, string json)
    {
        Dispatcher.UIThread.Post(() => HandleTextMessage(json));
    }

    private void Client_OnBinaryMessageReceived(object? sender, byte[] payload)
    {
        HandleBinaryMessage(payload);
    }

    private async Task RequestInitialDataAsync()
    {
        Log("同步配置中...");
        await _client.SendCommandAsync(new { command = "files_list" });
        await _client.SendCommandAsync(new { command = "model_list_slots" });
        await _client.SendCommandAsync(new { command = "voice_model_list" });
        await _client.SendCommandAsync(new { command = "list_logs" });
    }

    private void RefreshAudioDevices()
    {
        string? preferInput = _selectedInputDeviceId;
        string? preferOutput = _selectedOutputDeviceId;

        _audioInputDevices.Clear();
        _audioOutputDevices.Clear();

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            string? defaultCaptureId = null;
            try
            {
                defaultCaptureId = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications)?.ID;
            }
            catch
            {
            }

            preferInput ??= defaultCaptureId;

            foreach (var dev in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                _audioInputDevices.Add(new AudioDeviceItem { Id = dev.ID, Name = dev.FriendlyName });
            }
        }
        catch
        {
        }

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            string? defaultOutputId = null;
            try
            {
                defaultOutputId = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)?.ID;
            }
            catch
            {
            }

            foreach (var dev in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                _audioOutputDevices.Add(new AudioDeviceItem { Id = dev.ID, Name = dev.FriendlyName });
            }

            preferOutput ??= defaultOutputId;
        }
        catch
        {
        }

        if (_audioInputDevices.Count > 0)
        {
            var item = !string.IsNullOrWhiteSpace(preferInput)
                ? _audioInputDevices.FirstOrDefault(x => string.Equals(x.Id, preferInput, StringComparison.OrdinalIgnoreCase))
                : null;
            InputDeviceComboBox.SelectedItem = item ?? _audioInputDevices[0];
            _selectedInputDeviceId = (InputDeviceComboBox.SelectedItem as AudioDeviceItem)?.Id;
        }

        if (_audioOutputDevices.Count > 0)
        {
            var item = !string.IsNullOrWhiteSpace(preferOutput)
                ? _audioOutputDevices.FirstOrDefault(x => string.Equals(x.Id, preferOutput, StringComparison.OrdinalIgnoreCase))
                : null;
            OutputDeviceComboBox.SelectedItem = item ?? _audioOutputDevices[0];
            _selectedOutputDeviceId = (OutputDeviceComboBox.SelectedItem as AudioDeviceItem)?.Id;
        }
    }

    private async Task ReadSelectedServerLogAsync()
    {
        if (ServerLogFilesComboBox.SelectedItem is LogFileItem selectedItem)
        {
            await _client.SendCommandAsync(new { command = "read_log", filename = selectedItem.FileName });
        }
    }

    private void UpdateConnectionUi(bool isConnected)
    {
        ServerUriTextBox.IsEnabled = !isConnected;
        SetAnimatedVisibility(ConnectionGatePanel, !isConnected);
        DisconnectButton.Opacity = isConnected ? 1.0 : 0.0;
        DisconnectButton.IsEnabled = isConnected;
        DisconnectButton.IsHitTestVisible = isConnected;
        GlobalStatusTextBlock.Text = isConnected ? "已连接" : "未连接";
        if (!isConnected)
        {
            SetModelState(ModelState.NotReady);
        }
        else if (_modelState == ModelState.NotReady)
        {
            ModelStatusTextBlock.Text = _bypassServerVoice ? "原声" : "等待模型";
            MainTabControl.SelectedIndex = 0;
        }
        UpdateStreamingToggleEnabled();
    }

    private void HandleTextMessage(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.TryGetProperty("type", out var rootType) && string.Equals(rootType.GetString(), "pong", StringComparison.Ordinal))
            {
                if (root.TryGetProperty("client_ts", out var clientTsElement))
                {
                    var clientTs = clientTsElement.GetInt64();
                    var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    NetworkLatencyTextBlock.Text = $"{now - clientTs} ms";
                }

                return;
            }

            if (!root.TryGetProperty("status", out var status))
            {
                return;
            }

            var type = root.TryGetProperty("type", out var typeElement) ? typeElement.GetString() ?? string.Empty : string.Empty;
            var isOk = string.Equals(status.GetString(), "ok", StringComparison.OrdinalIgnoreCase);
            if (!isOk)
            {
                if (string.Equals(type, "upload_offset_mismatch", StringComparison.OrdinalIgnoreCase))
                {
                    _uploadOffsetCorrections[root.GetProperty("upload_id").GetString() ?? string.Empty] = root.GetProperty("expected_offset").GetInt64();
                    return;
                }

                var errorMessage = root.TryGetProperty("message", out var errorElement) ? errorElement.GetString() ?? "未知错误" : "未知错误";

                // 语音模型加载失败：把蓝灯变红灯
                if (string.Equals(type, "voice_model_error", StringComparison.OrdinalIgnoreCase))
                {
                    Log($"[错误] 模型加载失败: {errorMessage}");
                    ShowErrorToast("模型加载失败");
                    MarkCurrentTargetModelError();
                    RevertModelSelectionOnError();
                    if (!string.IsNullOrEmpty(_pendingPreloadModelId))
                    {
                        _failedVoiceModelIds.Add(_pendingPreloadModelId);
                        var failedVm = _voiceModelsManagement.FirstOrDefault(vm => string.Equals(vm.Id, _pendingPreloadModelId, StringComparison.Ordinal));
                        if (failedVm != null)
                        {
                            failedVm.StatusBrush = new SolidColorBrush(Color.Parse("#F44336"));
                            failedVm.StatusHint = $"加载失败: {errorMessage}";
                        }
                        _pendingPreloadModelId = null;
                    }
                    return;
                }

                // config 加载失败
                if (string.Equals(type, "config_error", StringComparison.OrdinalIgnoreCase))
                {
                    SetModelState(ModelState.Error, errorMessage);
                    MarkCurrentTargetModelError();
                    ShowErrorToast("模型加载失败");
                    RevertModelSelectionOnError();
                    return;
                }

                ModelStatusTextBlock.Text = "服务端返回错误";
                Log($"服务器错误: {errorMessage}");
                return;
            }

            switch (type)
            {
                case "config_ack":
                    SetModelState(ModelState.Ready);
                    SetActiveModelLoadingState(isLoading: false);
                    if (root.TryGetProperty("hash", out var hashElement))
                    {
                        long ackSeq = _lastSentConfigSeq;
                        if (root.TryGetProperty("seq", out var seqElement) && seqElement.TryGetInt64(out var seqValue))
                        {
                            ackSeq = seqValue;
                        }

                        if (ackSeq == _lastSentConfigSeq)
                        {
                            var serverHash = hashElement.GetString() ?? string.Empty;
                            var localHash = ComputeConfigHash(_lastSentConfig);
                            if (!string.Equals(serverHash, localHash, StringComparison.OrdinalIgnoreCase))
                            {
                                Log("[WARN] 配置不一致，正在强制同步...");
                                _ = SendConfigurationAsync(true);
                            }
                        }
                    }
                    break;
                case "config_error":
                    SetModelState(ModelState.Error, root.TryGetProperty("message", out var configErrorMessage) ? configErrorMessage.GetString() ?? "模型加载失败" : "模型加载失败");
                    MarkCurrentTargetModelError();
                    ShowErrorToast("模型加载失败");
                    RevertModelSelectionOnError();
                    break;
                case "log_list":
                    UpdateServerLogList(
                        root.GetProperty("files").EnumerateArray().Select(item => item.GetString() ?? string.Empty).ToList(),
                        root.GetProperty("current").GetString() ?? string.Empty);
                    break;
                case "log_content":
                    ShowServerLogContent(
                        root.GetProperty("filename").GetString() ?? string.Empty,
                        root.GetProperty("content").GetString() ?? string.Empty);
                    break;
                case "log_chunk":
                    ServerLogTextBox.Text = (ServerLogTextBox.Text ?? string.Empty) + (root.GetProperty("content").GetString() ?? string.Empty);
                    break;
                case "files_list":
                    ApplyServerFiles(root.GetProperty("files"));
                    break;
                case "voice_models":
                    _pendingPreloadModelId = null;
                    ApplyVoiceModelsFromServer(root.GetProperty("voice"));
                    break;
                case "voice_model_error":
                {
                    var errMsg = root.TryGetProperty("message", out var vmErrMsg) ? vmErrMsg.GetString() ?? "模型加载失败" : "模型加载失败";
                    Log($"[错误] 模型加载失败: {errMsg}");
                    ShowErrorToast("模型加载失败");
                    MarkCurrentTargetModelError();
                    RevertModelSelectionOnError();
                    if (!string.IsNullOrEmpty(_pendingPreloadModelId))
                    {
                        var failedVm = _voiceModelsManagement.FirstOrDefault(vm => string.Equals(vm.Id, _pendingPreloadModelId, StringComparison.Ordinal));
                        if (failedVm != null)
                        {
                            failedVm.StatusBrush = new SolidColorBrush(Color.Parse("#F44336"));
                            failedVm.StatusHint = $"加载失败: {errMsg}";
                        }
                        _pendingPreloadModelId = null;
                    }
                    break;
                }
                case "model_slots":
                    ApplySlotsFromServer(root.GetProperty("slots"));
                    break;
                case "model_slot_updated":
                    if (ApplySingleSlotFromServer(root.GetProperty("slot").GetString() ?? string.Empty, root.GetProperty("state")))
                    {
                        RecomputeBoundFiles();
                        RefreshServerFilesView();
                    }
                    break;
                case "files_renamed":
                    Log($"文件已改名: {root.GetProperty("old_name").GetString() ?? string.Empty} -> {root.GetProperty("new_name").GetString() ?? string.Empty}");
                    _ = _client.SendCommandAsync(new { command = "files_list" });
                    break;
                case "upload_ready":
                    _uploadReadyTcs?.TrySetResult((
                        root.GetProperty("upload_id").GetString() ?? string.Empty,
                        root.GetProperty("name").GetString() ?? string.Empty,
                        root.GetProperty("received_bytes").GetInt64(),
                        root.GetProperty("total_bytes").GetInt64()));
                    break;
                case "upload_progress":
                    var uploadId = root.GetProperty("upload_id").GetString() ?? string.Empty;
                    if (_uploadItemsById.TryGetValue(uploadId, out var uploadItem))
                    {
                        uploadItem.Name = root.GetProperty("name").GetString() ?? uploadItem.Name;
                        uploadItem.TotalBytes = root.GetProperty("total_bytes").GetInt64();
                        uploadItem.SentBytes = root.GetProperty("received_bytes").GetInt64();
                        uploadItem.IsUploading = true;
                        uploadItem.Status = "上传中";
                    }
                    break;
                case "upload_done":
                    _uploadDoneTcs?.TrySetResult((
                        root.GetProperty("upload_id").GetString() ?? string.Empty,
                        root.GetProperty("name").GetString() ?? string.Empty));
                    break;
                case "server_stopping":
                    Log(root.TryGetProperty("message", out var messageElement) ? messageElement.GetString() ?? "服务器正在关闭" : "服务器正在关闭");
                    break;
                default:
                    if (root.TryGetProperty("message", out var defaultMessage))
                    {
                        Log($"服务器: {defaultMessage.GetString() ?? "操作成功"}");
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            Log($"解析服务端消息失败: {ex.Message}");
        }
    }

    private void ApplyServerFiles(JsonElement filesElement)
    {
        var items = new List<ServerFileItem>();
        foreach (var fileElement in filesElement.EnumerateArray())
        {
            var name = fileElement.TryGetProperty("name", out var nameElement) ? nameElement.GetString() ?? string.Empty : string.Empty;
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            long size = fileElement.TryGetProperty("size", out var sizeElement) ? sizeElement.GetInt64() : 0;
            double modifiedSeconds = fileElement.TryGetProperty("mtime", out var mtimeElement) ? mtimeElement.GetDouble() : 0;

            var item = new ServerFileItem
            {
                Name = name,
                Size = size,
                ModifiedAt = modifiedSeconds > 0 ? DateTimeOffset.FromUnixTimeSeconds((long)Math.Round(modifiedSeconds)) : DateTimeOffset.MinValue,
                Status = string.Empty,
            };

            if (fileElement.TryGetProperty("voice_meta", out var metaElement) && metaElement.ValueKind == JsonValueKind.Object)
            {
                bool ok = metaElement.TryGetProperty("ok", out var okElement) && okElement.ValueKind == JsonValueKind.True;
                if (ok)
                {
                    var version = metaElement.TryGetProperty("version", out var versionElement) ? versionElement.GetString() ?? string.Empty : string.Empty;
                    var sr = metaElement.TryGetProperty("sr", out var srElement) ? srElement.GetString() ?? string.Empty : string.Empty;
                    int f0 = metaElement.TryGetProperty("f0", out var f0Element) && f0Element.TryGetInt32(out var f0Value) ? f0Value : 0;
                    var info = metaElement.TryGetProperty("info", out var infoElement) ? infoElement.GetString() ?? string.Empty : string.Empty;
                    item.IsVoiceModelPth = true;
                    item.VoiceModelTooltip = $"version: {version}\nsr: {sr}\nf0: {f0}\ninfo: {info}";
                }
            }

            items.Add(item);
        }

        _serverFilesRaw.Clear();
        _serverFilesRaw.AddRange(items);
        _serverFileCache.Clear();
        foreach (var item in items)
        {
            _serverFileCache[item.Name] = item;
        }

        RefreshServerFilesView();
        Log($"已获取服务端文件列表，共 {_serverFilesRaw.Count} 项。");
    }

    private void ApplyVoiceModelsFromServer(JsonElement voiceElement)
    {
        var previousSelectionId = _selectedVoiceModelId;
        var activeId = string.Empty;
        var lastUnloadedId = voiceElement.TryGetProperty("last_unloaded_id", out var lastUnloadedIdElement)
            ? lastUnloadedIdElement.GetString() ?? string.Empty
            : string.Empty;
        _recentUnloadedVoiceModelId = lastUnloadedId;
        var list = new List<VoiceModelItem>();
        var modelsElement = voiceElement.TryGetProperty("models", out var models) ? models : default;

        if (modelsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var modelElement in modelsElement.EnumerateArray())
            {
                var id = modelElement.TryGetProperty("id", out var idElement) ? idElement.GetString() ?? string.Empty : string.Empty;
                var name = modelElement.TryGetProperty("name", out var nameElement) ? nameElement.GetString() ?? string.Empty : string.Empty;
                var pth = modelElement.TryGetProperty("pth", out var pthElement) ? pthElement.GetString() ?? string.Empty : string.Empty;
                var index = modelElement.TryGetProperty("index", out var indexElement) ? indexElement.GetString() ?? string.Empty : string.Empty;
                var isActive = modelElement.TryGetProperty("active", out var activeElement) && activeElement.ValueKind == JsonValueKind.True;
                var isLoaded = modelElement.TryGetProperty("loaded", out var loadedElement) && loadedElement.ValueKind == JsonValueKind.True;
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(pth))
                {
                    continue;
                }

                if (isActive)
                {
                    activeId = id;
                }

                list.Add(new VoiceModelItem
                {
                    Id = id,
                    Name = name,
                    Pth = pth,
                    Index = index,
                    IsActive = isActive,
                    ShowStatusDot = true,
                });

                var justAdded = list[^1];
                var statusLoadedBrush = new SolidColorBrush(Color.Parse("#2E9F4D"));
                var statusIdleBrush = new SolidColorBrush(Color.Parse("#8B8B8B"));
                var statusUnloadedBrush = new SolidColorBrush(Color.Parse("#C4971E"));
                var statusFailedBrush = new SolidColorBrush(Color.Parse("#F44336"));
                if (isLoaded)
                {
                    // 成功加载后从失败集合中移除
                    _failedVoiceModelIds.Remove(id);
                    justAdded.StatusBrush = statusLoadedBrush;
                    justAdded.StatusHint = "已加载到显存，可立即使用";
                }
                else if (_failedVoiceModelIds.Contains(id))
                {
                    justAdded.StatusBrush = statusFailedBrush;
                    justAdded.StatusHint = "加载失败，点击重试";
                }
                else if (string.Equals(id, _recentUnloadedVoiceModelId, StringComparison.Ordinal))
                {
                    justAdded.StatusBrush = statusUnloadedBrush;
                    justAdded.StatusHint = "最近被卸载（为加载新模型释放显存）";
                }
                else
                {
                    justAdded.StatusBrush = statusIdleBrush;
                    justAdded.StatusHint = "未加载到显存";
                }
            }
        }

        _voiceModelsManagement.Clear();
        foreach (var item in list)
        {
            _voiceModelsManagement.Add(item);
        }

        _voiceModelsSelection.Clear();
        _voiceModelsSelection.Add(_rawVoiceModelItem);
        if (_debugMode)
            _voiceModelsSelection.Add(_serverRawVoiceModelItem);
        foreach (var item in list)
        {
            _voiceModelsSelection.Add(item);
        }

        var selectedId = string.IsNullOrEmpty(previousSelectionId) ? null : previousSelectionId;
        var resolvedId = _voiceModelsSelection.Any(item => string.Equals(item.Id, selectedId, StringComparison.Ordinal)) ? selectedId
            : !string.IsNullOrEmpty(activeId) ? activeId
            : VoiceModelItem.RawId;
        _selectedVoiceModelId = resolvedId;

        // Only clear switch rollback marker after server confirms target became active.
        if (!string.IsNullOrEmpty(_prevSelectedVoiceModelId)
            && !string.IsNullOrEmpty(activeId)
            && string.Equals(activeId, _selectedVoiceModelId, StringComparison.Ordinal))
        {
            _prevSelectedVoiceModelId = null;
        }

        VoiceModelManagementListBox.SelectedItem = _voiceModelsManagement.FirstOrDefault(item => string.Equals(item.Id, activeId, StringComparison.Ordinal));
        UpdateVoiceModelSelectionState();

        if (!string.IsNullOrWhiteSpace(activeId))
        {
            var activeVm = _voiceModelsManagement.FirstOrDefault(item => string.Equals(item.Id, activeId, StringComparison.Ordinal));
            if (activeVm != null && !_bypassServerVoice)
            {
                ModelStatusTextBlock.Text = activeVm.Name;
            }
        }

        RecomputeBoundFiles();
        RefreshServerFilesView();

        Log($"已获取音色模型列表，共 {list.Count} 项。");
    }

    /// <summary>
    /// Called on config_error: marks the model the user was trying to switch TO (not the server-active model) as failed (red light).
    /// Only acts when a model switch was in progress (_prevSelectedVoiceModelId != null).
    /// </summary>
    private void MarkCurrentTargetModelError()
    {
        if (string.IsNullOrEmpty(_prevSelectedVoiceModelId)) return; // no switch in progress

        var targetId = _selectedVoiceModelId;
        if (string.IsNullOrEmpty(targetId)
            || string.Equals(targetId, VoiceModelItem.RawId, StringComparison.Ordinal)
            || string.Equals(targetId, VoiceModelItem.ServerRawId, StringComparison.Ordinal))
        {
            return;
        }

        var failedVm = _voiceModelsManagement.FirstOrDefault(vm => string.Equals(vm.Id, targetId, StringComparison.Ordinal));
        if (failedVm != null)
        {
            _failedVoiceModelIds.Add(targetId);
            failedVm.StatusBrush = new SolidColorBrush(Color.Parse("#F44336"));
            failedVm.StatusHint = "加载失败，点击重试";
        }
    }

    private void RevertModelSelectionOnError()
    {
        if (string.IsNullOrEmpty(_prevSelectedVoiceModelId)) return;

        var prevId = _prevSelectedVoiceModelId;
        _prevSelectedVoiceModelId = null;
        _selectedVoiceModelId = prevId;

        if (string.Equals(prevId, VoiceModelItem.RawId, StringComparison.Ordinal))
        {
            _bypassServerVoice = true;
            _serverPassthroughVoice = false;
            ModelStatusTextBlock.Text = "原声";
        }
        else if (string.Equals(prevId, VoiceModelItem.ServerRawId, StringComparison.Ordinal))
        {
            _bypassServerVoice = false;
            _serverPassthroughVoice = true;
            ModelStatusTextBlock.Text = "原声（服务端）";
        }
        else
        {
            var vm = _voiceModelsSelection.FirstOrDefault(v => string.Equals(v.Id, prevId, StringComparison.Ordinal));
            if (vm != null)
            {
                _bypassServerVoice = false;
                _serverPassthroughVoice = false;
                _modelPath = vm.Pth;
                _indexPath = vm.Index;
                ModelStatusTextBlock.Text = vm.Name;
            }
        }

        UpdateVoiceModelSelectionState();
        UpdateStreamingToggleEnabled();
    }

    private void UpdateVoiceModelSelectionState()
    {
        foreach (var vm in _voiceModelsSelection)
        {
            vm.IsUserSelected = string.Equals(vm.Id, _selectedVoiceModelId, StringComparison.Ordinal);
        }

        foreach (var vm in _voiceModelsManagement)
        {
            vm.IsUserSelected = string.Equals(vm.Id, _selectedVoiceModelId, StringComparison.Ordinal);
        }
    }

    private void ApplySlotsFromServer(JsonElement slotsElement)
    {
        foreach (var slot in slotsElement.EnumerateObject())
        {
            ApplySingleSlotFromServer(slot.Name, slot.Value);
        }

        RecomputeBoundFiles();
        RefreshServerFilesView();
    }

    private bool ApplySingleSlotFromServer(string slot, JsonElement state)
    {
        var files = new List<string>();
        if (state.TryGetProperty("files", out var filesElement) && filesElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var fileElement in filesElement.EnumerateArray())
            {
                var fileName = fileElement.GetString() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(fileName))
                {
                    files.Add(fileName);
                }
            }
        }

        var active = state.TryGetProperty("active", out var activeElement) && activeElement.ValueKind == JsonValueKind.String
            ? activeElement.GetString() ?? string.Empty
            : string.Empty;

        if (state.TryGetProperty("allowed_ext", out var extElement) && extElement.ValueKind == JsonValueKind.Array)
        {
            var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var extItem in extElement.EnumerateArray())
            {
                var ext = (extItem.GetString() ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(ext))
                {
                    continue;
                }

                if (!ext.StartsWith('.'))
                {
                    ext = "." + ext;
                }

                allowed.Add(ext.ToLowerInvariant());
            }

            _slotAllowedExt[slot] = allowed;
        }

        var list = slot switch
        {
            "hubert_base" => _hubertSlotItems,
            "rmvpe" => _rmvpeSlotItems,
            _ => null,
        };

        var listBox = slot switch
        {
            "hubert_base" => HubertSlotListBox,
            "rmvpe" => RmvpeSlotListBox,
            _ => null,
        };

        if (list == null || listBox == null)
        {
            return false;
        }

        _suppressSlotSelectionChanged = true;
        try
        {
            var existingFiles = list.Select(item => item.FileName).ToList();
            var filesChanged = existingFiles.Count != files.Count || !existingFiles.SequenceEqual(files, StringComparer.OrdinalIgnoreCase);

            SlotBindingItem? activeItem = null;
            if (filesChanged)
            {
                list.Clear();
                foreach (var fileName in files)
                {
                    var isItemActive = string.Equals(fileName, active, StringComparison.OrdinalIgnoreCase);
                    var item = new SlotBindingItem
                    {
                        Slot = slot,
                        FileName = fileName,
                        IsActive = isItemActive,
                        StatusBrush = isItemActive ? new SolidColorBrush(Color.Parse("#2E9F4D")) : new SolidColorBrush(Color.Parse("#8B8B8B")),
                        StatusHint = isItemActive ? "已激活" : "未激活",
                    };
                    list.Add(item);
                    if (item.IsActive)
                    {
                        activeItem = item;
                    }
                }
            }
            else
            {
                foreach (var item in list)
                {
                    item.IsActive = string.Equals(item.FileName, active, StringComparison.OrdinalIgnoreCase);
                    item.StatusBrush = item.IsActive ? new SolidColorBrush(Color.Parse("#2E9F4D")) : new SolidColorBrush(Color.Parse("#8B8B8B"));
                    item.StatusHint = item.IsActive ? "已激活" : "未激活";
                    if (item.IsActive)
                    {
                        activeItem = item;
                    }
                }
            }

            listBox.SelectedItem = activeItem;
            return filesChanged;
        }
        finally
        {
            _suppressSlotSelectionChanged = false;
        }
    }

    private void RecomputeBoundFiles()
    {
        _boundFiles.Clear();
        foreach (var item in _hubertSlotItems)
        {
            _boundFiles.Add(item.FileName);
        }

        foreach (var item in _rmvpeSlotItems)
        {
            _boundFiles.Add(item.FileName);
        }

        foreach (var item in _voiceModelsManagement)
        {
            if (!string.IsNullOrWhiteSpace(item.Pth))
            {
                _boundFiles.Add(item.Pth);
            }

            if (!string.IsNullOrWhiteSpace(item.Index))
            {
                _boundFiles.Add(item.Index);
            }
        }
    }

    private void RefreshServerFilesView()
    {
        var desired = new List<ServerFileItem>();
        desired.AddRange(_uploadingFiles);

        IEnumerable<ServerFileItem> query = _serverFilesRaw;
        var uploadingNames = new HashSet<string>(_uploadingFiles.Select(item => item.Name), StringComparer.OrdinalIgnoreCase);
        query = query.Where(item => !uploadingNames.Contains(item.Name));

        if (_hideBoundFiles && _boundFiles.Count > 0)
        {
            query = query.Where(item => !_boundFiles.Contains(item.Name));
        }

        query = _fileSortMode switch
        {
            "time_asc" => query.OrderBy(item => item.ModifiedAt),
            "name_asc" => query.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase),
            "name_desc" => query.OrderByDescending(item => item.Name, StringComparer.OrdinalIgnoreCase),
            _ => query.OrderByDescending(item => item.ModifiedAt),
        };

        desired.AddRange(query);

        _serverFiles.Clear();
        foreach (var item in desired)
        {
            _serverFiles.Add(item);
        }
    }

    private async Task BindSelectedFilesToSlotAsync(string slot)
    {
        var selectedItems = ServerFilesListBox.SelectedItems?.OfType<ServerFileItem>().ToList() ?? [];
        if (selectedItems.Count == 0)
        {
            Log("请先在右侧选择至少一个文件。");
            return;
        }

        foreach (var item in selectedItems)
        {
            if (!IsFilenameAllowedForSlot(slot, item.Name))
            {
                Log($"文件 {item.Name} 的扩展名不符合槽位 {slot} 要求。");
                continue;
            }

            await _client.SendCommandAsync(new { command = "model_add_to_slot", slot, filename = item.Name });
        }
    }

    private async Task RemoveSelectedSlotBindingAsync(ListBox listBox)
    {
        if (listBox.SelectedItem is not SlotBindingItem item)
        {
            return;
        }

        await _client.SendCommandAsync(new { command = "model_remove_from_slot", slot = item.Slot, filename = item.FileName });
    }

    private bool IsFilenameAllowedForSlot(string slot, string filename)
    {
        if (!_slotAllowedExt.TryGetValue(slot, out var allowed) || allowed.Count == 0)
        {
            return true;
        }

        var lower = filename.Trim().ToLowerInvariant();
        return allowed.Any(ext => lower.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<List<string>> PickFilesAsync(string title, bool allowMultiple, params FilePickerFileType[] fileTypes)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider == null)
        {
            return [];
        }

        var items = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = allowMultiple,
            FileTypeFilter = fileTypes.Length > 0 ? fileTypes : null,
        });

        return items.Select(item => item.TryGetLocalPath()).Where(path => !string.IsNullOrWhiteSpace(path)).Cast<string>().ToList();
    }

    private async Task<string?> PromptAsync(string title, string prompt, string initialValue = "", string placeholderText = "")
    {
        var window = new TextPromptWindow(title, prompt, initialValue, placeholderText);
        return await window.ShowDialog<string?>(this);
    }

    private async Task<bool> ConfirmAsync(string title, string message)
    {
        var window = new ConfirmWindow(title, message);
        return await window.ShowDialog<bool>(this);
    }

    private async Task UploadFileToServerAsync(string filePath)
    {
        await _uploadSerialLock.WaitAsync();
        try
        {
            if (!_client.IsConnected)
            {
                Log("未连接到服务器。");
                return;
            }

            var fileInfo = new FileInfo(filePath);
            var uploadItem = new ServerFileItem
            {
                Name = fileInfo.Name,
                IsUploading = true,
                Status = "计算 SHA256",
                TotalBytes = fileInfo.Length,
                SentBytes = 0,
                ModifiedAt = DateTimeOffset.Now,
            };

            _uploadingFiles.RemoveAll(item => string.Equals(item.Name, uploadItem.Name, StringComparison.OrdinalIgnoreCase));
            _uploadingFiles.Insert(0, uploadItem);
            RefreshServerFilesView();

            var sha256 = await ComputeSha256HexAsync(filePath);
            uploadItem.Status = "准备上传";

            _uploadReadyTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
            await _client.SendCommandAsync(new { command = "upload_init", name = fileInfo.Name, size = fileInfo.Length, sha256 });
            var ready = await _uploadReadyTcs.Task;

            _uploadItemsById[ready.UploadId] = uploadItem;
            uploadItem.Name = ready.Name;
            uploadItem.TotalBytes = ready.TotalBytes;
            uploadItem.SentBytes = ready.ReceivedBytes;
            uploadItem.Status = ready.ReceivedBytes > 0 ? "续传中" : "上传中";

            var offset = ready.ReceivedBytes;
            var chunkSize = 1024 * 1024;
            var buffer = new byte[chunkSize];

            await using (var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, chunkSize, FileOptions.SequentialScan))
            {
                fileStream.Seek(offset, SeekOrigin.Begin);
                while (offset < fileInfo.Length)
                {
                    if (_uploadOffsetCorrections.TryRemove(ready.UploadId, out var expectedOffset) && expectedOffset != offset)
                    {
                        offset = expectedOffset;
                        fileStream.Seek(offset, SeekOrigin.Begin);
                    }

                    var read = await fileStream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(chunkSize, fileInfo.Length - offset)));
                    if (read <= 0)
                    {
                        break;
                    }

                    await _client.SendBinaryAsync(BuildFileChunkFrame(ready.UploadId, (ulong)offset, buffer, read));
                    offset += read;
                    uploadItem.SentBytes = offset;
                }
            }

            uploadItem.Status = "校验中";
            _uploadDoneTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
            await _client.SendCommandAsync(new { command = "upload_finish", upload_id = ready.UploadId });
            var done = await _uploadDoneTcs.Task;

            _uploadItemsById.TryRemove(ready.UploadId, out _);
            uploadItem.IsUploading = false;
            uploadItem.Name = done.FinalName;
            uploadItem.Status = "完成";
            _uploadingFiles.RemoveAll(item => ReferenceEquals(item, uploadItem));
            await _client.SendCommandAsync(new { command = "files_list" });
        }
        finally
        {
            _uploadSerialLock.Release();
        }
    }

    private static async Task<string> ComputeSha256HexAsync(string filePath)
    {
        using var sha = SHA256.Create();
        await using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
        var hash = await sha.ComputeHashAsync(fileStream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static byte[] BuildFileChunkFrame(string uploadId, ulong offset, byte[] payloadBuffer, int payloadLength)
    {
        var magic = Encoding.ASCII.GetBytes("RVCFILE1");
        var frame = new byte[8 + 1 + 16 + 8 + 4 + payloadLength];
        Buffer.BlockCopy(magic, 0, frame, 0, 8);
        frame[8] = 1;

        var guidBytes = GuidToRfcBytes(Guid.Parse(uploadId));
        Buffer.BlockCopy(guidBytes, 0, frame, 9, 16);

        WriteUInt64BE(frame, 25, offset);
        WriteUInt32BE(frame, 33, (uint)payloadLength);
        Buffer.BlockCopy(payloadBuffer, 0, frame, 37, payloadLength);
        return frame;
    }

    private static byte[] GuidToRfcBytes(Guid guid)
    {
        var hex = guid.ToString("N");
        var bytes = new byte[16];
        for (int index = 0; index < 16; index++)
        {
            bytes[index] = Convert.ToByte(hex.Substring(index * 2, 2), 16);
        }

        return bytes;
    }

    private static void WriteUInt64BE(byte[] buffer, int offset, ulong value)
    {
        buffer[offset + 0] = (byte)(value >> 56);
        buffer[offset + 1] = (byte)(value >> 48);
        buffer[offset + 2] = (byte)(value >> 40);
        buffer[offset + 3] = (byte)(value >> 32);
        buffer[offset + 4] = (byte)(value >> 24);
        buffer[offset + 5] = (byte)(value >> 16);
        buffer[offset + 6] = (byte)(value >> 8);
        buffer[offset + 7] = (byte)value;
    }

    private static void WriteUInt32BE(byte[] buffer, int offset, uint value)
    {
        buffer[offset + 0] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }

    private void UpdateServerLogList(List<string> files, string current)
    {
        _serverLogFiles.Clear();
        LogFileItem? currentItem = null;
        foreach (var file in files)
        {
            var item = new LogFileItem
            {
                FileName = file,
                DisplayName = file == current ? file + " (当前)" : file,
            };
            _serverLogFiles.Add(item);
            if (file == current)
            {
                currentItem = item;
            }
        }

        ServerLogFilesComboBox.SelectedItem = currentItem ?? _serverLogFiles.FirstOrDefault();
        // 固定下拉 Popup 最小宽度 = ComboBox 实际宽度，防止滚动时列表宽度收窄
        Dispatcher.UIThread.Post(() =>
        {
            if (ServerLogFilesComboBox.Bounds.Width > 0)
            {
                ServerLogFilesComboBox.MinWidth = ServerLogFilesComboBox.Bounds.Width;
            }
        }, DispatcherPriority.Loaded);
        Log($"已获取日志列表，共 {_serverLogFiles.Count} 个文件。");
    }

    private void ShowServerLogContent(string filename, string content)
    {
        ServerLogTextBox.Text = content;
        Log($"已加载日志文件: {filename} ({content.Length} 字节)");
    }

    private long GetMonoNs()
    {
        return Stopwatch.GetElapsedTime(_monoBaseTimestamp).Ticks * 100;
    }

    private void SetActiveModelLoadingState(bool isLoading, bool isError = false)
    {
        var loadingBrush = new SolidColorBrush(Color.Parse("#2196F3")); // 蓝色
        var readyBrush = new SolidColorBrush(Color.Parse("#2E9F4D"));   // 绿色
        var errorBrush = new SolidColorBrush(Color.Parse("#F44336"));   // 红色

        // 更新 VoiceModel 激活项的状态灯
        foreach (var vm in _voiceModelsManagement)
        {
            if (vm.IsActive)
            {
                if (isError)
                {
                    vm.StatusBrush = errorBrush;
                    vm.StatusHint = "加载失败";
                }
                else if (isLoading)
                {
                    vm.StatusBrush = loadingBrush;
                    vm.StatusHint = "加载中…";
                }
                else
                {
                    vm.StatusBrush = readyBrush;
                    vm.StatusHint = "已加载到显存，可立即使用";
                }
            }
        }

        // 更新 Hubert / RMVPE slot active 项的状态灯
        foreach (var slotItems in new[] { _hubertSlotItems, _rmvpeSlotItems })
        {
            foreach (var item in slotItems)
            {
                if (item.IsActive)
                {
                    if (isError)
                    {
                        item.StatusBrush = errorBrush;
                        item.StatusHint = "加载失败";
                    }
                    else if (isLoading)
                    {
                        item.StatusBrush = loadingBrush;
                        item.StatusHint = "加载中…";
                    }
                    else
                    {
                        item.StatusBrush = readyBrush;
                        item.StatusHint = "已加载到显存";
                    }
                }
            }
        }
    }

    private void SetModelState(ModelState state, string? message = null)
    {
        _modelState = state;
        var text = state switch
        {
            ModelState.Loading => "模型加载中",
            ModelState.Ready => string.IsNullOrWhiteSpace(message) ? "模型已就绪" : message,
            ModelState.Error => string.IsNullOrWhiteSpace(message) ? "模型加载失败" : message,
            _ => string.IsNullOrWhiteSpace(message) ? "模型未加载" : message,
        };
        if (!_bypassServerVoice)
        {
            ModelStatusTextBlock.Text = text;
        }
        UpdateStreamingToggleEnabled();
    }

    private void UpdateStreamingToggleEnabled()
    {
        bool canStartBypass = _bypassServerVoice;
        bool canStartViaServer = _client.IsConnected && (_serverPassthroughVoice || _modelState == ModelState.Ready);
        StreamingToggleButton.IsEnabled = _isPlaying || canStartBypass || canStartViaServer;
    }

    private void UpdateStreamingUi(bool isStreaming)
    {
        _isPlaying = isStreaming;
        StreamingToggleButton.Content = isStreaming ? "停止" : "开始变声";
        InputDeviceComboBox.IsEnabled = !isStreaming && _audioInputDevices.Count > 0;
        OutputDeviceComboBox.IsEnabled = !isStreaming && _audioOutputDevices.Count > 0;
        GlobalStatusTextBlock.Text = isStreaming ? "变声中" : _client.IsConnected ? "已连接" : "未连接";
    }

    private void ScheduleRealtimeConfigSend()
    {
        if (!_client.IsConnected)
        {
            return;
        }

        if (_bypassServerVoice)
        {
            return;
        }

        Interlocked.Exchange(ref _realtimeConfigDebouncePending, 1);
        _realtimeConfigDebounceTimer?.Stop();
        _realtimeConfigDebounceTimer?.Start();
    }

    private async Task FlushRealtimeConfigAsync()
    {
        _realtimeConfigDebounceTimer?.Stop();
        if (Interlocked.Exchange(ref _realtimeConfigDebouncePending, 0) == 0)
        {
            return;
        }

        try
        {
            await SendConfigurationAsync();
        }
        catch (Exception ex)
        {
            Log($"实时更新参数失败: {ex.Message}");
        }
    }

    private string ComputeConfigHash(Dictionary<string, object> config)
    {
        var keysToHash = new List<string>
        {
            "model_path",
            "index_path",
            "f0_up_key",
            "block_time",
            "crossfade_length",
            "extra_time",
            "stream_chunk_ms",
            "formant_shift",
            "f0method",
            "index_rate",
            "passthrough",
            "silence_db_threshold",
            "silence_gate_atten",
            "input_noise_reduce",
            "output_noise_reduce",
            "noise_reduce_prop_decrease",
            "rms_mix_rate",
        };

        var floatKeys = new HashSet<string>
        {
            "block_time",
            "crossfade_length",
            "extra_time",
            "formant_shift",
            "index_rate",
            "silence_db_threshold",
            "silence_gate_atten",
            "noise_reduce_prop_decrease",
            "rms_mix_rate",
        };

        var parts = new List<string>();
        foreach (var key in keysToHash.OrderBy(item => item, StringComparer.Ordinal))
        {
            config.TryGetValue(key, out var value);
            if (key == "model_path" || key == "index_path")
            {
                var fileName = Path.GetFileName(value?.ToString() ?? string.Empty);
                parts.Add($"{key}={fileName}");
                continue;
            }

            if (floatKeys.Contains(key))
            {
                var floatValue = value == null ? 0.0f : Convert.ToSingle(value);
                parts.Add($"{key}={floatValue.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)}");
                continue;
            }

            parts.Add($"{key}={value?.ToString() ?? "None"}");
        }

        using var md5 = MD5.Create();
        var hash = md5.ComputeHash(Encoding.UTF8.GetBytes(string.Join("|", parts)));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private async Task SendConfigurationAsync(bool forceFull = false)
    {
        if (!_serverPassthroughVoice && string.IsNullOrWhiteSpace(_modelPath) && !_bypassServerVoice)
        {
            SetModelState(ModelState.NotReady);
            return;
        }

        var modelPath = _serverPassthroughVoice ? string.Empty : _modelPath;
        var indexPath = _serverPassthroughVoice ? string.Empty : _indexPath;
        var indexRate = _serverPassthroughVoice ? 0.0f : _indexRate;

        var currentConfig = new Dictionary<string, object>
        {
            { "model_path", modelPath },
            { "index_path", indexPath },
            { "f0_up_key", _f0UpKey },
            { "block_time", _blockTime },
            { "crossfade_length", _crossfadeLength },
            { "extra_time", _extraTime },
            { "stream_chunk_ms", _serverStreamChunkMs },
            { "formant_shift", _formantShift },
            { "f0method", _f0Method },
            { "index_rate", indexRate },
            { "passthrough", _serverPassthroughVoice },
            { "silence_db_threshold", _silenceDbThreshold },
            { "silence_gate_atten", _silenceGateAtten },
            { "input_noise_reduce", _inputNoiseReduce },
            { "output_noise_reduce", _outputNoiseReduce },
            { "noise_reduce_prop_decrease", _noiseReducePropDecrease },
            { "rms_mix_rate", _rmsMixRate },
        };

        var diffConfig = new Dictionary<string, object>();
        foreach (var pair in currentConfig)
        {
            if (forceFull || !_lastSentConfig.TryGetValue(pair.Key, out var previous) || !Equals(previous, pair.Value))
            {
                diffConfig[pair.Key] = pair.Value;
            }
        }

        if (diffConfig.Count == 0)
        {
            return;
        }

        foreach (var pair in diffConfig)
        {
            _lastSentConfig[pair.Key] = pair.Value;
        }

        var seq = Interlocked.Increment(ref _configSeq);
        _lastSentConfigSeq = seq;
        SetModelState(ModelState.Loading);
        await _client.SendCommandAsync(new { config = diffConfig, seq });
        Log($"已发送配置 (Keys: {diffConfig.Count})");
    }

    private void StartStreaming()
    {
        if (_isPlaying)
        {
            return;
        }

        if (!_bypassServerVoice && !_serverPassthroughVoice && _modelState != ModelState.Ready)
        {
            throw new InvalidOperationException("模型尚未就绪，请先选择并等待模型加载完成。");
        }

        _streamStartNs = GetMonoNs();
        _lastSentAudioTsNs = 0;
        _streamSessionId = unchecked(_streamSessionId + 1);

        _waveProvider = new BufferedWaveProvider(WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, Channels))
        {
            BufferDuration = TimeSpan.FromMilliseconds(_bufferCapacityMs),
            DiscardOnBufferOverflow = true,
            ReadFully = true,
        };

        var selectedOutput = OutputDeviceComboBox.SelectedItem as AudioDeviceItem;
        if (selectedOutput != null && !string.IsNullOrWhiteSpace(selectedOutput.Id))
        {
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                _outputDevice = enumerator.GetDevice(selectedOutput.Id);
                _waveOut = new WasapiOut(_outputDevice, AudioClientShareMode.Shared, false, 80);
            }
            catch
            {
                _outputDevice?.Dispose();
                _outputDevice = null;
                _waveOut = new WasapiOut(AudioClientShareMode.Shared, 80);
            }
        }
        else
        {
            _waveOut = new WasapiOut(AudioClientShareMode.Shared, 80);
        }

        _waveOut.PlaybackStopped += OnPlaybackStopped;
        _waveOut.Init(_waveProvider);
        _playbackStarted = false;
        UpdateStreamingUi(true);

        if (!_bypassServerVoice)
        {
            StartAudioSendLoop();
        }

        using var inputEnumerator = new MMDeviceEnumerator();
        MMDevice? inputDevice = null;
        if (InputDeviceComboBox.SelectedItem is AudioDeviceItem selectedInput && !string.IsNullOrWhiteSpace(selectedInput.Id))
        {
            try
            {
                inputDevice = inputEnumerator.GetDevice(selectedInput.Id);
            }
            catch
            {
                inputDevice = null;
            }
        }

        inputDevice ??= TryGetDefaultCapture(inputEnumerator, Role.Communications);
        inputDevice ??= TryGetDefaultCapture(inputEnumerator, Role.Multimedia);
        _waveIn = inputDevice != null ? new WasapiCapture(inputDevice) : new WasapiCapture();
        _waveIn.DataAvailable += OnAudioDataAvailable;

        var sourceFormat = _waveIn.WaveFormat;
        _captureBuffer = new BufferedWaveProvider(sourceFormat)
        {
            BufferDuration = TimeSpan.FromMilliseconds(500),
            DiscardOnBufferOverflow = true,
            ReadFully = false,
        };

        ISampleProvider samples = new WaveToSampleProvider(_captureBuffer);
        if (samples.WaveFormat.Channels == 2)
        {
            samples = new StereoToMonoSampleProvider(samples) { LeftVolume = 0.5f, RightVolume = 0.5f };
        }
        else if (samples.WaveFormat.Channels > 2)
        {
            var mux = new MultiplexingSampleProvider(new[] { samples }, 1);
            mux.ConnectInputToOutput(0, 0);
            samples = mux;
        }

        samples = new WdlResamplingSampleProvider(samples, SampleRate);
        _captureProvider = new SampleToWaveProvider(samples);

        int chunkBytes = (int)(SampleRate * (_networkSliceMs / 1000.0) * 4);
        if (chunkBytes < 4)
        {
            chunkBytes = 4;
        }

        _captureReadBuffer = new byte[chunkBytes];
        _waveIn.StartRecording();

        Log(_bypassServerVoice ? "音频录制已开始 - 原声输出中" : _serverPassthroughVoice ? "音频录制已开始 - 原声经服务器输出中" : "音频录制已开始 - 变声进行中");
    }

    private void StartWaveformTimer()
    {
        if (_waveformTimer != null) return;

        _waveformTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
        _waveformTimer.Tick += (_, _) =>
        {
            const int samplesPerTick = 320;

            // --- 输入（跳过积压，始终读最新数据，消除延迟感）---
            float inSumSq = 0; int inCount = 0;
            lock (_waveformAudioInLock)
            {
                int avail = (_waveformAudioInWritePos - _waveformAudioInReadPos + _waveformAudioInBuf.Length) % _waveformAudioInBuf.Length;
                // Skip stale backlog: keep only the latest samplesPerTick samples
                if (avail > samplesPerTick)
                {
                    int skip = avail - samplesPerTick;
                    _waveformAudioInReadPos = (_waveformAudioInReadPos + skip) % _waveformAudioInBuf.Length;
                    avail = samplesPerTick;
                }
                for (int s = 0; s < avail; s++)
                {
                    float v = _waveformAudioInBuf[_waveformAudioInReadPos];
                    inSumSq += v * v; inCount++;
                    _waveformAudioInReadPos = (_waveformAudioInReadPos + 1) % _waveformAudioInBuf.Length;
                }
            }
            {
                float rawRms = inCount > 0 ? (float)Math.Sqrt(inSumSq / inCount) : 0f;
                // Peak envelope: instant attack, slow release (τ≈190ms)
                float prevIn = _waveformInput[(_waveformInPos + _waveformInput.Length - 1) % _waveformInput.Length];
                float rms = rawRms > prevIn ? rawRms : prevIn * 0.9f;
                _waveformInput[_waveformInPos] = rms;
                if (rms > _waveformMaxIn) _waveformMaxIn = rms;
            }
            _waveformInPos = (_waveformInPos + 1) % _waveformInput.Length;
            _waveformMaxIn *= 0.998;
            if (_waveformMaxIn < 0.001) _waveformMaxIn = 0.001;

            // --- 输出 ---
            float outSumSq = 0; int outCount = 0;
            lock (_waveformAudioOutLock)
            {
                int avail2 = (_waveformAudioOutWritePos - _waveformAudioOutReadPos + _waveformAudioOutBuf.Length) % _waveformAudioOutBuf.Length;
                // Skip stale backlog: always show the latest audio (same as input)
                if (avail2 > samplesPerTick)
                {
                    int skip = avail2 - samplesPerTick;
                    _waveformAudioOutReadPos = (_waveformAudioOutReadPos + skip) % _waveformAudioOutBuf.Length;
                    avail2 = samplesPerTick;
                }
                for (int s = 0; s < avail2; s++)
                {
                    float v = _waveformAudioOutBuf[_waveformAudioOutReadPos];
                    outSumSq += v * v; outCount++;
                    _waveformAudioOutReadPos = (_waveformAudioOutReadPos + 1) % _waveformAudioOutBuf.Length;
                }
            }
            {
                float rawRms = outCount > 0 ? (float)Math.Sqrt(outSumSq / outCount) : 0f;
                // Peak envelope: instant attack, slow release (τ≈190ms) — same as input
                float prevOut = _waveformOutput[(_waveformOutPos + _waveformOutput.Length - 1) % _waveformOutput.Length];
                float rms = rawRms > prevOut ? rawRms : prevOut * 0.9f;
                _waveformOutput[_waveformOutPos] = rms;
                if (rms > _waveformMaxOut) _waveformMaxOut = rms;
            }
            _waveformOutPos = (_waveformOutPos + 1) % _waveformOutput.Length;
            _waveformMaxOut *= 0.998;
            if (_waveformMaxOut < 0.001) _waveformMaxOut = 0.001;

            DrawWaveform();
        };
        _waveformTimer.Start();
    }

    private void StopStreaming()
    {
        try
        {
            // Note: waveform timer is kept running so the waveform continues scrolling at 0 amplitude
            // Note: waveform buffers are NOT cleared — audio will gracefully decay to 0 as ring buffers drain

            StopAudioSendLoop();
            _lastSentAudioTsNs = 0;
            _streamStartNs = 0;
            _streamSessionId = unchecked(_streamSessionId + 1);

            if (_waveIn != null)
            {
                _waveIn.StopRecording();
                _waveIn.DataAvailable -= OnAudioDataAvailable;
                _waveIn.Dispose();
                _waveIn = null;
            }

            _captureBuffer = null;
            _captureProvider = null;
            _captureReadBuffer = Array.Empty<byte>();

            if (_waveOut != null)
            {
                _waveOut.Stop();
                _waveOut.PlaybackStopped -= OnPlaybackStopped;
                _waveOut.Dispose();
                _waveOut = null;
            }

            _outputDevice?.Dispose();
            _outputDevice = null;
            _waveProvider = null;
            _playbackStarted = false;
            UpdateStreamingUi(false);
            TotalLatencyTextBlock.Text = "-- ms";
            InferenceLatencyTextBlock.Text = "-- ms";
            Log("音频流已停止");
        }
        catch (Exception ex)
        {
            Log($"停止音频流时出错: {ex.Message}");
        }
    }

    private void StartAudioSendLoop()
    {
        if (_audioSendLoopTask != null && !_audioSendLoopTask.IsCompleted)
        {
            return;
        }

        _streamingCts?.Cancel();
        _streamingCts?.Dispose();
        _streamingCts = new CancellationTokenSource();
        _audioSendLoopTask = Task.Run(() => AudioSendLoopAsync(_streamingCts.Token), _streamingCts.Token);
    }

    private void StopAudioSendLoop()
    {
        _streamingCts?.Cancel();
        _streamingCts?.Dispose();
        _streamingCts = null;

        while (_audioSendQueue.TryDequeue(out _))
        {
        }

        Interlocked.Exchange(ref _audioSendQueueCount, 0);
    }

    private async Task AudioSendLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await _audioSendSignal.WaitAsync(cancellationToken);

                while (_audioSendQueue.TryDequeue(out var messageBytes))
                {
                    Interlocked.Decrement(ref _audioSendQueueCount);
                    await _client.SendBinaryAsync(messageBytes, cancellationToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log($"音频发送循环错误: {ex.Message}");
        }
    }

    private void OnAudioDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded == 0)
        {
            return;
        }

        try
        {
            if (_captureBuffer == null || _captureProvider == null)
            {
                return;
            }

            lock (_captureLock)
            {
                _captureBuffer.AddSamples(e.Buffer, 0, e.BytesRecorded);

                while (true)
                {
                    int read = _captureProvider.Read(_captureReadBuffer, 0, _captureReadBuffer.Length);
                    if (read <= 0)
                    {
                        break;
                    }

                    int alignedRead = read - read % 4;
                    if (alignedRead <= 0)
                    {
                        if (read < _captureReadBuffer.Length)
                        {
                            break;
                        }

                        continue;
                    }

                    if (_bypassServerVoice)
                    {
                        if (_waveProvider == null || _waveOut == null)
                        {
                            break;
                        }

                        _waveProvider.AddSamples(_captureReadBuffer, 0, alignedRead);
                        if (!_playbackStarted && _waveProvider.BufferedBytes > 0)
                        {
                            var bufferedMs = _waveProvider.BufferedDuration.TotalMilliseconds;
                            if (bufferedMs >= Math.Max(_networkSliceMs * 2, 40))
                            {
                                _waveOut.Play();
                                _playbackStarted = true;
                            }
                        }

                        // In bypass mode, input IS the output — write audio to both waveform ring buffers
                        int wfBypassSamples = alignedRead / 4;
                        lock (_waveformAudioInLock)
                        {
                            for (int s = 0; s < wfBypassSamples; s++)
                            {
                                _waveformAudioInBuf[_waveformAudioInWritePos] = BitConverter.ToSingle(_captureReadBuffer, s * 4);
                                _waveformAudioInWritePos = (_waveformAudioInWritePos + 1) % _waveformAudioInBuf.Length;
                            }
                        }
                        lock (_waveformAudioOutLock)
                        {
                            for (int s = 0; s < wfBypassSamples; s++)
                            {
                                _waveformAudioOutBuf[_waveformAudioOutWritePos] = BitConverter.ToSingle(_captureReadBuffer, s * 4);
                                _waveformAudioOutWritePos = (_waveformAudioOutWritePos + 1) % _waveformAudioOutBuf.Length;
                            }
                        }

                        if (read < _captureReadBuffer.Length)
                        {
                            break;
                        }

                        continue;
                    }

                    if (!_client.IsConnected)
                    {
                        if (read < _captureReadBuffer.Length)
                        {
                            break;
                        }

                        continue;
                    }

                    int samples = alignedRead / 4;
                    long nowNs = GetMonoNs();
                    long durationNs = samples * NsPerSample;
                    long tsNs = nowNs - durationNs;
                    long lastTs = Interlocked.Read(ref _lastSentAudioTsNs);
                    if (tsNs <= lastTs)
                    {
                        tsNs = lastTs + 1;
                    }

                    Interlocked.Exchange(ref _lastSentAudioTsNs, tsNs);

                    var messageBytes = new byte[8 + alignedRead];
                    var tsBytes = BitConverter.GetBytes((ulong)tsNs);
                    if (BitConverter.IsLittleEndian)
                    {
                        Array.Reverse(tsBytes);
                    }

                    Array.Copy(tsBytes, 0, messageBytes, 0, 8);
                    Array.Copy(_captureReadBuffer, 0, messageBytes, 8, alignedRead);

                    // Write raw audio to input waveform ring buffer (consumed by timer)
                    int wfInSamples = alignedRead / 4;
                    lock (_waveformAudioInLock)
                    {
                        for (int s = 0; s < wfInSamples; s++)
                        {
                            _waveformAudioInBuf[_waveformAudioInWritePos] = BitConverter.ToSingle(_captureReadBuffer, s * 4);
                            _waveformAudioInWritePos = (_waveformAudioInWritePos + 1) % _waveformAudioInBuf.Length;
                        }
                    }

                    _audioSendQueue.Enqueue(messageBytes);
                    var currentCount = Interlocked.Increment(ref _audioSendQueueCount);
                    var dropped = false;
                    while (currentCount > _maxAudioSendQueuePackets && _audioSendQueue.TryDequeue(out _))
                    {
                        currentCount = Interlocked.Decrement(ref _audioSendQueueCount);
                        dropped = true;
                    }
                    if (dropped)
                    {
                        var now = GetMonoNs();
                        if (now - _lastSendDropLogNs > 2_000_000_000)
                        {
                            _lastSendDropLogNs = now;
                            Dispatcher.UIThread.Post(() => Log("警告: 发送队列溢出，音频丢包"));
                        }
                    }

                    _audioSendSignal.Release();

                    if (read < _captureReadBuffer.Length)
                    {
                        break;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log($"发送音频入队时出错: {ex.Message}");
        }
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception != null)
        {
            Log($"播放错误停止: {e.Exception.Message}");
        }
    }

    private void HandleBinaryMessage(byte[] messageData)
    {
        if (!_isPlaying || _waveProvider == null)
        {
            return;
        }

        try
        {
            const int headerBytes = 12;
            if (messageData.Length < headerBytes)
            {
                return;
            }

            var procTimeBytes = new byte[2];
            Array.Copy(messageData, 0, procTimeBytes, 0, 2);
            if (BitConverter.IsLittleEndian)
            {
                Array.Reverse(procTimeBytes);
            }

            var procTimeMs = BitConverter.ToUInt16(procTimeBytes, 0);

            var queueTimeBytes = new byte[2];
            Array.Copy(messageData, 2, queueTimeBytes, 0, 2);
            if (BitConverter.IsLittleEndian)
            {
                Array.Reverse(queueTimeBytes);
            }

            var queueTimeMs = BitConverter.ToUInt16(queueTimeBytes, 0);

            var tsBytes = new byte[8];
            Array.Copy(messageData, 4, tsBytes, 0, 8);
            if (BitConverter.IsLittleEndian)
            {
                Array.Reverse(tsBytes);
            }

            ulong tsNs = BitConverter.ToUInt64(tsBytes, 0);
            int audioOffset = headerBytes;
            int audioLength = messageData.Length - audioOffset;
            if (audioLength <= 0)
            {
                return;
            }

            double bufferBeforeAddMs = _waveProvider.BufferedDuration.TotalMilliseconds;
            bool shouldAdd = true;
            int effectiveTargetLatency = _useAdaptiveBuffer ? _jitterEstimator.GetTargetBufferMs(10) : _targetBufferLatency;
            if (bufferBeforeAddMs > _maxBufferMs)
            {
                shouldAdd = false;
            }
            else if (bufferBeforeAddMs > effectiveTargetLatency + _silenceDropOffset)
            {
                var rms = CalculateRms(messageData, audioOffset, audioLength);
                if (rms < _silenceThreshold)
                {
                    shouldAdd = false;
                }
            }

            // Always write to waveform ring buffer regardless of shouldAdd,
            // so the output waveform shows activity even when packets are dropped for playback
            int outSamples = audioLength / 4;
            lock (_waveformAudioOutLock)
            {
                for (int s = 0; s < outSamples; s++)
                {
                    _waveformAudioOutBuf[_waveformAudioOutWritePos] = BitConverter.ToSingle(messageData, audioOffset + s * 4);
                    _waveformAudioOutWritePos = (_waveformAudioOutWritePos + 1) % _waveformAudioOutBuf.Length;
                }
            }

            if (!shouldAdd)
            {
                return;
            }

            _waveProvider.AddSamples(messageData, audioOffset, audioLength);
            _jitterEstimator.Update();
            if (!_playbackStarted && _waveOut != null && _waveProvider.BufferedBytes > 0)
            {
                var minStartBufferMs = Math.Max(_networkSliceMs * 3, Math.Max(effectiveTargetLatency, 60));
                if (_waveProvider.BufferedDuration.TotalMilliseconds >= minStartBufferMs)
                {
                    _waveOut.Play();
                    _playbackStarted = true;
                    Log($"缓冲达到 {_waveProvider.BufferedDuration.TotalMilliseconds:F0}ms，开始播放");
                }
            }

            if (tsNs != 0)
            {
                double ageAtReceiveMs = (GetMonoNs() - (long)tsNs) / 1_000_000.0;
                double totalMsNow = ageAtReceiveMs + bufferBeforeAddMs;

                _emaTotalLatencyMs = LatencyEmaAlpha * totalMsNow + (1.0 - LatencyEmaAlpha) * _emaTotalLatencyMs;
                _emaInferLatencyMs = LatencyEmaAlpha * procTimeMs + (1.0 - LatencyEmaAlpha) * _emaInferLatencyMs;
                _emaQueueLatencyMs = LatencyEmaAlpha * queueTimeMs + (1.0 - LatencyEmaAlpha) * _emaQueueLatencyMs;

                _latencySamples.Add(new LatencySample { TsNs = GetMonoNs(), TotalMs = totalMsNow, RttMs = queueTimeMs, InferMs = procTimeMs });

                long cutoff = GetMonoNs() - (long)(LatencySampleWindowSeconds * 1_000_000_000.0);
                while (_latencySamples.Count > 0 && _latencySamples[0].TsNs < cutoff)
                {
                    _latencySamples.RemoveAt(0);
                }

                Dispatcher.UIThread.Post(() =>
                {
                    TotalLatencyTextBlock.Text = $"{_emaTotalLatencyMs:F0} ms";
                    InferenceLatencyTextBlock.Text = $"{_emaInferLatencyMs:F0} ms";
                    NetworkLatencyTextBlock.Text = $"{_emaQueueLatencyMs:F0} ms";
                });
            }
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() => Log($"解析二进制音频消息失败: {ex.Message}"));
        }
    }

    private static MMDevice? TryGetDefaultCapture(MMDeviceEnumerator enumerator, Role role)
    {
        try
        {
            return enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, role);
        }
        catch
        {
            return null;
        }
    }

    private static float CalculateRms(byte[] buffer, int offset, int length)
    {
        float sum = 0;
        int count = length / 4;
        if (count <= 0)
        {
            return 0;
        }

        for (int index = 0; index < count; index++)
        {
            float sample = BitConverter.ToSingle(buffer, offset + index * 4);
            sum += sample * sample;
        }

        return (float)Math.Sqrt(sum / count);
    }

    private void DrawWaveform()
    {
        var canvas = WaveformCanvas;
        if (canvas == null) return;

        var width = canvas.Bounds.Width;
        var height = canvas.Bounds.Height;
        if (width <= 2 || height <= 2) return;

        canvas.Children.Clear();

        var halfH = height / 2.0;
        var amp = halfH - 4;
        int count = _waveformInput.Length;

        double scaleIn = _waveformMaxIn > 0.0001 ? 1.0 / _waveformMaxIn : 100.0;
        double scaleOut = _waveformMaxOut > 0.0001 ? 1.0 / _waveformMaxOut : 100.0;

        // Input: green, top half (向上展开)
        var inputPts = new Avalonia.Points();
        int inStart = _waveformInPos;
        for (int i = 0; i < count; i++)
        {
            int idx = (inStart + i) % count;
            double x = i * width / count;
            double v = Math.Min(_waveformInput[idx] * scaleIn, 1.0);
            double y = halfH - v * amp;
            inputPts.Add(new Avalonia.Point(x, y));
        }
        if (inputPts.Count > 1)
            canvas.Children.Add(new Avalonia.Controls.Shapes.Polyline
            {
                Points = inputPts,
                Stroke = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(76, 175, 80)),
                StrokeThickness = 1,
                IsHitTestVisible = false,
            });

        // Output: red, bottom half (向上展开，与输入同方向)
        var outputPts = new Avalonia.Points();
        int outStart = _waveformOutPos;
        for (int i = 0; i < count; i++)
        {
            int idx = (outStart + i) % count;
            double x = i * width / count;
            double v = Math.Min(_waveformOutput[idx] * scaleOut, 1.0);
            double y = height - 2 - v * amp;
            outputPts.Add(new Avalonia.Point(x, y));
        }
        if (outputPts.Count > 1)
            canvas.Children.Add(new Avalonia.Controls.Shapes.Polyline
            {
                Points = outputPts,
                Stroke = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(244, 67, 54)),
                StrokeThickness = 1,
                IsHitTestVisible = false,
            });
    }

    private void InferenceExpander_OnClick(object? sender, RoutedEventArgs e)
    {
        var isExpanded = !InferenceExpanderPanel.Classes.Contains("collapsed");
        if (isExpanded)
        {
            InferenceExpanderPanel.Classes.Add("collapsed");
            ((Button)InferenceExpanderToggle).Content = "▶ 高级推理参数";
        }
        else
        {
            InferenceExpanderPanel.Classes.Remove("collapsed");
            ((Button)InferenceExpanderToggle).Content = "▼ 高级推理参数";
        }
    }

    private void BufferExpander_OnClick(object? sender, RoutedEventArgs e)
    {
        var isExpanded = !BufferExpanderPanel.Classes.Contains("collapsed");
        if (isExpanded)
        {
            BufferExpanderPanel.Classes.Add("collapsed");
            ((Button)BufferExpanderToggle).Content = "▶ 缓冲与网络";
        }
        else
        {
            BufferExpanderPanel.Classes.Remove("collapsed");
            ((Button)BufferExpanderToggle).Content = "▼ 缓冲与网络";
        }
    }

    private void TrackHover(object? sender, Avalonia.Input.PointerEventArgs e)
    {
        var hit = this.GetVisualsAt(e.GetPosition(this)).OfType<Control>().FirstOrDefault();
        if (hit == _lastHovered) return;
        _lastHovered?.Classes.Remove("hover");
        hit?.Classes.Add("hover");
        _lastHovered = hit;
    }

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    private void MinimizeBtn_Click(object? sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void MaximizeBtn_Click(object? sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
        MaximizeIcon.IsVisible = WindowState != WindowState.Maximized;
        RestoreIcon.IsVisible = WindowState == WindowState.Maximized;
    }

    private void CloseBtn_Click(object? sender, RoutedEventArgs e)
        => Close();

    private void MainWindow_KeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (e.Key != Avalonia.Input.Key.F12) return;
        var now = DateTime.UtcNow;
        if ((now - _lastF12Time).TotalSeconds > 3)
            _f12Count = 0;
        _f12Count++;
        _lastF12Time = now;
        if (_f12Count >= 5 && !_debugMode)
        {
            _debugMode = true;
            if (!_voiceModelsSelection.Contains(_serverRawVoiceModelItem))
            {
                _voiceModelsSelection.Add(_serverRawVoiceModelItem);
            }
            Log("调试模式已启用。输出原声(经服务器) 已可用。");
        }
    }

    private void Log(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        var current = LogTextBox.Text ?? string.Empty;
        LogTextBox.Text = string.IsNullOrWhiteSpace(current)
            ? line
            : current + Environment.NewLine + line;
    }

    private void ShowErrorToast(string message)
    {
        var text = string.IsNullOrWhiteSpace(message) ? "连接失败" : message;
        var toast = new Border
        {
            Classes = { "toast-panel", "collapsed" },
            Child = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                Spacing = 10,
                Children =
                {
                    new MaterialIcon
                    {
                        Kind = MaterialIconKind.AlertCircleOutline,
                        Width = 18,
                        Height = 18,
                        Foreground = Brushes.IndianRed,
                        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    },
                    new TextBlock
                    {
                        Text = text,
                        Foreground = Brushes.IndianRed,
                        FontWeight = FontWeight.SemiBold,
                        FontSize = 15,
                        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    },
                },
            },
        };

        ToastHostPanel.Children.Add(toast);
        Dispatcher.UIThread.Post(() => toast.Classes.Remove("collapsed"), DispatcherPriority.Background);

        var holdTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.0) };
        EventHandler? holdTick = null;
        holdTick = (_, _) =>
        {
            holdTimer.Stop();
            holdTimer.Tick -= holdTick;

            toast.Classes.Add("collapsed");

            // Wait for all transitions (MaxHeight + Opacity + Margin + Transform all 200ms) to finish
            var removeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(230) };
            EventHandler? removeTick = null;
            removeTick = (_, _) =>
            {
                removeTimer.Stop();
                removeTimer.Tick -= removeTick;
                ToastHostPanel.Children.Remove(toast);
            };
            removeTimer.Tick += removeTick;
            removeTimer.Start();
        };
        holdTimer.Tick += holdTick;
        holdTimer.Start();
    }
}
