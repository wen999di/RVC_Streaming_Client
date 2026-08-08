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

    private readonly record struct WaveformPoint(long TimestampNs, float Rms);

    private sealed class WaveformAccumulator
    {
        public long FrameIndex { get; set; } = long.MinValue;
        public double SumSquares { get; set; }
        public int SampleCount { get; set; }

        public void Reset()
        {
            FrameIndex = long.MinValue;
            SumSquares = 0.0;
            SampleCount = 0;
        }
    }

    private sealed class PlaybackWaveformAccumulator
    {
        public double SumSquares { get; set; }
        public int SampleCount { get; set; }
        public long FirstMediaTimestampNs { get; set; }
        public long LastMediaTimestampNs { get; set; }

        public void Reset()
        {
            SumSquares = 0.0;
            SampleCount = 0;
            FirstMediaTimestampNs = 0;
            LastMediaTimestampNs = 0;
        }
    }
    private sealed class PlaybackTimestampSegment
    {
        public PlaybackTimestampSegment(long nextTimestampNs, int remainingSamples)
        {
            NextTimestampNs = nextTimestampNs;
            RemainingSamples = remainingSamples;
        }

        public long NextTimestampNs { get; set; }
        public int RemainingSamples { get; set; }
    }

    private sealed class PlaybackTapWaveProvider : IWaveProvider
    {
        private readonly BufferedWaveProvider _source;
        private readonly object _sync;
        private readonly Action<byte[], int, int, int> _onRead;

        public PlaybackTapWaveProvider(
            BufferedWaveProvider source,
            object sync,
            Action<byte[], int, int, int> onRead)
        {
            _source = source;
            _sync = sync;
            _onRead = onRead;
        }

        public WaveFormat WaveFormat => _source.WaveFormat;

        public int Read(byte[] buffer, int offset, int count)
        {
            lock (_sync)
            {
                int bufferedBytesBeforeRead = _source.BufferedBytes;
                int read = _source.Read(buffer, offset, count);
                int mediaBytesRead = Math.Min(read, bufferedBytesBeforeRead);
                mediaBytesRead -= mediaBytesRead % WaveFormat.BlockAlign;
                if (read > 0)
                {
                    _onRead(buffer, offset, read, mediaBytesRead);
                }
                return read;
            }
        }
    }
    private const string DefaultServerUri = "ws://127.0.0.1:8765/";
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
    private List<string>? _activeDragFilenames;
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
    private IWaveProvider? _playbackWaveProvider;
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
    private long _nextCaptureAudioTsNs;
    private long _streamStartNs;
    private int _streamSessionId;
    private double _emaTotalLatencyMs;
    private double _emaInferLatencyMs;
    private double _emaQueueLatencyMs;
    private bool _hasLatencyEstimate;
    private int _effectiveServerBlockMs;
    private int _pendingLatencyReset;
    private const double LatencyEmaAlpha = 0.2;
    private bool _isPlaying;
    private bool _playbackStarted;
    private bool _bypassServerVoice;
    private bool _serverPassthroughVoice;

    // 波形显示
    // 声卡实际播放每累计 20ms 样本就生成一对输入/输出 RMS 点，分辨率与网络切片无关。
    // 输入通过播放样本的媒体时间戳回查；两条曲线共用播放时间轴和固定 dBFS 量程。
    private const int WaveformFrameSamples = SampleRate / 50;
    private const long WaveformFrameDurationNs = WaveformFrameSamples * NsPerSample;
    private const long WaveformInterpolationMaxGapNs = WaveformFrameDurationNs * 8;
    private const long CaptureTimestampResyncThresholdNs = 1_000_000_000L;
    private const long WaveformWindowNs = 8_000_000_000L;
    private const long WaveformRetentionNs = 30_000_000_000L;
    private const double WaveformFloorDb = -60.0;
    private const double WaveformCeilingDb = 0.0;
    private readonly List<WaveformPoint> _waveformInputHistory = new();
    private readonly List<WaveformPoint> _waveformInputSourceHistory = new();
    private readonly List<WaveformPoint> _waveformOutputHistory = new();
    private readonly WaveformAccumulator _waveformInputAccumulator = new();
    private readonly PlaybackWaveformAccumulator _waveformPlaybackAccumulator = new();
    private readonly Queue<PlaybackTimestampSegment> _playbackTimestampSegments = new();
    private readonly object _playbackTimestampSync = new();
    private long _playbackExpectedTimestampNs;
    private long _waveformPlaybackTimelineNs;
    private readonly object _waveformInputLock = new();
    private readonly object _waveformOutputLock = new();
    private readonly object _waveformInputSourceLock = new();
    private long _waveformLastDataWallNs;
    private long _waveformDisplayEndNs;
    private long _waveformDisplayLastTickNs;
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
        InlinePthDropBorder.AddHandler(DragDrop.DragLeaveEvent, (_, _) => RestoreDragAvailabilityHighlight(InlinePthDropBorder));
        InlineIndexDropBorder.AddHandler(DragDrop.DragOverEvent, InlineIndexBorder_DragOver);
        InlineIndexDropBorder.AddHandler(DragDrop.DropEvent, InlineIndexBorder_Drop);
        InlineIndexDropBorder.AddHandler(DragDrop.DragLeaveEvent, (_, _) => RestoreDragAvailabilityHighlight(InlineIndexDropBorder));

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

        const double indicatorInset = 12.0;
        var targetX = origin.Value.X + indicatorInset;
        var targetWidth = Math.Max(0, selectedButton.Bounds.Width - indicatorInset * 2);
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
                serverUri = DefaultServerUri;
                ServerUriTextBox.Text = serverUri;
                Log($"未指定服务器地址，使用本地默认地址：{serverUri}");
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
            var hasPth = (e.DataTransfer.TryGetText() ?? string.Empty)
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Any(filename => filename.Trim().EndsWith(".pth", StringComparison.OrdinalIgnoreCase));
            e.DragEffects = hasPth ? DragDropEffects.Copy : DragDropEffects.None;
            SetSlotHighlight(InlinePthDropBorder, true, !hasPth);
            e.Handled = true;
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
            SetSlotHighlight(VoiceModelsDropZoneBorder, true, !hasPth);
            e.Handled = true;
        }
        else
        {
            e.DragEffects = DragDropEffects.None;
        }
    }

    private void VoiceModels_DragLeave(object? sender, RoutedEventArgs e)
    {
        RestoreDragAvailabilityHighlight(VoiceModelsDropZoneBorder);
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
                       .Select(t => t.Trim()).FirstOrDefault(t => t.EndsWith(".pth", StringComparison.OrdinalIgnoreCase));
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
            var hasIndex = (e.DataTransfer.TryGetText() ?? string.Empty)
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Any(filename => filename.Trim().EndsWith(".index", StringComparison.OrdinalIgnoreCase));
            e.DragEffects = hasIndex ? DragDropEffects.Copy : DragDropEffects.None;
            SetSlotHighlight(InlineIndexDropBorder, true, !hasIndex);
            e.Handled = true;
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
                       .Select(t => t.Trim()).FirstOrDefault(t => t.EndsWith(".index", StringComparison.OrdinalIgnoreCase));
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
        _activeDragFilenames = selected;
        RefreshDragAvailabilityHighlights();

        try
        {
            await DragDrop.DoDragDropAsync(dragEvent, data, DragDropEffects.Copy);
        }
        finally
        {
            // DragLeave is not always fired when the drag is cancelled or released
            // outside a drop target, so always clear the guidance here.
            _activeDragFilenames = null;
            RefreshDragAvailabilityHighlights();
        }
    }

    private void SlotBorder_DragOver(object? sender, Avalonia.Input.DragEventArgs e)
    {
        if (sender is not Border border || border.Tag is not string slot) return;

        if (e.DataTransfer.Contains(DataFormat.Text))
        {
            var text = e.DataTransfer.TryGetText() ?? string.Empty;
            var filenames = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                               .Select(f => f.Trim()).Where(f => !string.IsNullOrWhiteSpace(f)).ToList();
            var allValid = filenames.Count > 0 && filenames.All(f => IsFilenameAllowedForSlot(slot, f));
            e.DragEffects = allValid ? DragDropEffects.Copy : DragDropEffects.None;
            SetSlotHighlight(border, true, !allValid);
            e.Handled = true;
        }
        else
        {
            e.DragEffects = DragDropEffects.None;
        }
    }

    private void SlotBorder_DragLeave(object? sender, EventArgs e)
    {
        RestoreDragAvailabilityHighlight(sender);
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

        var desiredClass = active
            ? invalid ? "drag-invalid" : "drag-valid"
            : null;

        if (desiredClass != "drag-valid")
            border.Classes.Remove("drag-valid");
        else if (!border.Classes.Contains("drag-valid"))
            border.Classes.Add("drag-valid");

        if (desiredClass != "drag-invalid")
            border.Classes.Remove("drag-invalid");
        else if (!border.Classes.Contains("drag-invalid"))
            border.Classes.Add("drag-invalid");
    }

    private void RefreshDragAvailabilityHighlights()
    {
        foreach (var border in new[] { HubertSlotBorder, RmvpeSlotBorder, VoiceModelsDropZoneBorder, InlinePthDropBorder, InlineIndexDropBorder })
        {
            RestoreDragAvailabilityHighlight(border);
        }
    }

    private void RestoreDragAvailabilityHighlight(object? sender)
    {
        if (sender is not Border border || _activeDragFilenames is not { Count: > 0 } filenames)
        {
            SetSlotHighlight(sender, false);
            return;
        }

        var isAvailable = border switch
        {
            _ when ReferenceEquals(border, VoiceModelsDropZoneBorder) =>
                filenames.Any(filename => filename.EndsWith(".pth", StringComparison.OrdinalIgnoreCase)),
            _ when ReferenceEquals(border, InlinePthDropBorder) =>
                border.IsVisible && filenames.Any(filename => filename.EndsWith(".pth", StringComparison.OrdinalIgnoreCase)),
            _ when ReferenceEquals(border, InlineIndexDropBorder) =>
                border.IsVisible && filenames.Any(filename => filename.EndsWith(".index", StringComparison.OrdinalIgnoreCase)),
            _ when border.Tag is string slot =>
                filenames.All(filename => IsFilenameAllowedForSlot(slot, filename)),
            _ => false,
        };

        SetSlotHighlight(border, isAvailable);
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

        bool isSameSelection = string.Equals(_selectedVoiceModelId, id, StringComparison.Ordinal);
        if (isSameSelection
            && !string.Equals(id, VoiceModelItem.RawId, StringComparison.Ordinal)
            && !string.Equals(id, VoiceModelItem.ServerRawId, StringComparison.Ordinal)
            && _modelState is ModelState.Loading or ModelState.Ready)
        {
            Log(_modelState == ModelState.Ready ? "当前模型已就绪，无需重复加载。" : "当前模型正在准备中，请稍候。");
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
            _realtimeConfigDebounceTimer?.Stop();
            Interlocked.Exchange(ref _realtimeConfigDebouncePending, 0);
            _lastSentConfig.Clear();
            _lastSentConfigSeq = 0;
            _effectiveServerBlockMs = 0;
            Interlocked.Exchange(ref _pendingLatencyReset, 0);
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
                {
                    long ackSeq = _lastSentConfigSeq;
                    if (root.TryGetProperty("seq", out var seqElement) && seqElement.TryGetInt64(out var seqValue))
                    {
                        ackSeq = seqValue;
                    }

                    if (ackSeq != _lastSentConfigSeq)
                    {
                        Log($"忽略过期配置确认 (ACK: {ackSeq}, Latest: {_lastSentConfigSeq})");
                        break;
                    }

                    if (root.TryGetProperty("effective", out var effectiveElement)
                        && effectiveElement.TryGetProperty("block_ms", out var blockMsElement)
                        && blockMsElement.TryGetInt32(out var acknowledgedBlockMs))
                    {
                        bool blockChanged = _effectiveServerBlockMs > 0 && acknowledgedBlockMs != _effectiveServerBlockMs;
                        _effectiveServerBlockMs = acknowledgedBlockMs;

                        if (blockChanged && _isPlaying)
                        {
                            Interlocked.Exchange(ref _pendingLatencyReset, 1);
                            TotalLatencyTextBlock.Text = "-- ms";
                            InferenceLatencyTextBlock.Text = "-- ms";
                            Log($"服务端分块已切换为 {acknowledgedBlockMs}ms，正在重新校准自动缓冲。");
                        }
                    }
                    SetModelState(ModelState.Ready);
                    SetActiveModelLoadingState(isLoading: false);
                    if (root.TryGetProperty("hash", out var hashElement))
                    {
                        var serverHash = hashElement.GetString() ?? string.Empty;
                        var localHash = ComputeConfigHash(_lastSentConfig);
                        if (!string.Equals(serverHash, localHash, StringComparison.OrdinalIgnoreCase))
                        {
                            Log("[WARN] 配置不一致，正在强制同步...");
                            _ = SendConfigurationAsync(true);
                        }
                    }
                    break;
                }
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

        var readinessKeys = new[]
        {
            "model_path",
            "index_path",
            "f0method",
            "passthrough",
        };
        bool readinessChanged = readinessKeys.Any(key =>
            !_lastSentConfig.TryGetValue(key, out var previous)
            || !Equals(previous, currentConfig[key]));

        float previousIndexRate = _lastSentConfig.TryGetValue("index_rate", out var previousIndexRateValue)
            ? Convert.ToSingle(previousIndexRateValue)
            : 0.0f;
        bool indexUsageChanged = (previousIndexRate > 0.0f) != (indexRate > 0.0f);
        readinessChanged |= indexUsageChanged;

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
        if (readinessChanged || _modelState != ModelState.Ready)
        {
            SetModelState(ModelState.Loading);
        }
        await _client.SendCommandAsync(new { config = diffConfig, seq });
        Log($"已发送配置 (Keys: {diffConfig.Count})");
    }

    private void ResetLatencyTracking()
    {
        _jitterEstimator.Reset();
        _emaTotalLatencyMs = 0;
        _emaInferLatencyMs = 0;
        _emaQueueLatencyMs = 0;
        _hasLatencyEstimate = false;
        _latencySamples.Clear();
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
        _nextCaptureAudioTsNs = 0;
        _streamSessionId = unchecked(_streamSessionId + 1);
        Interlocked.Exchange(ref _pendingLatencyReset, 0);
        ResetLatencyTracking();

        _waveProvider = new BufferedWaveProvider(WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, Channels))
        {
            BufferDuration = TimeSpan.FromMilliseconds(_bufferCapacityMs),
            DiscardOnBufferOverflow = true,
            ReadFully = true,
        };
        ResetWaveformHistory();
        _playbackWaveProvider = new PlaybackTapWaveProvider(_waveProvider, _playbackTimestampSync, CapturePlaybackWaveform);

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
        _waveOut.Init(_playbackWaveProvider);
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

        _waveformTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _waveformTimer.Tick += (_, _) => DrawWaveform();
        _waveformTimer.Start();
    }

    private void ResetWaveformHistory()
    {
        lock (_waveformInputSourceLock)
        {
            _waveformInputSourceHistory.Clear();
            _waveformInputAccumulator.Reset();
        }
        lock (_waveformInputLock)
        {
            _waveformInputHistory.Clear();
        }
        lock (_waveformOutputLock)
        {
            _waveformOutputHistory.Clear();
            _waveformPlaybackAccumulator.Reset();
        }
        lock (_playbackTimestampSync)
        {
            _playbackTimestampSegments.Clear();
            _playbackExpectedTimestampNs = 0;
        }
        _waveformPlaybackTimelineNs = 0;
        Interlocked.Exchange(ref _waveformLastDataWallNs, GetMonoNs());
        _waveformDisplayEndNs = 0;
        _waveformDisplayLastTickNs = GetMonoNs();
        DrawWaveform();
    }
    private void AppendInputSourceSamples(
        List<WaveformPoint> history,
        WaveformAccumulator accumulator,
        object historyLock,
        byte[] buffer,
        int offset,
        int count,
        long startTimestampNs)
    {
        int alignedCount = count - count % 4;
        long streamOriginNs = Interlocked.Read(ref _streamStartNs);
        if (alignedCount <= 0 || startTimestampNs <= 0 || streamOriginNs <= 0)
        {
            return;
        }

        int sampleCount = alignedCount / 4;
        lock (historyLock)
        {
            for (int sampleIndex = 0; sampleIndex < sampleCount; sampleIndex++)
            {
                long sampleTimestampNs = startTimestampNs + sampleIndex * NsPerSample;
                long relativeTimestampNs = sampleTimestampNs - streamOriginNs;
                if (relativeTimestampNs < 0)
                {
                    continue;
                }

                long frameIndex = relativeTimestampNs / WaveformFrameDurationNs;
                if (accumulator.FrameIndex == long.MinValue)
                {
                    accumulator.FrameIndex = frameIndex;
                }
                else if (frameIndex < accumulator.FrameIndex)
                {
                    continue;
                }
                else if (frameIndex > accumulator.FrameIndex)
                {
                    CommitWaveformFrame(history, accumulator, streamOriginNs);
                    accumulator.FrameIndex = frameIndex;
                    accumulator.SumSquares = 0.0;
                    accumulator.SampleCount = 0;
                }

                float sample = BitConverter.ToSingle(buffer, offset + sampleIndex * 4);
                accumulator.SumSquares += sample * sample;
                accumulator.SampleCount++;
            }

            if (history.Count > 0)
            {
                long cutoffNs = history[^1].TimestampNs - WaveformRetentionNs;
                int removeCount = 0;
                while (removeCount < history.Count && history[removeCount].TimestampNs < cutoffNs)
                {
                    removeCount++;
                }
                if (removeCount > 0)
                {
                    history.RemoveRange(0, removeCount);
                }
            }
        }

        Interlocked.Exchange(ref _waveformLastDataWallNs, GetMonoNs());
    }

    private static void CommitWaveformFrame(
        List<WaveformPoint> history,
        WaveformAccumulator accumulator,
        long streamOriginNs)
    {
        if (accumulator.FrameIndex == long.MinValue)
        {
            return;
        }

        float rms = accumulator.SampleCount > 0
            ? (float)Math.Sqrt(accumulator.SumSquares / accumulator.SampleCount)
            : 0f;
        long centerTimestampNs = streamOriginNs
            + accumulator.FrameIndex * WaveformFrameDurationNs
            + WaveformFrameDurationNs / 2;
        history.Add(new WaveformPoint(centerTimestampNs, rms));
    }

    private bool TryFindInputSourceRms(long mediaTimestampNs, out float rms)
    {
        rms = 0f;
        if (mediaTimestampNs <= 0)
        {
            return false;
        }

        lock (_waveformInputSourceLock)
        {
            if (_waveformInputSourceHistory.Count == 0)
            {
                return false;
            }

            int low = 0;
            int high = _waveformInputSourceHistory.Count - 1;
            while (low <= high)
            {
                int middle = low + (high - low) / 2;
                if (_waveformInputSourceHistory[middle].TimestampNs < mediaTimestampNs)
                {
                    low = middle + 1;
                }
                else
                {
                    high = middle - 1;
                }
            }

            if (low > 0 && low < _waveformInputSourceHistory.Count)
            {
                var before = _waveformInputSourceHistory[low - 1];
                var after = _waveformInputSourceHistory[low];
                long gapNs = after.TimestampNs - before.TimestampNs;
                if (gapNs > 0 && gapNs <= WaveformInterpolationMaxGapNs)
                {
                    double amount = (double)(mediaTimestampNs - before.TimestampNs) / gapNs;
                    rms = (float)(before.Rms + (after.Rms - before.Rms) * amount);
                    return true;
                }
            }

            int nearestIndex = Math.Clamp(low, 0, _waveformInputSourceHistory.Count - 1);
            if (nearestIndex > 0)
            {
                long beforeDistance = Math.Abs(
                    _waveformInputSourceHistory[nearestIndex - 1].TimestampNs - mediaTimestampNs);
                long afterDistance = Math.Abs(
                    _waveformInputSourceHistory[nearestIndex].TimestampNs - mediaTimestampNs);
                if (beforeDistance <= afterDistance)
                {
                    nearestIndex--;
                }
            }

            var nearest = _waveformInputSourceHistory[nearestIndex];
            if (Math.Abs(nearest.TimestampNs - mediaTimestampNs) > WaveformInterpolationMaxGapNs)
            {
                return false;
            }

            rms = nearest.Rms;
            return true;
        }
    }

    private void AppendPlaybackComparisonSamples(
        byte[] buffer,
        int offset,
        int count,
        long startMediaTimestampNs,
        bool hasMediaTimestamp)
    {
        int alignedCount = count - count % 4;
        if (alignedCount <= 0)
        {
            return;
        }

        int sampleCount = alignedCount / 4;
        lock (_waveformOutputLock)
        {
            for (int sampleIndex = 0; sampleIndex < sampleCount; sampleIndex++)
            {
                float sample = BitConverter.ToSingle(buffer, offset + sampleIndex * 4);
                _waveformPlaybackAccumulator.SumSquares += sample * sample;
                _waveformPlaybackAccumulator.SampleCount++;

                if (hasMediaTimestamp && startMediaTimestampNs > 0)
                {
                    long mediaTimestampNs = startMediaTimestampNs + sampleIndex * NsPerSample;
                    if (_waveformPlaybackAccumulator.FirstMediaTimestampNs == 0)
                    {
                        _waveformPlaybackAccumulator.FirstMediaTimestampNs = mediaTimestampNs;
                    }
                    _waveformPlaybackAccumulator.LastMediaTimestampNs = mediaTimestampNs;
                }

                if (_waveformPlaybackAccumulator.SampleCount < WaveformFrameSamples)
                {
                    continue;
                }

                float outputRms = (float)Math.Sqrt(
                    _waveformPlaybackAccumulator.SumSquares / WaveformFrameSamples);
                long mediaCenterTimestampNs = _waveformPlaybackAccumulator.FirstMediaTimestampNs > 0
                    ? (_waveformPlaybackAccumulator.FirstMediaTimestampNs
                        + _waveformPlaybackAccumulator.LastMediaTimestampNs) / 2
                    : 0;
                float inputRms = TryFindInputSourceRms(mediaCenterTimestampNs, out float matchedInputRms)
                    ? matchedInputRms
                    : 0f;

                if (_waveformPlaybackTimelineNs == 0)
                {
                    _waveformPlaybackTimelineNs = GetMonoNs();
                }
                else
                {
                    _waveformPlaybackTimelineNs += WaveformFrameDurationNs;
                }

                lock (_waveformInputLock)
                {
                    _waveformInputHistory.Add(new WaveformPoint(_waveformPlaybackTimelineNs, inputRms));
                    long inputCutoffNs = _waveformPlaybackTimelineNs - WaveformRetentionNs;
                    int inputRemoveCount = 0;
                    while (inputRemoveCount < _waveformInputHistory.Count
                        && _waveformInputHistory[inputRemoveCount].TimestampNs < inputCutoffNs)
                    {
                        inputRemoveCount++;
                    }
                    if (inputRemoveCount > 0)
                    {
                        _waveformInputHistory.RemoveRange(0, inputRemoveCount);
                    }
                }

                _waveformOutputHistory.Add(new WaveformPoint(_waveformPlaybackTimelineNs, outputRms));
                long outputCutoffNs = _waveformPlaybackTimelineNs - WaveformRetentionNs;
                int outputRemoveCount = 0;
                while (outputRemoveCount < _waveformOutputHistory.Count
                    && _waveformOutputHistory[outputRemoveCount].TimestampNs < outputCutoffNs)
                {
                    outputRemoveCount++;
                }
                if (outputRemoveCount > 0)
                {
                    _waveformOutputHistory.RemoveRange(0, outputRemoveCount);
                }

                _waveformPlaybackAccumulator.Reset();
            }
        }

        Interlocked.Exchange(ref _waveformLastDataWallNs, GetMonoNs());
    }
    private void AddPlaybackSamples(byte[] buffer, int offset, int count, long startTimestampNs)
    {
        int alignedCount = count - count % 4;
        var provider = _waveProvider;
        if (provider == null || alignedCount <= 0)
        {
            return;
        }

        lock (_playbackTimestampSync)
        {
            provider.AddSamples(buffer, offset, alignedCount);
            if (startTimestampNs > 0)
            {
                int sampleCount = alignedCount / 4;
                _playbackTimestampSegments.Enqueue(new PlaybackTimestampSegment(startTimestampNs, sampleCount));
            }
        }
    }

    private void CapturePlaybackWaveform(byte[] buffer, int offset, int count, int mediaBytesRead)
    {
        int alignedCount = count - count % 4;
        int alignedMediaBytes = Math.Min(alignedCount, mediaBytesRead - mediaBytesRead % 4);
        int mediaSamples = alignedMediaBytes / 4;
        int totalSamples = alignedCount / 4;
        int consumedMediaSamples = 0;

        while (consumedMediaSamples < mediaSamples)
        {
            int samplesToAppend;
            long segmentTimestampNs;
            if (_playbackTimestampSegments.Count > 0)
            {
                var segment = _playbackTimestampSegments.Peek();
                samplesToAppend = Math.Min(mediaSamples - consumedMediaSamples, segment.RemainingSamples);
                segmentTimestampNs = segment.NextTimestampNs;
                segment.NextTimestampNs += samplesToAppend * NsPerSample;
                segment.RemainingSamples -= samplesToAppend;
                if (segment.RemainingSamples == 0)
                {
                    _playbackTimestampSegments.Dequeue();
                }
            }
            else
            {
                if (_playbackExpectedTimestampNs <= 0)
                {
                    break;
                }
                samplesToAppend = mediaSamples - consumedMediaSamples;
                segmentTimestampNs = _playbackExpectedTimestampNs;
            }

            AppendPlaybackComparisonSamples(
                buffer,
                offset + consumedMediaSamples * 4,
                samplesToAppend * 4,
                segmentTimestampNs,
                true);
            consumedMediaSamples += samplesToAppend;
            _playbackExpectedTimestampNs = segmentTimestampNs + samplesToAppend * NsPerSample;
        }

        int zeroSamples = totalSamples - mediaSamples;
        if (zeroSamples > 0)
        {
            AppendPlaybackComparisonSamples(
                buffer,
                offset + mediaSamples * 4,
                zeroSamples * 4,
                0,
                false);
        }
    }
    private void StopStreaming()
    {
        try
        {
            // Keep the timer and timestamp histories so the shared window scrolls away naturally after stopping.
            // Histories are reset on the next StartStreaming call.

            StopAudioSendLoop();
            _nextCaptureAudioTsNs = 0;
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
            _playbackWaveProvider = null;
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

    private long GetNextCaptureTimestampNs(int sampleCount)
    {
        long durationNs = sampleCount * NsPerSample;
        long observedStartNs = GetMonoNs() - durationNs;
        long expectedStartNs = Interlocked.Read(ref _nextCaptureAudioTsNs);

        long startTimestampNs = expectedStartNs;
        if (expectedStartNs <= 0
            || Math.Abs(observedStartNs - expectedStartNs) > CaptureTimestampResyncThresholdNs)
        {
            startTimestampNs = observedStartNs;
        }

        Interlocked.Exchange(ref _nextCaptureAudioTsNs, startTimestampNs + durationNs);
        return startTimestampNs;
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

                        // Input uses fixed media-time buckets; output is committed only when the device reads it.
                        long waveformStartNs = GetNextCaptureTimestampNs(alignedRead / 4);
                        AppendInputSourceSamples(_waveformInputSourceHistory, _waveformInputAccumulator, _waveformInputSourceLock, _captureReadBuffer, 0, alignedRead, waveformStartNs);
                        AddPlaybackSamples(_captureReadBuffer, 0, alignedRead, waveformStartNs);

                        if (!_playbackStarted && _waveProvider.BufferedBytes > 0)
                        {
                            var bufferedMs = _waveProvider.BufferedDuration.TotalMilliseconds;
                            if (bufferedMs >= Math.Max(_networkSliceMs * 2, 40))
                            {
                                _waveOut.Play();
                                _playbackStarted = true;
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

                    long tsNs = GetNextCaptureTimestampNs(alignedRead / 4);

                    var messageBytes = new byte[8 + alignedRead];
                    var tsBytes = BitConverter.GetBytes((ulong)tsNs);
                    if (BitConverter.IsLittleEndian)
                    {
                        Array.Reverse(tsBytes);
                    }

                    Array.Copy(tsBytes, 0, messageBytes, 0, 8);
                    Array.Copy(_captureReadBuffer, 0, messageBytes, 8, alignedRead);
                    AppendInputSourceSamples(_waveformInputSourceHistory, _waveformInputAccumulator, _waveformInputSourceLock, _captureReadBuffer, 0, alignedRead, tsNs);


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
            long arrivalNs = GetMonoNs();
            bool hasValidMediaTimestamp = _streamStartNs > 0
                && tsNs >= (ulong)_streamStartNs
                && tsNs <= (ulong)long.MaxValue;
            int audioOffset = headerBytes;
            int audioLength = messageData.Length - audioOffset;
            if (audioLength <= 0)
            {
                return;
            }


            double bufferBeforeAddMs = _waveProvider.BufferedDuration.TotalMilliseconds;
            if (hasValidMediaTimestamp)
            {
                if (Interlocked.Exchange(ref _pendingLatencyReset, 0) != 0)
                {
                    ResetLatencyTracking();
                }
                _jitterEstimator.Update((long)tsNs, arrivalNs);
            }

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

            if (!shouldAdd)
            {
                return;
            }

            AddPlaybackSamples(messageData, audioOffset, audioLength, hasValidMediaTimestamp ? (long)tsNs : 0);
            if (!_playbackStarted && _waveOut != null && _waveProvider.BufferedBytes > 0)
            {
                var minStartBufferMs = Math.Max(effectiveTargetLatency, 40);
                if (_waveProvider.BufferedDuration.TotalMilliseconds >= minStartBufferMs)
                {
                    _waveOut.Play();
                    _playbackStarted = true;
                    Log($"缓冲达到 {_waveProvider.BufferedDuration.TotalMilliseconds:F0}ms，开始播放");
                }
            }

            if (hasValidMediaTimestamp)
            {
                double ageAtReceiveMs = (arrivalNs - (long)tsNs) / 1_000_000.0;
                double totalMsNow = ageAtReceiveMs + bufferBeforeAddMs;

                if (!_hasLatencyEstimate)
                {
                    _emaTotalLatencyMs = totalMsNow;
                    _emaInferLatencyMs = procTimeMs;
                    _emaQueueLatencyMs = queueTimeMs;
                    _hasLatencyEstimate = true;
                }
                else
                {
                    _emaTotalLatencyMs = LatencyEmaAlpha * totalMsNow + (1.0 - LatencyEmaAlpha) * _emaTotalLatencyMs;
                    _emaInferLatencyMs = LatencyEmaAlpha * procTimeMs + (1.0 - LatencyEmaAlpha) * _emaInferLatencyMs;
                    _emaQueueLatencyMs = LatencyEmaAlpha * queueTimeMs + (1.0 - LatencyEmaAlpha) * _emaQueueLatencyMs;
                }

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

        double width = canvas.Bounds.Width;
        double height = canvas.Bounds.Height;
        if (width <= 2 || height <= 2) return;

        WaveformPoint[] inputHistory;
        WaveformPoint[] outputHistory;
        lock (_waveformInputLock)
        {
            inputHistory = _waveformInputHistory.ToArray();
        }
        lock (_waveformOutputLock)
        {
            outputHistory = _waveformOutputHistory.ToArray();
        }

        canvas.Children.Clear();
        if (inputHistory.Length == 0 && outputHistory.Length == 0)
        {
            return;
        }

        long latestInputNs = inputHistory.Length > 0 ? inputHistory[^1].TimestampNs : long.MaxValue;
        long latestOutputNs = outputHistory.Length > 0 ? outputHistory[^1].TimestampNs : long.MaxValue;
        long availableEndNs = latestInputNs == long.MaxValue
            ? latestOutputNs
            : latestOutputNs == long.MaxValue
                ? latestInputNs
                : Math.Min(latestInputNs, latestOutputNs);

        long nowNs = GetMonoNs();
        long elapsedNs = _waveformDisplayLastTickNs > 0
            ? Math.Clamp(nowNs - _waveformDisplayLastTickNs, 0, 250_000_000L)
            : 0;
        _waveformDisplayLastTickNs = nowNs;

        // WasapiOut reads in batches; keep a short visual lead so 60 FPS scrolling stays continuous.
        const long smoothingBufferNs = 120_000_000L;

        if (_waveformDisplayEndNs == 0)
        {
            _waveformDisplayEndNs = availableEndNs - smoothingBufferNs;
        }
        else if (_isPlaying)
        {
            long availableLeadNs = availableEndNs - _waveformDisplayEndNs;
            if (_waveformDisplayEndNs > availableEndNs || availableLeadNs > smoothingBufferNs * 3)
            {
                _waveformDisplayEndNs = availableEndNs - smoothingBufferNs;
            }
            else
            {
                _waveformDisplayEndNs = Math.Min(_waveformDisplayEndNs + elapsedNs, availableEndNs);
            }
        }
        else
        {
            _waveformDisplayEndNs += elapsedNs;
        }

        long endTimestampNs = _waveformDisplayEndNs;

        long startTimestampNs = endTimestampNs - WaveformWindowNs;
        double halfHeight = height / 2.0;
        double amplitude = halfHeight - 4.0;

        Avalonia.Points BuildPoints(WaveformPoint[] history, double baselineY)
        {
            var points = new Avalonia.Points();
            foreach (var point in history)
            {
                if (point.TimestampNs < startTimestampNs || point.TimestampNs > endTimestampNs)
                {
                    continue;
                }

                double x = (point.TimestampNs - startTimestampNs) * width / WaveformWindowNs;
                double db = 20.0 * Math.Log10(Math.Max(point.Rms, 0.000001f));
                double normalized = Math.Clamp(
                    (db - WaveformFloorDb) / (WaveformCeilingDb - WaveformFloorDb),
                    0.0,
                    1.0);
                points.Add(new Avalonia.Point(x, baselineY - normalized * amplitude));
            }
            return points;
        }

        var inputPoints = BuildPoints(inputHistory, halfHeight);
        if (inputPoints.Count > 1)
        {
            canvas.Children.Add(new Avalonia.Controls.Shapes.Polyline
            {
                Points = inputPoints,
                Stroke = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(76, 175, 80)),
                StrokeThickness = 1,
                IsHitTestVisible = false,
            });
        }

        var outputPoints = BuildPoints(outputHistory, height - 2.0);
        if (outputPoints.Count > 1)
        {
            canvas.Children.Add(new Avalonia.Controls.Shapes.Polyline
            {
                Points = outputPoints,
                Stroke = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(244, 67, 54)),
                StrokeThickness = 1,
                IsHitTestVisible = false,
            });
        }
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
