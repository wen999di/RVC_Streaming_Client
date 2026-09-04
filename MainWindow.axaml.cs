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
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
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
        public double ServerQueueMs { get; init; }
        public double InferMs { get; init; }
    }

    private sealed class HubDownloadClientOperation
    {
        public required TaskCompletionSource<HubDownloadResult> Completion { get; init; }
        public required IProgress<HubDownloadProgress> Progress { get; init; }
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
        private readonly Func<int, int, bool> _shouldHold;
        private readonly Action<byte[], int, int, int> _onRead;

        public PlaybackTapWaveProvider(
            BufferedWaveProvider source,
            object sync,
            Func<int, int, bool> shouldHold,
            Action<byte[], int, int, int> onRead)
        {
            _source = source;
            _sync = sync;
            _shouldHold = shouldHold;
            _onRead = onRead;
        }

        public WaveFormat WaveFormat => _source.WaveFormat;

        public int Read(byte[] buffer, int offset, int count)
        {
            lock (_sync)
            {
                int alignedCount = count - count % WaveFormat.BlockAlign;
                int bufferedBytesBeforeRead = _source.BufferedBytes;
                if (_shouldHold(bufferedBytesBeforeRead, alignedCount))
                {
                    Array.Clear(buffer, offset, count);
                    _onRead(buffer, offset, count, 0);
                    return count;
                }

                int read = alignedCount > 0 ? _source.Read(buffer, offset, alignedCount) : 0;
                int mediaBytesRead = Math.Min(read, bufferedBytesBeforeRead);
                mediaBytesRead -= mediaBytesRead % WaveFormat.BlockAlign;
                if (read < count)
                {
                    Array.Clear(buffer, offset + read, count - read);
                }
                _onRead(buffer, offset, count, mediaBytesRead);
                return count;
            }
        }
    }
    private const string DefaultServerUri = "ws://127.0.0.1:8765/";
    private const int SampleRate = 16000;
    private const int Channels = 1;
    private const long NsPerSample = 1_000_000_000L / SampleRate;
    private const double LatencySampleWindowSeconds = 10.0;
    private const int AudioDeviceBufferMs = 30;
    private const int AdaptiveSchedulerSlackMs = 5;
    private const long AdaptiveStatusUpdateIntervalNs = 250_000_000L;
    private const long LatencyUiUpdateIntervalNs = 250_000_000L;
    private static readonly HashSet<string> TrainingAudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".wav", ".flac", ".mp3", ".m4a", ".ogg", ".opus",
    };

    private static class InferenceDefaults
    {
        public const int SpeakerId = 0;
        public const int F0UpKey = 0;
        public const float BlockTimeSeconds = 0.25f;
        public const float CrossfadeSeconds = 0.04f;
        public const float ExtraTimeSeconds = 2.0f;
        public const float FormantShift = 0.0f;
        public const string F0Method = "rmvpe";
        public const float IndexRate = 0.5f;
        public const float SilenceDbThreshold = -70.0f;
        public const float SilenceGateAttenuation = 0.0f;
        public const bool InputNoiseReduce = false;
        public const bool OutputNoiseReduce = false;
        public const float NoiseReduceStrength = 0.9f;
        public const float RmsMixRate = 0.8f;
    }

    private readonly RvcClientService _client = new();
    private readonly ObservableCollection<VoiceModelItem> _voiceModelsSelection = new();
    private readonly ObservableCollection<VoiceModelItem> _voiceModelsManagement = new();
    private readonly ObservableCollection<ServerFileItem> _serverFiles = new();
    private readonly ObservableCollection<AudioDeviceItem> _audioInputDevices = new();
    private readonly ObservableCollection<AudioDeviceItem> _audioOutputDevices = new();
    private readonly ObservableCollection<LogFileItem> _serverLogFiles = new();
    private readonly ObservableCollection<SlotBindingItem> _hubertSlotItems = new();
    private readonly ObservableCollection<SlotBindingItem> _rmvpeSlotItems = new();
    private readonly ObservableCollection<SlotBindingItem> _pymssWeightSlotItems = new();
    private readonly ObservableCollection<SlotBindingItem> _pymssConfigSlotItems = new();
    private readonly ObservableCollection<SlotBindingItem> _pretrainedGeneratorSlotItems = new();
    private readonly ObservableCollection<SlotBindingItem> _pretrainedDiscriminatorSlotItems = new();
    private readonly ObservableCollection<TrainingJobItem> _trainingJobs = new();
    private readonly ObservableCollection<TrainingAudioItem> _trainingAudioFiles = new();
    private readonly ObservableCollection<TrainingSpeakerGroup> _trainingSpeakerGroups = new();
    private readonly HashSet<string> _hiddenTrainingAudioFiles = new(StringComparer.OrdinalIgnoreCase);
    private bool _trainingNameAutoManaged = true;
    private bool _settingTrainingName;
    private bool _trainingOrganizePending;
    private string _inlinePendingPth = string.Empty;
    private string _inlinePendingIndex = string.Empty;
    private readonly VoiceModelItem _rawVoiceModelItem = new() { Id = VoiceModelItem.RawId, Name = "输出原声", Pth = string.Empty, Index = string.Empty, IsActive = false, ShowStatusDot = false };
    private readonly VoiceModelItem _serverRawVoiceModelItem = new() { Id = VoiceModelItem.ServerRawId, Name = "输出原声(经服务器)", Pth = string.Empty, Index = string.Empty, IsActive = false, ShowStatusDot = false };
    private readonly JitterEstimator _jitterEstimator = new() { DeviceBufferMs = AudioDeviceBufferMs };
    private readonly ConcurrentQueue<byte[]> _audioSendQueue = new();
    private SemaphoreSlim? _audioSendSignal;
    private readonly List<LatencySample> _latencySamples = new();
    private readonly object _captureLock = new();
    private readonly List<ServerFileItem> _serverFilesRaw = new();
    private readonly List<ServerFileItem> _uploadingFiles = new();
    private readonly Dictionary<string, ServerFileItem> _serverFileCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _boundFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _expandedServerFolders = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ServerFileItem> _serverFolderCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _serverFolderAnimationVersions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<TextBox, int> _sliderEditAnimationVersions = new();
    private readonly HashSet<ContextMenu> _removeMenusAnimatingClose = new();
    private readonly HashSet<ContextMenu> _removeMenusAllowedToClose = new();
    private int _serverFileReflowVersion;
    private readonly Dictionary<string, HashSet<string>> _slotAllowedExt = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ServerFileItem> _uploadItemsById = new();
    private readonly ConcurrentDictionary<string, long> _uploadOffsetCorrections = new();
    private readonly SemaphoreSlim _uploadSerialLock = new(1, 1);
    private TaskCompletionSource<HubRepositorySnapshot>? _hubRepositoryTcs;
    private readonly ConcurrentDictionary<string, HubDownloadClientOperation> _hubDownloadOperations = new();

    private bool _suppressSlotSelectionChanged;
    private bool _suppressModelCardSelectionChanged;
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
    private bool _useLocalServer;
    private string _localServerDirectory = AppPaths.DefaultLocalServerDirectory;
    private bool _localServerEnvironmentVerified;
    private bool _connectionActionBusy;
    private string _fileSortMode = "time_desc";
    private bool _hideBoundFiles;
    private string _recentUnloadedVoiceModelId = string.Empty;
    private string? _pendingPreloadModelId;
    private string _lastBaseModelSlotWarning = string.Empty;
    private readonly HashSet<string> _failedVoiceModelIds = new(StringComparer.Ordinal);
    private string _modelPath = string.Empty;
    private string _indexPath = string.Empty;
    private int _speakerId = InferenceDefaults.SpeakerId;
    private int _f0UpKey = InferenceDefaults.F0UpKey;
    private float _blockTime = InferenceDefaults.BlockTimeSeconds;
    private float _crossfadeLength = InferenceDefaults.CrossfadeSeconds;
    private float _extraTime = InferenceDefaults.ExtraTimeSeconds;
    private int _serverStreamChunkMs = 20;
    private float _formantShift = InferenceDefaults.FormantShift;
    private string _f0Method = InferenceDefaults.F0Method;
    private float _indexRate = InferenceDefaults.IndexRate;
    private float _silenceDbThreshold = InferenceDefaults.SilenceDbThreshold;
    private float _silenceGateAtten = InferenceDefaults.SilenceGateAttenuation;
    private bool _inputNoiseReduce = InferenceDefaults.InputNoiseReduce;
    private bool _outputNoiseReduce = InferenceDefaults.OutputNoiseReduce;
    private float _noiseReducePropDecrease = InferenceDefaults.NoiseReduceStrength;
    private float _rmsMixRate = InferenceDefaults.RmsMixRate;

    private readonly Dictionary<string, object> _lastSentConfig = new();
    private long _configSeq;
    private long _lastSentConfigSeq;
    private string? _lastConfigHashRetry;
    private DispatcherTimer? _realtimeConfigDebounceTimer;
    private DispatcherTimer? _trainingPollTimer;
    private DispatcherTimer? _settingsSaveTimer;
    private int _realtimeConfigDebouncePending;

    private bool _useAdaptiveBuffer = true;
    private int _targetBufferLatency = 40;
    private int _maxBufferMs = 500;
    private int _bufferCapacityMs = 1500;
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
    private int _maxAudioSendQueuePackets = 8;
    private long _lastSendDropLogNs;
    private TaskCompletionSource<(string UploadId, string Name, long ReceivedBytes, long TotalBytes)>? _uploadReadyTcs;
    private TaskCompletionSource<(string UploadId, string FinalName)>? _uploadDoneTcs;

    private long _monoBaseTimestamp;
    private long _streamStartNs;
    private long _streamSessionId;
    private int _audioSequence;
    private long _captureMediaCursorNs;
    private double _emaTotalLatencyMs;
    private double _emaInferLatencyMs;
    private double _emaServerQueueLatencyMs;
    private bool _hasLatencyEstimate;
    private uint _lastOutputSequence;
    private bool _hasOutputSequence;
    private int _effectiveServerBlockMs;
    private int _effectiveServerChunkMs;
    private int _pendingLatencyReset;
    private long _lastLatencyUiUpdateNs;
    private const double LatencyEmaAlpha = 0.2;
    private bool _isPlaying;
    private int _captureActive;
    private bool _playbackStarted;
    private int _adaptiveRebuffering;
    private int _adaptiveUnderrunCount;
    private long _lastAdaptiveStatusUpdateNs;
    private bool _bypassServerVoice;
    private bool _serverPassthroughVoice;
    private bool _serverConfigurationAccepted;

    // 波形显示
    // 声卡实际播放每累计 20ms 样本就生成一对输入/输出 RMS 点，分辨率与网络切片无关。
    // 输入通过播放样本的媒体时间戳回查；两条曲线共用播放时间轴和固定 dBFS 量程。
    private const int WaveformFrameSamples = SampleRate / 50;
    private const long WaveformFrameDurationNs = WaveformFrameSamples * NsPerSample;
    private const long WaveformInterpolationMaxGapNs = WaveformFrameDurationNs * 8;
    private const long CaptureTimestampResyncThresholdNs = 250_000_000L;
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
    private TranslateTransform? _mainTabPageTransform;
    private int _lastMainTabIndex;
    private int _mainTabPageAnimationVersion;

    public MainWindow()
    {
        Program.AppendStartupTrace("MainWindow: ctor enter");
        InitializeComponent();
        InitializeMainTabPageAnimation();
        KeyDown += MainWindow_KeyDown;
        MainTabControl.SelectionChanged += OnMainTabControlSelectionChanged;
        Opened += (_, _) => Dispatcher.UIThread.Post(() => UpdateMainTabHeaderVisual(false), DispatcherPriority.Loaded);
        MainTabsHeaderGrid.SizeChanged += (_, _) => UpdateMainTabHeaderVisual(false);
        AddHandler(InputElement.PointerPressedEvent, GlobalPointerPressed_CommitSliderEdit, RoutingStrategies.Tunnel);
        AddHandler(InputElement.PointerPressedEvent, GlobalPointerPressed_ClearModelCardSelection, RoutingStrategies.Tunnel);
        PointerMoved += TrackHover;
        PointerExited += (_, _) => { _lastHovered?.Classes.Remove("hover"); _lastHovered = null; };
        Program.AppendStartupTrace("MainWindow: InitializeComponent completed");

        // Set up drag-drop handlers for slot borders
        foreach (var border in new[]
                 {
                     HubertSlotBorder, RmvpeSlotBorder, PymssWeightSlotBorder, PymssConfigSlotBorder,
                     PretrainedGeneratorSlotBorder, PretrainedDiscriminatorSlotBorder,
                 })
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
        PymssWeightSlotListBox.ItemsSource = _pymssWeightSlotItems;
        PymssConfigSlotListBox.ItemsSource = _pymssConfigSlotItems;
        PretrainedGeneratorSlotListBox.ItemsSource = _pretrainedGeneratorSlotItems;
        PretrainedDiscriminatorSlotListBox.ItemsSource = _pretrainedDiscriminatorSlotItems;
        _hubertSlotItems.CollectionChanged += (_, _) => UpdateSlotPlaceholderVisibility();
        _rmvpeSlotItems.CollectionChanged += (_, _) => UpdateSlotPlaceholderVisibility();
        _pymssWeightSlotItems.CollectionChanged += (_, _) => UpdateSlotPlaceholderVisibility();
        _pymssConfigSlotItems.CollectionChanged += (_, _) => UpdateSlotPlaceholderVisibility();
        _pretrainedGeneratorSlotItems.CollectionChanged += (_, _) => UpdateSlotPlaceholderVisibility();
        _pretrainedDiscriminatorSlotItems.CollectionChanged += (_, _) => UpdateSlotPlaceholderVisibility();
        UpdateSlotPlaceholderVisibility();
        TrainingJobsListBox.ItemsSource = _trainingJobs;
        TrainingSpeakerGroupsList.ItemsSource = _trainingSpeakerGroups;
        _trainingNameAutoManaged = true;
        UpdateSuggestedTrainingName();
        FileSortComboBox.SelectedIndex = 0;

        _client.LogReceived += Client_OnLogReceived;
        _client.ConnectionStateChanged += Client_OnConnectionStateChanged;
        _client.TextMessageReceived += Client_OnTextMessageReceived;
    _client.BinaryMessageReceived += Client_OnBinaryMessageReceived;

    _realtimeConfigDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
    _realtimeConfigDebounceTimer.Tick += async (_, _) => await FlushRealtimeConfigAsync();
        _settingsSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _settingsSaveTimer.Tick += (_, _) =>
        {
            _settingsSaveTimer.Stop();
            SaveClientSettingsNow();
        };
        _trainingPollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _trainingPollTimer.Tick += async (_, _) =>
        {
            if (_client.IsConnected)
            {
                await _client.SendCommandAsync(new { command = "training_list" });
            }
        };
        _trainingPollTimer.Start();
        Program.AppendStartupTrace("MainWindow: debounce timer prepared");

        SeedPreviewData();
        Program.AppendStartupTrace("MainWindow: preview data seeded");
        LoadClientSettings();
        InitializeSettingsUi();
        _uiInitialized = true;
        RefreshAdaptiveBufferStatus(GetEffectiveTargetBufferMs(), force: true);
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

    private void LoadClientSettings()
    {
        var settings = ClientSettingsStore.Load();
        var savedServerUri = settings.ServerUri?.Trim() ?? string.Empty;
        ServerUriTextBox.Text = string.Equals(savedServerUri, DefaultServerUri, StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : savedServerUri;
        _useLocalServer = string.Equals(settings.ConnectionMode, "local", StringComparison.OrdinalIgnoreCase);
        _localServerDirectory = NormalizeLocalServerDirectory(settings.LocalServerDirectory);
        _localServerEnvironmentVerified = settings.LocalServerEnvironmentVerified
            && LocalServerEnvironmentChecker.HasInstalledEnvironment(_localServerDirectory);
        UpdateConnectionModeUi();

        _f0UpKey = Math.Clamp(settings.F0UpKey, -12, 12);
        _blockTime = Math.Clamp(settings.BlockTimeSeconds, 0.08f, 1.0f);
        _crossfadeLength = Math.Clamp(settings.CrossfadeSeconds, 0.01f, 0.04f);
        _extraTime = Math.Clamp(settings.ExtraTimeSeconds, 0.2f, 4.0f);
        _serverStreamChunkMs = Math.Clamp(settings.ServerStreamChunkMs, 10, 120);
        _formantShift = Math.Clamp(settings.FormantShift, -2.0f, 2.0f);
        _f0Method = string.Equals(settings.F0Method, "fcpe", StringComparison.OrdinalIgnoreCase)
            ? "fcpe"
            : "rmvpe";
        _indexRate = Math.Clamp(settings.IndexRate, 0.0f, 1.0f);
        _silenceDbThreshold = Math.Clamp(settings.SilenceDbThreshold, -90.0f, -20.0f);
        _silenceGateAtten = Math.Clamp(settings.SilenceGateAttenuation, 0.0f, 1.0f);
        _inputNoiseReduce = settings.InputNoiseReduce;
        _outputNoiseReduce = settings.OutputNoiseReduce;
        _noiseReducePropDecrease = Math.Clamp(settings.NoiseReduceStrength, 0.0f, 1.0f);
        _rmsMixRate = Math.Clamp(settings.RmsMixRate, 0.0f, 1.0f);

        _useAdaptiveBuffer = settings.UseAdaptiveBuffer;
        _targetBufferLatency = Math.Clamp(settings.TargetBufferLatencyMs, 20, 500);
        _maxBufferMs = Math.Clamp(settings.MaxBufferMs, 100, 3000);
        _bufferCapacityMs = Math.Clamp(settings.BufferCapacityMs, 1000, 8000);
        _networkSliceMs = Math.Clamp(settings.NetworkSliceMs, 10, 120);
        if (!_useAdaptiveBuffer && _serverStreamChunkMs > _targetBufferLatency)
        {
            _targetBufferLatency = _serverStreamChunkMs;
        }
        _jitterEstimator.JitterFactor = Math.Clamp(settings.JitterFactor, 1.0, 5.0);
        _jitterEstimator.Alpha = Math.Clamp(settings.JitterAlpha, 0.80, 0.99);
        _jitterEstimator.MaxBufferMs = Math.Clamp(settings.JitterMaxBufferMs, 20.0, 500.0);
        _jitterEstimator.MinNetworkProtectionMs = Math.Clamp(settings.MinNetworkProtectionMs, 0.0, 120.0);
    }

    private void ScheduleClientSettingsSave()
    {
        if (!_uiInitialized || _settingsSaveTimer is null) return;
        _settingsSaveTimer.Stop();
        _settingsSaveTimer.Start();
    }

    private void SaveClientSettingsNow()
    {
        if (!_uiInitialized) return;
        try
        {
            ClientSettingsStore.Save(new ClientSettings
            {
                ServerUri = ServerUriTextBox.Text?.Trim() ?? string.Empty,
                ConnectionMode = _useLocalServer ? "local" : "remote",
                LocalServerDirectory = _localServerDirectory,
                LocalServerEnvironmentVerified = _localServerEnvironmentVerified,
                F0UpKey = _f0UpKey,
                BlockTimeSeconds = _blockTime,
                CrossfadeSeconds = _crossfadeLength,
                ExtraTimeSeconds = _extraTime,
                ServerStreamChunkMs = _serverStreamChunkMs,
                FormantShift = _formantShift,
                F0Method = _f0Method,
                IndexRate = _indexRate,
                SilenceDbThreshold = _silenceDbThreshold,
                SilenceGateAttenuation = _silenceGateAtten,
                InputNoiseReduce = _inputNoiseReduce,
                OutputNoiseReduce = _outputNoiseReduce,
                NoiseReduceStrength = _noiseReducePropDecrease,
                RmsMixRate = _rmsMixRate,
                UseAdaptiveBuffer = _useAdaptiveBuffer,
                TargetBufferLatencyMs = _targetBufferLatency,
                MaxBufferMs = _maxBufferMs,
                BufferCapacityMs = _bufferCapacityMs,
                NetworkSliceMs = _networkSliceMs,
                JitterFactor = _jitterEstimator.JitterFactor,
                JitterAlpha = _jitterEstimator.Alpha,
                JitterMaxBufferMs = _jitterEstimator.MaxBufferMs,
                MinNetworkProtectionMs = _jitterEstimator.MinNetworkProtectionMs,
            });
        }
        catch (Exception ex)
        {
            Log($"保存客户端参数失败: {ex.Message}");
        }
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
        MinBufferSlider.Value = _jitterEstimator.MinNetworkProtectionMs;
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
        BlockTimeValueText.Text = $"{BlockTimeSlider.Value:F0}";
        CrossfadeValueText.Text = $"{CrossfadeSlider.Value:F0}";
        ExtraTimeValueText.Text = $"{ExtraTimeSlider.Value:F0}";
        ServerStreamChunkValueText.Text = $"{ServerStreamChunkSlider.Value:F0}";
        SilenceDbValueText.Text = $"{SilenceDbSlider.Value:F0}";
        SilenceGateAttenValueText.Text = SilenceGateAttenSlider.Value.ToString("0.00");
        NoiseReduceStrengthValueText.Text = NoiseReduceStrengthSlider.Value.ToString("0.00");
        RmsMixRateValueText.Text = RmsMixRateSlider.Value.ToString("0.00");

        JitterFactorValueText.Text = JitterFactorSlider.Value.ToString("0.0");
        JitterAlphaValueText.Text = JitterAlphaSlider.Value.ToString("0.00");
        JitterMaxBufferValueText.Text = $"{JitterMaxBufferSlider.Value:F0}";
        MinBufferValueText.Text = $"{MinBufferSlider.Value:F0}";
        TargetBufferValueText.Text = $"{TargetBufferSlider.Value:F0}";
        MaxBufferValueText.Text = $"{MaxBufferSlider.Value:F0}";
        BufferCapacityValueText.Text = $"{BufferCapacitySlider.Value:F0}";
        NetworkSliceValueText.Text = $"{NetworkSliceSlider.Value:F0}";
    }

    private static void SetAnimatedVisibility(Control control, bool isVisible)
    {
        control.Classes.Set("collapsed", !isVisible);
        control.IsEnabled = isVisible;
        control.IsHitTestVisible = isVisible;
    }

    // ── 自定义页签头横条动画 ─────────────────────────────────────────────────────────

    private void InitializeMainTabPageAnimation()
    {
        _mainTabPageTransform = new TranslateTransform();
        MainTabControl.RenderTransform = _mainTabPageTransform;
        MainTabControl.RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);
        _lastMainTabIndex = Math.Max(0, MainTabControl.SelectedIndex);
    }

    private void AnimateMainTabPageIn(int targetIndex)
    {
        if (_mainTabPageTransform is null)
        {
            _lastMainTabIndex = targetIndex;
            return;
        }

        var direction = targetIndex >= _lastMainTabIndex ? 1.0 : -1.0;
        _lastMainTabIndex = targetIndex;
        var animationVersion = ++_mainTabPageAnimationVersion;

        // First establish the incoming page pose without animating, then let it settle
        // into place. The short directional offset keeps navigation light and readable.
        MainTabControl.Transitions = new Transitions();
        _mainTabPageTransform.Transitions = new Transitions();
        MainTabControl.Opacity = 0.72;
        _mainTabPageTransform.X = direction * 14.0;

        MainTabControl.Transitions = new Transitions
        {
            new DoubleTransition
            {
                Property = Visual.OpacityProperty,
                Duration = TimeSpan.FromMilliseconds(190),
                Easing = new CubicEaseOut(),
            },
        };
        _mainTabPageTransform.Transitions = new Transitions
        {
            new DoubleTransition
            {
                Property = TranslateTransform.XProperty,
                Duration = TimeSpan.FromMilliseconds(230),
                Easing = new CubicEaseOut(),
            },
        };

        Dispatcher.UIThread.Post(() =>
        {
            if (animationVersion != _mainTabPageAnimationVersion) return;
            MainTabControl.Opacity = 1.0;
            _mainTabPageTransform.X = 0.0;
        }, DispatcherPriority.Loaded);
    }

    private static double EaseInOut(double t) =>
        t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;

    private Button? GetMainTabHeaderButton(int index) => index switch
    {
        0 => MainTabHeaderBtn0,
        1 => MainTabHeaderBtn1,
        2 => MainTabHeaderBtn2,
        3 => MainTabHeaderBtn3,
        _ => null,
    };

    private void MainTabHeaderButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string tag) return;
        if (!int.TryParse(tag, out var idx)) return;
        if (idx < 0 || idx > 3) return;
        MainTabControl.SelectedIndex = idx;
    }

    private void OnMainTabControlSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        var targetIndex = Math.Max(0, MainTabControl.SelectedIndex);
        if (targetIndex != _lastMainTabIndex)
        {
            AnimateMainTabPageIn(targetIndex);
        }
        UpdateMainTabHeaderVisual(true);
    }

    private void UpdateMainTabHeaderVisual(bool animate)
    {
        var idx = MainTabControl.SelectedIndex;
        if (idx < 0) idx = 0;

        MainTabHeaderBtn0.Classes.Set("active", idx == 0);
        MainTabHeaderBtn1.Classes.Set("active", idx == 1);
        MainTabHeaderBtn2.Classes.Set("active", idx == 2);
        MainTabHeaderBtn3.Classes.Set("active", idx == 3);

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
            _connectionActionBusy = true;
            UpdateConnectionActionAvailability();
            if (_client.IsConnected)
            {
                await _client.DisconnectAsync();
                UpdateConnectionUi(false);
                return;
            }

            if (_useLocalServer)
            {
                if (!_localServerEnvironmentVerified)
                {
                    Log("请先在本地环境设置中检查依赖并保存。");
                    ShowErrorToast("本地环境尚未就绪");
                    return;
                }
                if (!LocalServerEnvironmentChecker.HasInstalledEnvironment(_localServerDirectory))
                {
                    _localServerEnvironmentVerified = false;
                    UpdateLocalEnvironmentUi();
                    ScheduleClientSettingsSave();
                    Log("本地 Server 环境已失效，请重新检查依赖。");
                    ShowErrorToast("请重新检查本地环境");
                    return;
                }
                Log("正在启动本地 Server 并建立私有进程管道...");
                await _client.ConnectLocalAsync(_localServerDirectory);
            }
            else
            {
                var serverUri = ServerUriTextBox.Text?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(serverUri))
                {
                    serverUri = DefaultServerUri;
                    Log($"未指定服务器地址，使用本地默认地址：{serverUri}");
                }

                if (!Uri.TryCreate(serverUri, UriKind.Absolute, out _))
                {
                    Log("无效的 URI 格式。");
                    return;
                }
                await _client.ConnectAsync(serverUri);
            }
            _serverConfigurationAccepted = false;
            UpdateConnectionUi(true);
            ScheduleClientSettingsSave();
            await SendConfigurationAsync(true);
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
            _connectionActionBusy = false;
            UpdateConnectionActionAvailability();
        }
    }

    private async void StreamingToggleButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_isPlaying)
        {
            StopStreaming();
            return;
        }

        try
        {
            await StartStreamingAsync();
        }
        catch (Exception ex)
        {
            Log($"启动变声失败: {ex.Message}");
            StopStreaming();
            UpdateStreamingUi(false);
        }
    }

    private void RefreshAudioDevices_OnClick(object? sender, RoutedEventArgs e)
    {
        RefreshAudioDevices();
        Log("已刷新音频设备列表。");
    }

    private void RemoveContextMenu_OnOpened(object? sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu)
        {
            return;
        }

        _removeMenusAnimatingClose.Remove(menu);
        _removeMenusAllowedToClose.Remove(menu);
        menu.IsHitTestVisible = true;

        // Establish the opening pose before the popup is rendered, then ease it
        // into place. The transform is owned by this menu instance so template
        // menus never animate one another.
        menu.Transitions = new Transitions();
        menu.Opacity = 0.0;
        var transform = new TranslateTransform { Y = -6.0 };
        menu.RenderTransform = transform;
        menu.RenderTransformOrigin = new RelativePoint(0.5, 0.0, RelativeUnit.Relative);
        menu.Transitions = new Transitions
        {
            new DoubleTransition
            {
                Property = Visual.OpacityProperty,
                Duration = TimeSpan.FromMilliseconds(135),
                Easing = new CubicEaseOut(),
            },
        };
        transform.Transitions = new Transitions
        {
            new DoubleTransition
            {
                Property = TranslateTransform.YProperty,
                Duration = TimeSpan.FromMilliseconds(165),
                Easing = new CubicEaseOut(),
            },
        };

        Dispatcher.UIThread.Post(() =>
        {
            if (!menu.IsOpen)
            {
                return;
            }

            menu.Opacity = 1.0;
            transform.Y = 0.0;
        }, DispatcherPriority.Loaded);
    }

    private void RemoveContextMenu_OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (sender is not ContextMenu menu)
        {
            return;
        }

        // The second close request is ours, after the fade-out has completed.
        if (_removeMenusAllowedToClose.Remove(menu))
        {
            return;
        }

        e.Cancel = true;
        if (!_removeMenusAnimatingClose.Add(menu))
        {
            return;
        }

        menu.IsHitTestVisible = false;
        menu.Opacity = 0.0;
        if (menu.RenderTransform is TranslateTransform transform)
        {
            transform.Y = -4.0;
        }

        DispatcherTimer.RunOnce(() =>
        {
            if (!_removeMenusAnimatingClose.Remove(menu) || !menu.IsOpen)
            {
                return;
            }

            _removeMenusAllowedToClose.Add(menu);
            menu.Close();
        }, TimeSpan.FromMilliseconds(145));
    }

    private void RemoveContextMenu_OnClosed(object? sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu)
        {
            return;
        }

        _removeMenusAnimatingClose.Remove(menu);
        _removeMenusAllowedToClose.Remove(menu);
        menu.IsHitTestVisible = true;
        menu.Opacity = 0.0;
    }

    private async void RefreshServerFiles_OnClick(object? sender, RoutedEventArgs e)
    {
        if (!_client.IsConnected) return;
        await _client.SendCommandAsync(new { command = "files_list" });
    }

    private async void HubDownload_OnClick(object? sender, RoutedEventArgs e)
    {
        if (!_client.IsConnected) return;
        var destinationOptions = _serverFolderCache.Keys
            .Append("downloads")
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path.Count(character => character == '/'))
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var dialog = new HubDownloadWindow(BrowseHubRepositoryAsync, DownloadHubFilesAsync, destinationOptions);
        await dialog.ShowDialog<bool>(this);
    }

    private async Task<HubRepositorySnapshot> BrowseHubRepositoryAsync(
        string provider,
        string repository,
        string revision)
    {
        if (!_client.IsConnected) throw new InvalidOperationException("尚未连接服务器");
        var completion = new TaskCompletionSource<HubRepositorySnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var previous = Interlocked.Exchange(ref _hubRepositoryTcs, completion);
        previous?.TrySetCanceled();
        try
        {
            await _client.SendCommandAsync(new
            {
                command = "hub_repo_list",
                provider,
                repo = repository,
                revision,
            });
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(90));
        }
        finally
        {
            Interlocked.CompareExchange(ref _hubRepositoryTcs, null, completion);
        }
    }

    private void ConnectionModeButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_client.IsConnected || sender is not Button button) return;
        _useLocalServer = string.Equals(button.Tag?.ToString(), "local", StringComparison.OrdinalIgnoreCase);
        UpdateConnectionModeUi();
        ScheduleClientSettingsSave();
    }

    private void UpdateConnectionModeUi()
    {
        RemoteConnectionModeButton.Classes.Set("active", !_useLocalServer);
        LocalConnectionModeButton.Classes.Set("active", _useLocalServer);
        ServerUriTextBox.IsVisible = !_useLocalServer;
        LocalEnvironmentStatusPanel.IsVisible = _useLocalServer;
        LocalEnvironmentSettingsButton.IsVisible = _useLocalServer;
        ConnectionToggleButton.Content = _useLocalServer ? "启动并连接" : "连接";
        ConnectionToggleButton.MinWidth = _useLocalServer ? 94 : 72;
        UpdateLocalEnvironmentUi();
        UpdateConnectionActionAvailability();
    }

    private async void LocalEnvironmentSettings_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_client.IsConnected || _connectionActionBusy) return;
        var dialog = new LocalEnvironmentWindow(_localServerDirectory, _localServerEnvironmentVerified);
        var configuration = await dialog.ShowDialog<LocalEnvironmentConfiguration?>(this);
        if (configuration is null) return;

        _localServerDirectory = Path.GetFullPath(configuration.ServerDirectory);
        _localServerEnvironmentVerified = configuration.IsVerified;
        UpdateLocalEnvironmentUi();
        UpdateConnectionActionAvailability();
        ScheduleClientSettingsSave();
        Log($"本地环境设置已保存：{_localServerDirectory}");
    }

    private static string NormalizeLocalServerDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return AppPaths.DefaultLocalServerDirectory;
        try
        {
            return Path.GetFullPath(directory);
        }
        catch
        {
            return AppPaths.DefaultLocalServerDirectory;
        }
    }

    private void UpdateLocalEnvironmentUi()
    {
        LocalEnvironmentStatusPanel.Classes.Set("ready", _localServerEnvironmentVerified);
        LocalEnvironmentStatusIcon.Kind = _localServerEnvironmentVerified
            ? MaterialIconKind.CheckCircleOutline
            : MaterialIconKind.ClockOutline;
        LocalEnvironmentStatusTextBlock.Text = _localServerEnvironmentVerified
            ? "本地环境已就绪"
            : "本地环境未设置";
        ToolTip.SetTip(LocalEnvironmentStatusPanel, _localServerDirectory);
    }

    private void UpdateConnectionActionAvailability()
    {
        var canEditConnection = !_client.IsConnected && !_connectionActionBusy;
        ServerUriTextBox.IsEnabled = canEditConnection;
        LocalEnvironmentSettingsButton.IsEnabled = canEditConnection;
        RemoteConnectionModeButton.IsEnabled = canEditConnection;
        LocalConnectionModeButton.IsEnabled = canEditConnection;
        ConnectionToggleButton.IsEnabled = !_connectionActionBusy
            && (!_useLocalServer || _localServerEnvironmentVerified);
    }

    private async Task<HubDownloadResult> DownloadHubFilesAsync(
        HubDownloadRequest request,
        IProgress<HubDownloadProgress> progress,
        CancellationToken cancellationToken)
    {
        if (!_client.IsConnected) throw new InvalidOperationException("尚未连接服务器");
        var requestId = Guid.NewGuid().ToString("D");
        var completion = new TaskCompletionSource<HubDownloadResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = new HubDownloadClientOperation { Completion = completion, Progress = progress };
        if (!_hubDownloadOperations.TryAdd(requestId, operation))
        {
            throw new InvalidOperationException("无法创建下载任务");
        }

        using var cancellation = cancellationToken.Register(() =>
        {
            _ = _client.SendCommandAsync(new { command = "hub_download_cancel", request_id = requestId });
        });
        try
        {
            await _client.SendCommandAsync(new
            {
                command = "hub_download_start",
                request_id = requestId,
                provider = request.Provider,
                repo = request.RepoId,
                revision = request.Revision,
                destination = request.Destination,
                paths = request.Paths,
            });
            return await completion.Task.WaitAsync(cancellationToken);
        }
        finally
        {
            _hubDownloadOperations.TryRemove(requestId, out _);
        }
    }

    private async void UploadFile_OnClick(object? sender, RoutedEventArgs e)
    {
        if (!_client.IsConnected) return;
        if (_isPlaying)
        {
            Log("上传会占用同一条 WebSocket 发送通道，请先停止变声。");
            return;
        }

        var files = await PickFilesAsync("选择上传文件", allowMultiple: true);
        if (!_client.IsConnected) return;
        foreach (var filePath in files)
        {
            await UploadFileToServerAsync(filePath);
        }
    }

    private sealed class ServerFileLayoutSnapshot
    {
        public Dictionary<ServerFileItem, double> VisiblePositions { get; } = new();
        public Dictionary<ServerFileItem, int> ItemIndices { get; } = new();
        public double ItemStride { get; set; }
        public Vector? ScrollOffset { get; set; }
    }

    private async void UploadFolder_OnClick(object? sender, RoutedEventArgs e)
    {
        if (!_client.IsConnected) return;
        if (_isPlaying)
        {
            Log("上传会占用同一条 WebSocket 发送通道，请先停止变声。");
            return;
        }
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider == null) return;
        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择训练音频文件夹",
            AllowMultiple = false,
        });
        var folderPath = folders.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath)) return;
        if (!_client.IsConnected) return;

        var enumeration = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };
        var audioFiles = Directory.EnumerateFiles(folderPath, "*", enumeration)
            .Where(IsTrainingAudioFile)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Take(20001)
            .ToList();
        if (audioFiles.Count == 0)
        {
            Log("所选文件夹中没有受支持的音频文件。");
            return;
        }
        if (audioFiles.Count > 20000)
        {
            Log("所选文件夹中的音频超过 20000 个，请拆分后上传。");
            return;
        }
        Log($"开始上传训练文件夹，共 {audioFiles.Count} 个音频文件。");
        var rootName = new DirectoryInfo(folderPath).Name;
        var folderUpload = new ServerFileItem
        {
            Name = rootName,
            IsUploadFolder = true,
            IsExpanded = true,
            IsUploading = true,
            Status = "准备上传文件夹",
            ModifiedAt = DateTimeOffset.Now,
        };
        var uploadEntries = new List<(string FilePath, string RemoteName, ServerFileItem Item)>();
        foreach (var filePath in audioFiles)
        {
            var relative = Path.GetRelativePath(folderPath, filePath);
            var remoteName = BuildTrainingRemotePath(rootName, relative);
            var child = new ServerFileItem
            {
                Name = relative,
                IsUploading = true,
                Status = "等待上传",
                TotalBytes = new FileInfo(filePath).Length,
                SentBytes = 0,
                ModifiedAt = DateTimeOffset.Now,
                UploadParent = folderUpload,
            };
            folderUpload.UploadChildren.Add(child);
            uploadEntries.Add((filePath, remoteName, child));
        }
        folderUpload.RefreshFolderProgress();
        _uploadingFiles.Insert(0, folderUpload);
        RefreshServerFilesView();

        var uploaded = 0;
        foreach (var entry in uploadEntries)
        {
            folderUpload.Status = $"正在上传：{entry.Item.Name}";
            if (await UploadFileToServerAsync(entry.FilePath, entry.RemoteName, entry.Item) is not null) uploaded++;
        }
        folderUpload.Status = uploaded == audioFiles.Count ? "文件夹上传完成" : "文件夹上传结束";
        folderUpload.IsUploading = false;
        Log($"训练文件夹上传完成：成功 {uploaded}/{audioFiles.Count} 个音频文件。");
        if (uploaded != audioFiles.Count)
        {
            ShowErrorToast($"有 {audioFiles.Count - uploaded} 个文件上传失败");
        }
        _uploadingFiles.RemoveAll(item => ReferenceEquals(item, folderUpload));
        RefreshServerFilesView();
        await _client.SendCommandAsync(new { command = "files_list" });
    }

    private static bool IsTrainingAudioFile(string path)
        => TrainingAudioExtensions.Contains(Path.GetExtension(path));

    private static string BuildTrainingRemotePath(string rootName, string relativePath)
    {
        return NormalizeRemotePath($"{rootName}/{relativePath.Replace('\\', '/')}");
    }

    private static string NormalizeRemotePath(string value)
    {
        var parts = value.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Any(part => part is "." or ".."))
        {
            throw new InvalidOperationException("服务器文件路径无效");
        }
        return string.Join('/', parts);
    }

    private void AddVoiceModel_OnClick(object? sender, RoutedEventArgs e)
    {
        if (!_client.IsConnected) return;
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
        AddVoiceModelButton.IsEnabled = _client.IsConnected;
    }

    private async void InlineConfirmVoiceModel_OnClick(object? sender, RoutedEventArgs e)
    {
        if (!_client.IsConnected) return;
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
        AddVoiceModelButton.IsEnabled = _client.IsConnected;

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

    private ListBox[] GetModelCardListBoxes() =>
    [
        VoiceModelManagementListBox,
        HubertSlotListBox,
        RmvpeSlotListBox,
        PymssWeightSlotListBox,
        PymssConfigSlotListBox,
        PretrainedGeneratorSlotListBox,
        PretrainedDiscriminatorSlotListBox,
    ];

    private void ClearModelCardSelections(ListBox? except = null)
    {
        var previousSuppression = _suppressModelCardSelectionChanged;
        _suppressModelCardSelectionChanged = true;
        try
        {
            foreach (var listBox in GetModelCardListBoxes())
            {
                if (!ReferenceEquals(listBox, except) && listBox.SelectedItem != null)
                {
                    listBox.SelectedItem = null;
                }
            }
        }
        finally
        {
            _suppressModelCardSelectionChanged = previousSuppression;
        }
    }

    private bool IsPointerInsideModelCard(object? source)
    {
        if (source is not Visual visual)
        {
            return false;
        }

        var itemContainer = visual as ListBoxItem
            ?? visual.GetVisualAncestors().OfType<ListBoxItem>().FirstOrDefault();
        if (itemContainer == null)
        {
            return false;
        }

        var owner = itemContainer.GetVisualAncestors().OfType<ListBox>().FirstOrDefault();
        return owner != null && GetModelCardListBoxes().Any(listBox => ReferenceEquals(listBox, owner));
    }

    private void GlobalPointerPressed_ClearModelCardSelection(object? sender, PointerPressedEventArgs e)
    {
        if (!IsPointerInsideModelCard(e.Source))
        {
            ClearModelCardSelections();
        }
    }

    private void VoiceModelManagementListBox_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressModelCardSelectionChanged)
        {
            return;
        }

        if (sender is ListBox { SelectedItem: not null } listBox)
        {
            ClearModelCardSelections(listBox);
        }
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
        var selectedCount = ServerFilesListBox.SelectedItems?
            .OfType<ServerFileItem>()
            .Count(item => !item.IsFolder && !item.IsUploadFolder) ?? 0;
        DeleteFileButton.IsEnabled = _client.IsConnected && selectedCount > 0;
        RenameFileButton.IsEnabled = _client.IsConnected && selectedCount == 1;
    }

    private void DeleteSelectedFile_OnClick(object? sender, RoutedEventArgs e)
    {
        var selectedItems = ServerFilesListBox.SelectedItems?.OfType<ServerFileItem>()
            .Where(item => !item.IsFolder && !item.IsUploadFolder).ToList() ?? [];
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

        var selectedItems = ServerFilesListBox.SelectedItems?.OfType<ServerFileItem>()
            .Where(item => !item.IsFolder && !item.IsUploadFolder).ToList() ?? [];
        foreach (var item in selectedItems)
        {
            await _client.SendCommandAsync(new { command = "files_delete", name = item.Name });
        }
        await _client.SendCommandAsync(new { command = "files_list" });
    }

    private void RenameSelectedFile_OnClick(object? sender, RoutedEventArgs e)
    {
        var selectedItems = ServerFilesListBox.SelectedItems?.OfType<ServerFileItem>()
            .Where(file => !file.IsFolder && !file.IsUploadFolder).ToList() ?? [];
        if (selectedItems.Count != 1)
        {
            return;
        }
        var item = selectedItems[0];

        if (item.IsUploading)
        {
            Log("该文件正在上传中，无法改名。");
            return;
        }

        item.EditingName = item.DisplayName;
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
        newName = newName.Trim();
        if (string.IsNullOrWhiteSpace(newName)
            || newName.Contains('/')
            || newName.Contains('\\'))
        {
            Log("文件名不能为空，也不能包含路径分隔符。");
            return;
        }
        var targetName = string.IsNullOrWhiteSpace(item.ParentPath)
            ? newName
            : $"{item.ParentPath}/{newName}";
        if (string.Equals(targetName, item.Name, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        await _client.SendCommandAsync(new { command = "files_rename", old_name = item.Name, new_name = targetName });
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
        if (_suppressSlotSelectionChanged || _suppressModelCardSelectionChanged)
        {
            return;
        }

        if (sender is ListBox listBox && listBox.SelectedItem is SlotBindingItem item)
        {
            ClearModelCardSelections(listBox);
            await _client.SendCommandAsync(new { command = "model_activate_in_slot", slot = item.Slot, filename = item.FileName });
        }
    }


    // ---- Drag-drop: file list → slots ----

    private PointerPressedEventArgs? _pendingDragEvent;

    private async void ServerFileItem_PointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(sender as Visual);
        if (point.Properties.IsLeftButtonPressed
            && (sender as Control)?.DataContext is ServerFileItem { IsFolder: true } treeFolder)
        {
            var previousLayout = CaptureServerFileLayout();
            // 立即作废上一轮已排队的 Loaded/Render 回调；折叠动画会延迟
            // 150 ms 才改集合，不能等到那时才取消旧的展开回调。
            _serverFileReflowVersion++;
            _pendingDragEvent = null;
            _dragCandidates = null;
            e.Handled = true;
            var animationVersion = _serverFolderAnimationVersions.TryGetValue(treeFolder.Name, out var currentVersion)
                ? currentVersion + 1
                : 1;
            _serverFolderAnimationVersions[treeFolder.Name] = animationVersion;
            if (_expandedServerFolders.Remove(treeFolder.Name))
            {
                treeFolder.IsExpanded = false;
                var descendants = _serverFiles
                    .Where(item => IsServerPathDescendant(item.Name, treeFolder.Name))
                    .ToList();
                foreach (var descendant in descendants)
                {
                    descendant.TreeOpacity = 0.0;
                    descendant.TreeOffsetY = -5.0;
                }
                await Task.Delay(150);
                if (_serverFolderAnimationVersions.TryGetValue(treeFolder.Name, out var latestVersion)
                    && latestVersion == animationVersion
                    && !_expandedServerFolders.Contains(treeFolder.Name))
                {
                    RefreshServerFilesView(previousLayout: previousLayout);
                }
            }
            else
            {
                _expandedServerFolders.Add(treeFolder.Name);
                treeFolder.IsExpanded = true;
                RefreshServerFilesView(treeFolder.Name, previousLayout);
            }
            return;
        }
        if (point.Properties.IsLeftButtonPressed
            && (sender as Control)?.DataContext is ServerFileItem { IsUploadFolder: true } folder)
        {
            folder.IsExpanded = !folder.IsExpanded;
            _pendingDragEvent = null;
            _dragCandidates = null;
            e.Handled = true;
            return;
        }
        if (point.Properties.IsLeftButtonPressed)
        {
            _dragStartPoint = e.GetPosition(sender as Visual);
            _dragStarted = false;
            _pendingDragEvent = e;

            // Capture selection NOW before ListBox pointer handling can change it.
            // If the pressed item is already in the current multi-selection, keep all selected;
            // otherwise the ListBox will switch to only this item (handled in PointerMoved fallback).
            var pressedItem = (sender as Control)?.DataContext as ServerFileItem;
            var currentSelection = ServerFilesListBox.SelectedItems?.OfType<ServerFileItem>()
                .Where(item => !item.IsFolder && !item.IsUploadFolder).ToList() ?? [];
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
                .Where(item => !item.IsFolder && !item.IsUploadFolder)
                .Select(x => x.Name).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();

        if (selected is not { Count: > 0 }
            && sender is Control ctrl
            && ctrl.DataContext is ServerFileItem { IsFolder: false, IsUploadFolder: false } singleItem)
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
        foreach (var border in new[]
                 {
                     HubertSlotBorder, RmvpeSlotBorder, PymssWeightSlotBorder, PymssConfigSlotBorder,
                     PretrainedGeneratorSlotBorder, PretrainedDiscriminatorSlotBorder,
                     VoiceModelsDropZoneBorder, InlinePthDropBorder, InlineIndexDropBorder,
                 })
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
        if (!_client.IsConnected) return;
        await _client.SendCommandAsync(new { command = "list_logs" });
    }

    private async void ClearOldLogs_OnClick(object? sender, RoutedEventArgs e)
    {
        if (!_client.IsConnected) return;
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
            Log("服务器发送切片不能大于手动目标缓冲区延迟。");
            return;
        }

        _targetBufferLatency = (int)Math.Round(TargetBufferSlider.Value);
        _maxBufferMs = (int)Math.Round(MaxBufferSlider.Value);
        _bufferCapacityMs = (int)Math.Round(BufferCapacitySlider.Value);
        _networkSliceMs = (int)Math.Round(NetworkSliceSlider.Value);
        if (_isPlaying) UpdateCaptureReadBufferSize();
        _useAdaptiveBuffer = AutoBufferBtn.Classes.Contains("active");
        _jitterEstimator.JitterFactor = JitterFactorSlider.Value;
        _jitterEstimator.MinNetworkProtectionMs = MinBufferSlider.Value;
        _jitterEstimator.MaxBufferMs = JitterMaxBufferSlider.Value;
        _jitterEstimator.Alpha = JitterAlphaSlider.Value;
        _jitterEstimator.DeviceBufferMs = AudioDeviceBufferMs;
        int effectiveTargetMs = GetEffectiveTargetBufferMs();
        RefreshAdaptiveBufferStatus(effectiveTargetMs, force: true);

        if (_waveProvider != null)
        {
            if (_waveProvider.BufferedDuration.TotalMilliseconds > _bufferCapacityMs)
            {
                TrimPlaybackBufferTo(Math.Min(effectiveTargetMs, _bufferCapacityMs / 2));
            }
            _waveProvider.BufferDuration = TimeSpan.FromMilliseconds(_bufferCapacityMs);
        }
        ScheduleClientSettingsSave();
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

        var requiresBaseModels = !string.Equals(id, VoiceModelItem.RawId, StringComparison.Ordinal)
            && !string.Equals(id, VoiceModelItem.ServerRawId, StringComparison.Ordinal);
        if (requiresBaseModels && _client.IsConnected && !EnsureRequiredBaseModelSlotsConfigured())
        {
            return;
        }

        bool isSameSelection = string.Equals(_selectedVoiceModelId, id, StringComparison.Ordinal);
        if (isSameSelection
            && !string.Equals(id, VoiceModelItem.RawId, StringComparison.Ordinal)
            && !string.Equals(id, VoiceModelItem.ServerRawId, StringComparison.Ordinal)
            && vm.IsActive
            && (vm.IsLoading || (vm.IsLoaded && _modelState == ModelState.Ready)))
        {
            Log(vm.IsLoading ? "当前模型正在准备中，请稍候。" : "当前模型已就绪，无需重复加载。");
            return;
        }

        if (_client.IsConnected
            && !string.Equals(id, VoiceModelItem.RawId, StringComparison.Ordinal)
            && !string.Equals(id, VoiceModelItem.ServerRawId, StringComparison.Ordinal))
        {
            SetVoiceModelLoadingState(id, "加载中…");
            SetModelState(ModelState.Loading);
        }

        _prevSelectedVoiceModelId = _selectedVoiceModelId;
        _selectedVoiceModelId = id;
        UpdateVoiceModelSelectionState();

        bool targetBypass = string.Equals(id, VoiceModelItem.RawId, StringComparison.Ordinal);
        if (_isPlaying && _bypassServerVoice != targetBypass)
        {
            StopStreaming();
        }

        if (string.Equals(id, VoiceModelItem.RawId, StringComparison.Ordinal))
        {
            _prevSelectedVoiceModelId = null;
            _bypassServerVoice = true;
            _serverPassthroughVoice = false;
            ModelStatusTextBlock.Text = "原声";
            Log("已切换到本地原声模式。");
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
        _speakerId = vm.SpeakerId;
        ModelStatusTextBlock.Text = vm.Name;
        UpdateStreamingToggleEnabled();
        if (_client.IsConnected)
        {
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
        if (_isPlaying)
        {
            Log("实时变声期间不加载新的显存模型，请先停止音频流。");
            return;
        }
        if (!EnsureRequiredBaseModelSlotsConfigured())
        {
            return;
        }

        try
        {
            SetVoiceModelLoadingState(id, "加载中…");
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
        if (!_client.IsConnected) return;
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

        var animationVersion = _sliderEditAnimationVersions.TryGetValue(editBox, out var currentVersion)
            ? currentVersion + 1
            : 1;
        _sliderEditAnimationVersions[editBox] = animationVersion;

        editBox.Text = GetSliderRawText(slider);
        tb.IsVisible = true;
        tb.IsHitTestVisible = false;
        editBox.IsHitTestVisible = true;
        editBox.Opacity = 0.0;
        var scale = new ScaleTransform { ScaleX = 0.96, ScaleY = 0.88 };
        scale.Transitions = new Transitions
        {
            new DoubleTransition
            {
                Property = ScaleTransform.ScaleXProperty,
                Duration = TimeSpan.FromMilliseconds(145),
                Easing = new CubicEaseOut(),
            },
            new DoubleTransition
            {
                Property = ScaleTransform.ScaleYProperty,
                Duration = TimeSpan.FromMilliseconds(145),
                Easing = new CubicEaseOut(),
            },
        };
        editBox.RenderTransform = scale;
        editBox.RenderTransformOrigin = new RelativePoint(1.0, 0.5, RelativeUnit.Relative);
        editBox.IsVisible = true;
        editBox.Focus();
        editBox.CaretIndex = editBox.Text?.Length ?? 0;

        Dispatcher.UIThread.Post(() =>
        {
            if (!_sliderEditAnimationVersions.TryGetValue(editBox, out var latestVersion)
                || latestVersion != animationVersion
                || !editBox.IsVisible)
            {
                return;
            }

            tb.Opacity = 0.0;
            editBox.Opacity = 1.0;
            scale.ScaleX = 1.0;
            scale.ScaleY = 1.0;
        }, DispatcherPriority.Loaded);
    }

    private void AnimateSliderEditClosed(TextBox editBox, TextBlock? textBlock)
    {
        var animationVersion = _sliderEditAnimationVersions.TryGetValue(editBox, out var currentVersion)
            ? currentVersion + 1
            : 1;
        _sliderEditAnimationVersions[editBox] = animationVersion;

        editBox.IsHitTestVisible = false;
        editBox.Opacity = 0.0;
        if (editBox.RenderTransform is ScaleTransform scale)
        {
            scale.ScaleX = 0.96;
            scale.ScaleY = 0.88;
        }

        if (textBlock != null)
        {
            textBlock.IsVisible = true;
            textBlock.IsHitTestVisible = false;
            textBlock.Opacity = 1.0;
        }

        DispatcherTimer.RunOnce(() =>
        {
            if (!_sliderEditAnimationVersions.TryGetValue(editBox, out var latestVersion)
                || latestVersion != animationVersion)
            {
                return;
            }

            editBox.IsVisible = false;
            editBox.IsHitTestVisible = false;
            if (textBlock != null)
            {
                textBlock.IsHitTestVisible = true;
            }
            _sliderEditAnimationVersions.Remove(editBox);
        }, TimeSpan.FromMilliseconds(130));
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

        AnimateSliderEditClosed(tb, textBlock);
    }

    private void CancelSliderEdit(TextBox tb)
    {
        if (tb.Tag is not string sliderName) return;
        var textBlockName = sliderName.Replace("Slider", "ValueText");
        var textBlock = this.FindControl<TextBlock>(textBlockName);
        AnimateSliderEditClosed(tb, textBlock);
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
        if (sender is TextBox { IsVisible: true, IsHitTestVisible: true } tb)
            CommitSliderEdit(tb);
    }

    private void GlobalPointerPressed_CommitSliderEdit(object? sender, PointerPressedEventArgs e)
    {
        var activeEdit = this
            .GetVisualDescendants()
            .OfType<TextBox>()
            .FirstOrDefault(tb => tb.IsVisible && tb.IsHitTestVisible && tb.Classes.Contains("slider-value-edit"));

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
        BlockTimeValueText?.Text = $"{e.NewValue:F0}";
        if (!_uiInitialized) return;
        UpdateBlockTimeValidationUi();
        _blockTime = (float)e.NewValue / 1000f;
        if (_isPlaying) UpdateCaptureReadBufferSize();
        ScheduleRealtimeConfigSend();
    }

    private void CrossfadeSlider_OnValueChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        CrossfadeValueText?.Text = $"{e.NewValue:F0}";
        if (!_uiInitialized) return;
        _crossfadeLength = (float)e.NewValue / 1000f;
        ScheduleRealtimeConfigSend();
    }

    private void ExtraTimeSlider_OnValueChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        ExtraTimeValueText?.Text = $"{e.NewValue:F0}";
        if (!_uiInitialized) return;
        _extraTime = (float)e.NewValue / 1000f;
        ScheduleRealtimeConfigSend();
    }

    private void ServerStreamChunkSlider_OnValueChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        ServerStreamChunkValueText?.Text = $"{e.NewValue:F0}";
        if (!_uiInitialized) return;
        _serverStreamChunkMs = (int)Math.Round(e.NewValue);
        ScheduleRealtimeConfigSend();
    }

    private void SilenceDbSlider_OnValueChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        SilenceDbValueText?.Text = $"{e.NewValue:F0}";
        if (!_uiInitialized) return;
        _silenceDbThreshold = (float)e.NewValue;
        ScheduleRealtimeConfigSend();
    }

    private void SilenceGateAttenSlider_OnValueChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        SilenceGateAttenValueText?.Text = e.NewValue.ToString("0.00");
        if (!_uiInitialized) return;
        _silenceGateAtten = (float)e.NewValue;
        ScheduleRealtimeConfigSend();
    }

    private void NoiseReduceStrengthSlider_OnValueChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        NoiseReduceStrengthValueText?.Text = e.NewValue.ToString("0.00");
        if (!_uiInitialized) return;
        _noiseReducePropDecrease = (float)e.NewValue;
        ScheduleRealtimeConfigSend();
    }

    private void RmsMixRateSlider_OnValueChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        RmsMixRateValueText?.Text = e.NewValue.ToString("0.00");
        if (!_uiInitialized) return;
        _rmsMixRate = (float)e.NewValue;
        ScheduleRealtimeConfigSend();
    }

    private void NoiseReduce_OnChange(object? sender, RoutedEventArgs e)
    {
        if (!_uiInitialized) return;
        _inputNoiseReduce = InputNoiseReduceSwitch.IsChecked == true;
        _outputNoiseReduce = OutputNoiseReduceSwitch.IsChecked == true;
        ScheduleRealtimeConfigSend();
    }

    private void F0Method_OnClick(object? sender, RoutedEventArgs e)
    {
        if (!_uiInitialized) return;
        if (sender is not Button btn) return;
        if (ClassesContains(btn, "active")) return;

        var isRmvpe = btn == F0RmvpeBtn;
        SetSegmentedToggle(F0RmvpeBtn, isRmvpe);
        SetSegmentedToggle(F0FcpeBtn, !isRmvpe);
        _f0Method = isRmvpe ? "rmvpe" : "fcpe";
        ScheduleRealtimeConfigSend();
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
        JitterMaxBufferValueText?.Text = $"{e.NewValue:F0}";
        if (!_uiInitialized) return;
        ApplyLocalSettings();
    }

    private void MinBufferSlider_OnValueChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        MinBufferValueText?.Text = $"{e.NewValue:F0}";
        if (!_uiInitialized) return;
        ApplyLocalSettings();
    }

    private void TargetBufferSlider_OnValueChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        TargetBufferValueText?.Text = $"{e.NewValue:F0}";
        if (!_uiInitialized) return;
        UpdateBlockTimeValidationUi();
        ApplyLocalSettings();
    }

    private void MaxBufferSlider_OnValueChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        MaxBufferValueText?.Text = $"{e.NewValue:F0}";
        if (!_uiInitialized) return;
        ApplyLocalSettings();
    }

    private void BufferCapacitySlider_OnValueChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        BufferCapacityValueText?.Text = $"{e.NewValue:F0}";
        if (!_uiInitialized) return;
        ApplyLocalSettings();
    }

    private void NetworkSliceSlider_OnValueChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        NetworkSliceValueText?.Text = $"{e.NewValue:F0}";
        if (!_uiInitialized) return;
        ApplyLocalSettings();
    }

    private bool ValidateBlockTimeConfig()
    {
        if (AutoBufferBtn == null || ServerStreamChunkSlider == null || TargetBufferSlider == null)
        {
            // During XAML initialization some controls may not be ready yet.
            return true;
        }

        if (AutoBufferBtn.Classes.Contains("active"))
        {
            return true;
        }

        return ServerStreamChunkSlider.Value <= TargetBufferSlider.Value;
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
        _trainingPollTimer?.Stop();
        _settingsSaveTimer?.Stop();
        SaveClientSettingsNow();
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
        var generation = _client.ConnectionGeneration;
        Dispatcher.UIThread.Post(() =>
        {
            if (_client.IsConnected && generation == _client.ConnectionGeneration)
            {
                HandleTextMessage(json);
            }
        });
    }

    private void Client_OnBinaryMessageReceived(object? sender, byte[] payload)
    {
        HandleBinaryMessage(payload);
    }

    private async void TrainingStart_OnClick(object? sender, RoutedEventArgs e)
    {
        if (!_client.IsConnected)
        {
            TrainingStatusText.Text = "请先连接服务器";
            return;
        }
        if (_isPlaying)
        {
            StopStreaming();
            Log("开始训练前已停止实时变声，避免训练与推理争用显卡。");
        }

        var name = (TrainingNameBox.Text ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            TrainingStatusText.Text = "请填写模型名称";
            return;
        }
        var selectedFiles = _trainingAudioFiles.Where(item => item.IsSelected).ToList();
        if (selectedFiles.Count == 0)
        {
            TrainingStatusText.Text = "请至少勾选一个训练音频";
            return;
        }
        if (selectedFiles.Any(item => string.IsNullOrWhiteSpace(item.Speaker)))
        {
            TrainingStatusText.Text = "每个训练音频都必须填写说话人";
            return;
        }
        if (!int.TryParse(TrainingEpochsBox.Text, out var epochs) || epochs < 1
            || !int.TryParse(TrainingBatchSizeBox.Text, out var batchSize) || batchSize < 1)
        {
            TrainingStatusText.Text = "训练轮数和批大小必须是正整数";
            return;
        }
        var sampleRateText = (TrainingSampleRateBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "40000";
        if (!int.TryParse(sampleRateText, out var sampleRate)) sampleRate = 40000;
        var preprocess = (TrainingPreprocessBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "none";
        if (!string.Equals(preprocess, "none", StringComparison.OrdinalIgnoreCase)
            && (!_pymssWeightSlotItems.Any(item => item.IsActive)
                || !_pymssConfigSlotItems.Any(item => item.IsActive)))
        {
            TrainingStatusText.Text = "请先在“模型与文件”中激活 PyMSS 模型及其配置";
            return;
        }
        var usePretrained = TrainingUsePretrainedCheckBox.IsChecked == true;
        if (usePretrained
            && (!_pretrainedGeneratorSlotItems.Any(item => item.IsActive)
                || !_pretrainedDiscriminatorSlotItems.Any(item => item.IsActive)))
        {
            TrainingStatusText.Text = "请先在“模型与文件”中激活预训练生成器和判别器";
            return;
        }

        TrainingStartButton.IsEnabled = false;
        _trainingNameAutoManaged = false;
        TrainingStatusText.Text = "正在创建训练任务…";
        await _client.SendCommandAsync(new
        {
            command = "training_start",
            training = new
            {
                name,
                files = selectedFiles.Select(item => new { name = item.Name, speaker = item.Speaker.Trim() }).ToArray(),
                epochs,
                batch_size = batchSize,
                sample_rate = sampleRate,
                preprocess,
                use_pretrained = usePretrained,
            },
        });
    }

    private void TrainingSelectAll_OnClick(object? sender, RoutedEventArgs e)
    {
        foreach (var item in _trainingAudioFiles) item.IsSelected = true;
    }

    private void TrainingClear_OnClick(object? sender, RoutedEventArgs e)
    {
        foreach (var item in _trainingAudioFiles)
        {
            _hiddenTrainingAudioFiles.Add(item.Name);
        }
        _trainingAudioFiles.Clear();
        _trainingSpeakerGroups.Clear();
        UpdateTrainingAudioActionButtons();
        TrainingStatusText.Text = "已清空当前训练音频，服务器文件未删除";
    }

    private async void TrainingOrganizeFiles_OnClick(object? sender, RoutedEventArgs e)
    {
        if (!_client.IsConnected || _trainingOrganizePending) return;
        var modelName = (TrainingNameBox.Text ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(modelName))
        {
            TrainingStatusText.Text = "请先填写模型名称";
            TrainingNameBox.Focus();
            return;
        }
        if (_trainingAudioFiles.Count == 0)
        {
            TrainingStatusText.Text = "没有可整理的训练音频";
            return;
        }
        if (_trainingAudioFiles.Any(item => string.IsNullOrWhiteSpace(item.Speaker)))
        {
            TrainingStatusText.Text = "每个训练音频都必须填写说话人";
            return;
        }

        _trainingNameAutoManaged = false;
        _trainingOrganizePending = true;
        UpdateTrainingAudioActionButtons();
        TrainingStatusText.Text = "正在整理训练音频…";
        await _client.SendCommandAsync(new
        {
            command = "training_organize_files",
            model_name = modelName,
            files = _trainingAudioFiles.Select(item => new
            {
                name = item.Name,
                speaker = item.Speaker.Trim(),
            }).ToArray(),
        });
    }

    private void TrainingAudioItem_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(sender as Visual).Properties.IsLeftButtonPressed
            && (sender as Control)?.DataContext is TrainingAudioItem item)
        {
            if ((e.KeyModifiers & KeyModifiers.Control) != 0)
            {
                item.IsSelected = !item.IsSelected;
            }
            else
            {
                foreach (var audioFile in _trainingAudioFiles)
                {
                    audioFile.IsSelected = ReferenceEquals(audioFile, item);
                }
            }
            e.Handled = true;
        }
    }

    private void TrainingSpeakerGroupHeader_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(sender as Visual).Properties.IsLeftButtonPressed
            || (sender as Control)?.DataContext is not TrainingSpeakerGroup group)
        {
            return;
        }

        if (e.Source is Visual source
            && (source is TextBox || source.GetVisualAncestors().OfType<TextBox>().Any()))
        {
            return;
        }

        group.IsExpanded = !group.IsExpanded;
        e.Handled = true;
    }

    private async void TrainingRefresh_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_client.IsConnected)
        {
            _hiddenTrainingAudioFiles.Clear();
            await _client.SendCommandAsync(new { command = "files_list" });
            await _client.SendCommandAsync(new { command = "training_list" });
        }
    }

    private void TrainingNameBox_OnTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (!_settingTrainingName)
        {
            _trainingNameAutoManaged = false;
        }
    }

    private async void TrainingCancel_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_client.IsConnected && TrainingJobsListBox.SelectedItem is TrainingJobItem job && job.CanCancel)
        {
            TrainingCancelButton.IsEnabled = false;
            TrainingStatusText.Text = "正在取消训练…";
            await _client.SendCommandAsync(new { command = "training_cancel", id = job.Id });
        }
    }

    private void TrainingJobs_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        UpdateSelectedTrainingJob();
    }

    private void UpdateSelectedTrainingJob()
    {
        if (TrainingJobsListBox.SelectedItem is not TrainingJobItem job)
        {
            TrainingProgressBar.Value = 0;
            TrainingStatusText.Text = "等待任务";
            TrainingCancelButton.IsEnabled = false;
            return;
        }
        TrainingProgressBar.Value = Math.Clamp(job.Progress * 100.0, 0.0, 100.0);
        TrainingStatusText.Text = job.Message;
        TrainingCancelButton.IsEnabled = _client.IsConnected && job.CanCancel;
    }

    private void ApplyTrainingJobs(JsonElement trainingElement)
    {
        var selectedId = (TrainingJobsListBox.SelectedItem as TrainingJobItem)?.Id;
        var activeId = trainingElement.TryGetProperty("active_id", out var activeElement)
            ? activeElement.GetString() ?? string.Empty
            : string.Empty;
        var jobs = new List<TrainingJobItem>();
        if (trainingElement.TryGetProperty("jobs", out var jobsElement)
            && jobsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in jobsElement.EnumerateArray())
            {
                jobs.Add(new TrainingJobItem
                {
                    Id = item.TryGetProperty("id", out var id) ? id.GetString() ?? string.Empty : string.Empty,
                    Name = item.TryGetProperty("name", out var name) ? name.GetString() ?? string.Empty : string.Empty,
                    State = item.TryGetProperty("state", out var state) ? state.GetString() ?? string.Empty : string.Empty,
                    Stage = item.TryGetProperty("stage", out var stage) ? stage.GetString() ?? string.Empty : string.Empty,
                    Message = item.TryGetProperty("message", out var message) ? message.GetString() ?? string.Empty : string.Empty,
                    Progress = item.TryGetProperty("progress", out var progress) && progress.TryGetDouble(out var progressValue) ? progressValue : 0.0,
                    Epoch = item.TryGetProperty("epoch", out var epoch) && epoch.TryGetInt32(out var epochValue) ? epochValue : 0,
                    Loss = item.TryGetProperty("loss", out var loss) && loss.TryGetDouble(out var lossValue) ? lossValue : 0.0,
                    ModelFile = item.TryGetProperty("model_file", out var model) ? model.GetString() ?? string.Empty : string.Empty,
                    IndexFile = item.TryGetProperty("index_file", out var index) ? index.GetString() ?? string.Empty : string.Empty,
                });
            }
        }
        _trainingJobs.Clear();
        foreach (var job in jobs) _trainingJobs.Add(job);
        TrainingHistoryExpander.IsVisible = _trainingJobs.Count > 0;
        var target = _trainingJobs.FirstOrDefault(job => string.Equals(job.Id, selectedId, StringComparison.Ordinal))
            ?? _trainingJobs.FirstOrDefault(job => string.Equals(job.Id, activeId, StringComparison.Ordinal))
            ?? _trainingJobs.FirstOrDefault();
        TrainingJobsListBox.SelectedItem = target;
        TrainingStartButton.IsEnabled = _client.IsConnected && string.IsNullOrEmpty(activeId);
        UpdateSelectedTrainingJob();
        UpdateSuggestedTrainingName();
    }

    private async Task RequestInitialDataAsync()
    {
        Log("同步配置中...");
        await _client.SendCommandAsync(new { command = "files_list" });
        await _client.SendCommandAsync(new { command = "model_list_slots" });
        await _client.SendCommandAsync(new { command = "voice_model_list" });
        await _client.SendCommandAsync(new { command = "training_list" });
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
        UpdateConnectionActionAvailability();
        ServerFilesRefreshButton.IsEnabled = isConnected;
        ServerFilesUploadButton.IsEnabled = isConnected;
        ServerFolderUploadButton.IsEnabled = isConnected;
        HubDownloadButton.IsEnabled = isConnected;
        TrainingUploadAudioButton.IsEnabled = isConnected;
        TrainingUploadFolderButton.IsEnabled = isConnected;
        TrainingRefreshButton.IsEnabled = isConnected;
        UpdateTrainingAudioActionButtons();
        AddVoiceModelButton.IsEnabled = isConnected && !InlineAddVoiceModelCard.IsVisible;
        InlineConfirmVoiceModelButton.IsEnabled = isConnected;
        ServerLogsRefreshButton.IsEnabled = isConnected;
        ServerLogsClearButton.IsEnabled = isConnected;
        ServerLogReadButton.IsEnabled = isConnected;
        ServerLogFilesComboBox.IsEnabled = isConnected && SyncCurrentLogCheckBox.IsChecked != true;
        SyncCurrentLogCheckBox.IsEnabled = isConnected;
        RetrySyncButton.IsEnabled = isConnected;
        var selectedFileCount = ServerFilesListBox.SelectedItems?.OfType<ServerFileItem>()
            .Count(item => !item.IsFolder && !item.IsUploadFolder) ?? 0;
        DeleteFileButton.IsEnabled = isConnected && selectedFileCount > 0;
        RenameFileButton.IsEnabled = isConnected && selectedFileCount == 1;
        TrainingStartButton.IsEnabled = isConnected && !_trainingJobs.Any(job => job.CanCancel);
        TrainingCancelButton.IsEnabled = isConnected
            && TrainingJobsListBox.SelectedItem is TrainingJobItem selectedTraining
            && selectedTraining.CanCancel;
        SetAnimatedVisibility(ConnectionGatePanel, !isConnected);
        DisconnectButton.Opacity = isConnected ? 1.0 : 0.0;
        DisconnectButton.IsEnabled = isConnected;
        DisconnectButton.IsHitTestVisible = isConnected;
        ConnectionStatusDot.Opacity = isConnected ? 1.0 : 0.35;
        GlobalStatusTextBlock.Text = isConnected
            ? (_client.IsLocalConnection ? "本地直连" : "已连接")
            : "未连接";
        if (!isConnected)
        {
            _serverConfigurationAccepted = false;
            _trainingOrganizePending = false;
            UpdateTrainingAudioActionButtons();
            if (_isPlaying && !_bypassServerVoice)
            {
                StopStreaming();
            }
            _realtimeConfigDebounceTimer?.Stop();
            Interlocked.Exchange(ref _realtimeConfigDebouncePending, 0);
            _lastSentConfig.Clear();
            _lastSentConfigSeq = 0;
            _uploadReadyTcs?.TrySetCanceled();
            _uploadDoneTcs?.TrySetCanceled();
            _uploadReadyTcs = null;
            _uploadDoneTcs = null;
            _hubRepositoryTcs?.TrySetCanceled();
            _hubRepositoryTcs = null;
            foreach (var operation in _hubDownloadOperations.Values)
            {
                operation.Completion.TrySetCanceled();
            }
            _hubDownloadOperations.Clear();
            _uploadOffsetCorrections.Clear();
            _effectiveServerBlockMs = 0;
            _effectiveServerChunkMs = 0;
            Interlocked.Exchange(ref _pendingLatencyReset, 0);
            ResetVoiceModelsForDisconnectedState();
            SetModelState(ModelState.NotReady);
        }
        else if (_modelState == ModelState.NotReady)
        {
            ModelStatusTextBlock.Text = _bypassServerVoice ? "原声" : "等待模型";
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
                    var now = Stopwatch.GetTimestamp();
                    var rttMs = Math.Max(0.0, (now - clientTs) * 1000.0 / Stopwatch.Frequency);
                    NetworkLatencyTextBlock.Text = $"{rttMs:F0} ms";
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

                if (string.Equals(type, "upload_error", StringComparison.OrdinalIgnoreCase))
                {
                    var exception = new InvalidOperationException(errorMessage);
                    _uploadReadyTcs?.TrySetException(exception);
                    _uploadDoneTcs?.TrySetException(exception);
                    if (root.TryGetProperty("upload_id", out var failedUploadIdElement))
                    {
                        var failedUploadId = failedUploadIdElement.GetString() ?? string.Empty;
                        if (_uploadItemsById.TryRemove(failedUploadId, out var failedUploadItem))
                        {
                            failedUploadItem.IsUploading = false;
                            failedUploadItem.UploadFailed = true;
                            failedUploadItem.Status = "上传失败";
                        }
                    }
                    return;
                }

                if (string.Equals(type, "hub_repo_error", StringComparison.OrdinalIgnoreCase))
                {
                    _hubRepositoryTcs?.TrySetException(new InvalidOperationException(errorMessage));
                    return;
                }

                if (string.Equals(type, "hub_download_error", StringComparison.OrdinalIgnoreCase))
                {
                    var requestId = root.TryGetProperty("request_id", out var requestElement)
                        ? requestElement.GetString() ?? string.Empty
                        : string.Empty;
                    if (_hubDownloadOperations.TryGetValue(requestId, out var operation))
                    {
                        operation.Completion.TrySetException(new InvalidOperationException(errorMessage));
                    }
                    return;
                }

                if (string.Equals(type, "training_error", StringComparison.OrdinalIgnoreCase))
                {
                    TrainingStatusText.Text = errorMessage;
                    TrainingStartButton.IsEnabled = _client.IsConnected;
                    Log($"[训练错误] {errorMessage}");
                    return;
                }

                if (string.Equals(type, "training_organize_error", StringComparison.OrdinalIgnoreCase))
                {
                    _trainingOrganizePending = false;
                    UpdateTrainingAudioActionButtons();
                    TrainingStatusText.Text = errorMessage;
                    Log($"[整理文件失败] {errorMessage}");
                    ShowErrorToast("整理训练音频失败");
                    return;
                }

                if (string.Equals(type, "config_required", StringComparison.OrdinalIgnoreCase))
                {
                    _serverConfigurationAccepted = false;
                    UpdateStreamingToggleEnabled();
                    Log($"[配置] {errorMessage}");
                    ShowErrorToast(errorMessage);
                    return;
                }

                // 语音模型加载失败：把蓝灯变红灯
                if (string.Equals(type, "voice_model_error", StringComparison.OrdinalIgnoreCase))
                {
                    Log($"[错误] 模型加载失败: {errorMessage}");
                    ShowErrorToast(GetModelLoadErrorToastText(errorMessage));
                    MarkCurrentTargetModelError();
                    RevertModelSelectionOnError();
                    if (!string.IsNullOrEmpty(_pendingPreloadModelId))
                    {
                        _failedVoiceModelIds.Add(_pendingPreloadModelId);
                        var failedVm = _voiceModelsManagement.FirstOrDefault(vm => string.Equals(vm.Id, _pendingPreloadModelId, StringComparison.Ordinal));
                        if (failedVm != null)
                        {
                            failedVm.IsLoading = false;
                            failedVm.IsLoaded = false;
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
                    ShowErrorToast(GetModelLoadErrorToastText(errorMessage));
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

                    _serverConfigurationAccepted = true;

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

                    if (root.TryGetProperty("effective", out var chunkEffectiveElement)
                        && chunkEffectiveElement.TryGetProperty("stream_chunk_ms", out var chunkMsElement)
                        && chunkMsElement.TryGetInt32(out var acknowledgedChunkMs))
                    {
                        bool chunkChanged = _effectiveServerChunkMs > 0
                            && acknowledgedChunkMs != _effectiveServerChunkMs;
                        _effectiveServerChunkMs = acknowledgedChunkMs;
                        if (chunkChanged && _isPlaying)
                        {
                            Interlocked.Exchange(ref _pendingLatencyReset, 1);
                            Log($"服务端发送切片已切换为 {acknowledgedChunkMs}ms，正在重新校准自动缓冲。");
                        }
                    }
                    SetModelState(ModelState.Ready);
                    SetActiveModelLoadingState(isLoading: false);
                    var selectedVoiceModel = _voiceModelsManagement.FirstOrDefault(
                        vm => string.Equals(vm.Id, _selectedVoiceModelId, StringComparison.Ordinal));
                    if (selectedVoiceModel != null)
                    {
                        selectedVoiceModel.IsLoading = false;
                        selectedVoiceModel.IsLoaded = true;
                        selectedVoiceModel.StatusBrush = new SolidColorBrush(Color.Parse("#2E9F4D"));
                        selectedVoiceModel.StatusHint = "已加载到显存，可立即使用";
                        if (selectedVoiceModel.IsActive)
                        {
                            _prevSelectedVoiceModelId = null;
                        }
                    }
                    if (root.TryGetProperty("hash", out var hashElement))
                    {
                        var serverHash = hashElement.GetString() ?? string.Empty;
                        var localHash = ComputeConfigHash(_lastSentConfig);
                        if (string.Equals(serverHash, localHash, StringComparison.OrdinalIgnoreCase))
                        {
                            _lastConfigHashRetry = null;
                        }
                        else if (!string.Equals(_lastConfigHashRetry, localHash, StringComparison.OrdinalIgnoreCase))
                        {
                            _lastConfigHashRetry = localHash;
                            Log("[WARN] 配置校验不一致，自动重试一次...");
                            _ = SendConfigurationAsync(true);
                        }
                        else
                        {
                            Log("[WARN] 配置校验仍不一致，已停止自动重试以避免请求风暴。");
                        }
                    }
                    break;
                }
                case "config_error":
                {
                    var configErrorText = root.TryGetProperty("message", out var configErrorMessage) ? configErrorMessage.GetString() ?? "模型加载失败" : "模型加载失败";
                    SetModelState(ModelState.Error, configErrorText);
                    MarkCurrentTargetModelError();
                    ShowErrorToast(GetModelLoadErrorToastText(configErrorText));
                    RevertModelSelectionOnError();
                    break;
                }
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
                case "hub_repo_files":
                {
                    var files = root.GetProperty("files").EnumerateArray()
                        .Select(item => new HubRepositoryFile(
                            item.TryGetProperty("path", out var pathElement) ? pathElement.GetString() ?? string.Empty : string.Empty,
                            item.TryGetProperty("size", out var sizeElement) && sizeElement.TryGetInt64(out var size) ? size : 0,
                            item.TryGetProperty("oid", out var oidElement) ? oidElement.GetString() ?? string.Empty : string.Empty))
                        .Where(item => !string.IsNullOrWhiteSpace(item.Path))
                        .ToList();
                    _hubRepositoryTcs?.TrySetResult(new HubRepositorySnapshot(
                        root.TryGetProperty("provider", out var providerElement) ? providerElement.GetString() ?? string.Empty : string.Empty,
                        root.TryGetProperty("repo", out var repoElement) ? repoElement.GetString() ?? string.Empty : string.Empty,
                        root.TryGetProperty("revision", out var revisionElement) ? revisionElement.GetString() ?? string.Empty : string.Empty,
                        root.TryGetProperty("default_destination", out var destinationElement) ? destinationElement.GetString() ?? string.Empty : string.Empty,
                        root.TryGetProperty("total_bytes", out var totalElement) && totalElement.TryGetInt64(out var total) ? total : 0,
                        files));
                    break;
                }
                case "hub_download_started":
                    break;
                case "hub_download_progress":
                {
                    var requestId = root.TryGetProperty("request_id", out var requestElement)
                        ? requestElement.GetString() ?? string.Empty
                        : string.Empty;
                    if (_hubDownloadOperations.TryGetValue(requestId, out var operation))
                    {
                        operation.Progress.Report(new HubDownloadProgress(
                            root.TryGetProperty("path", out var pathElement) ? pathElement.GetString() ?? string.Empty : string.Empty,
                            root.TryGetProperty("file_index", out var indexElement) && indexElement.TryGetInt32(out var index) ? index : 0,
                            root.TryGetProperty("file_count", out var countElement) && countElement.TryGetInt32(out var count) ? count : 0,
                            root.TryGetProperty("completed_bytes", out var completedElement) && completedElement.TryGetInt64(out var completed) ? completed : 0,
                            root.TryGetProperty("total_bytes", out var totalElement) && totalElement.TryGetInt64(out var total) ? total : 0,
                            root.TryGetProperty("state", out var stateElement) ? stateElement.GetString() ?? string.Empty : string.Empty));
                    }
                    break;
                }
                case "hub_download_done":
                {
                    var requestId = root.TryGetProperty("request_id", out var requestElement)
                        ? requestElement.GetString() ?? string.Empty
                        : string.Empty;
                    var files = root.TryGetProperty("files", out var filesElement)
                        ? filesElement.EnumerateArray().Select(item => item.GetString() ?? string.Empty).Where(item => item.Length > 0).ToList()
                        : new List<string>();
                    if (_hubDownloadOperations.TryGetValue(requestId, out var operation))
                    {
                        operation.Completion.TrySetResult(new HubDownloadResult(
                            root.TryGetProperty("destination", out var destinationElement) ? destinationElement.GetString() ?? string.Empty : string.Empty,
                            files,
                            root.TryGetProperty("total_bytes", out var totalElement) && totalElement.TryGetInt64(out var total) ? total : 0));
                    }
                    Log($"模型仓库下载完成：{files.Count} 个文件");
                    _ = _client.SendCommandAsync(new { command = "files_list" });
                    break;
                }
                case "hub_download_cancelled":
                {
                    var requestId = root.TryGetProperty("request_id", out var requestElement)
                        ? requestElement.GetString() ?? string.Empty
                        : string.Empty;
                    if (_hubDownloadOperations.TryGetValue(requestId, out var operation))
                    {
                        operation.Completion.TrySetCanceled();
                    }
                    break;
                }
                case "voice_models":
                    ApplyVoiceModelsFromServer(root.GetProperty("voice"));
                    break;
                case "training_jobs":
                    ApplyTrainingJobs(root.GetProperty("training"));
                    break;
                case "training_started":
                    TrainingStatusText.Text = "训练任务已启动";
                    _ = _client.SendCommandAsync(new { command = "training_list" });
                    break;
                case "training_cancelled":
                    TrainingStatusText.Text = "训练正在取消";
                    _ = _client.SendCommandAsync(new { command = "training_list" });
                    break;
                case "training_files_organized":
                {
                    _trainingOrganizePending = false;
                    _trainingNameAutoManaged = false;
                    var organizedModel = root.TryGetProperty("model", out var organizedModelElement)
                        ? organizedModelElement.GetString() ?? string.Empty
                        : string.Empty;
                    if (!string.IsNullOrWhiteSpace(organizedModel))
                    {
                        SetTrainingName(organizedModel);
                    }
                    var organizedCount = root.TryGetProperty("files", out var organizedFilesElement)
                        && organizedFilesElement.ValueKind == JsonValueKind.Array
                        ? organizedFilesElement.GetArrayLength()
                        : 0;
                    TrainingStatusText.Text = $"已整理 {organizedCount} 个训练音频";
                    Log($"训练音频已整理到 {organizedModel}/dataset");
                    UpdateTrainingAudioActionButtons();
                    _ = _client.SendCommandAsync(new { command = "files_list" });
                    break;
                }
                case "voice_model_error":
                {
                    var errMsg = root.TryGetProperty("message", out var vmErrMsg) ? vmErrMsg.GetString() ?? "模型加载失败" : "模型加载失败";
                    Log($"[错误] 模型加载失败: {errMsg}");
                    ShowErrorToast(GetModelLoadErrorToastText(errMsg));
                    MarkCurrentTargetModelError();
                    RevertModelSelectionOnError();
                    if (!string.IsNullOrEmpty(_pendingPreloadModelId))
                    {
                        var failedVm = _voiceModelsManagement.FirstOrDefault(vm => string.Equals(vm.Id, _pendingPreloadModelId, StringComparison.Ordinal));
                        if (failedVm != null)
                        {
                            failedVm.IsLoading = false;
                            failedVm.IsLoaded = false;
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
                {
                    var changedSlot = root.GetProperty("slot").GetString() ?? string.Empty;
                    if (ApplySingleSlotFromServer(changedSlot, root.GetProperty("state")))
                    {
                        RecomputeBoundFiles();
                        RefreshServerFilesView();
                        if (_client.IsConnected && !_bypassServerVoice
                            && (string.Equals(changedSlot, "hubert_base", StringComparison.OrdinalIgnoreCase)
                                || string.Equals(changedSlot, "rmvpe", StringComparison.OrdinalIgnoreCase)))
                        {
                            _ = SendConfigurationAsync(true);
                        }
                    }
                    break;
                }
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
                        if (uploadItem.UploadParent is null)
                        {
                            uploadItem.Name = root.GetProperty("name").GetString() ?? uploadItem.Name;
                        }
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
        SyncTrainingAudioFiles();
        UpdateSuggestedTrainingName();
        Log($"已获取服务端文件列表，共 {_serverFilesRaw.Count} 项。");
    }

    private void SyncTrainingAudioFiles()
    {
        var previous = _trainingAudioFiles.ToDictionary(item => item.Name, StringComparer.OrdinalIgnoreCase);
        var previousExpansion = _trainingSpeakerGroups.ToDictionary(
            group => group.Name,
            group => group.IsExpanded,
            StringComparer.OrdinalIgnoreCase);
        var updated = _serverFilesRaw
            .Where(item => IsTrainingAudioFile(item.Name))
            .Where(item => !_hiddenTrainingAudioFiles.Contains(item.Name))
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .Select(item =>
            {
                if (previous.TryGetValue(item.Name, out var existing))
                {
                    existing.DetailText = BuildTrainingAudioDetail(item);
                    return existing;
                }

                var placement = InferTrainingPlacement(item.Name);
                return new TrainingAudioItem
                {
                    Name = item.Name,
                    DisplayName = placement.DisplayName,
                    DetailText = BuildTrainingAudioDetail(item),
                    Speaker = placement.Speaker,
                    IsSelected = false,
                };
            })
            .ToList();

        _trainingAudioFiles.Clear();
        foreach (var item in updated) _trainingAudioFiles.Add(item);

        _trainingSpeakerGroups.Clear();
        var groupIndex = 0;
        foreach (var speakerFiles in updated.GroupBy(item => item.Speaker, StringComparer.OrdinalIgnoreCase))
        {
            var group = new TrainingSpeakerGroup(speakerFiles.Key)
            {
                IsExpanded = previousExpansion.TryGetValue(speakerFiles.Key, out var wasExpanded)
                    ? wasExpanded
                    : groupIndex == 0,
            };
            foreach (var item in speakerFiles)
            {
                group.Files.Add(item);
            }
            _trainingSpeakerGroups.Add(group);
            groupIndex++;
        }
        UpdateTrainingAudioActionButtons();
    }

    private static string BuildTrainingAudioDetail(ServerFileItem item)
    {
        var fileType = Path.GetExtension(item.Name).TrimStart('.').ToUpperInvariant();
        var typePrefix = string.IsNullOrWhiteSpace(fileType) ? "音频" : fileType;
        return $"{typePrefix} · {FormatTrainingAudioBytes(item.Size)}";
    }

    private static string FormatTrainingAudioBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";

        double value = bytes;
        string[] units = ["KB", "MB", "GB", "TB"];
        var unitIndex = -1;
        do
        {
            value /= 1024;
            unitIndex++;
        } while (value >= 1024 && unitIndex < units.Length - 1);

        return $"{value:0.##} {units[unitIndex]}";
    }

    private static (string Speaker, string DisplayName) InferTrainingPlacement(string fileName)
    {
        var pathParts = fileName.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (pathParts.Length >= 4
            && string.Equals(pathParts[1], "dataset", StringComparison.OrdinalIgnoreCase))
        {
            return (pathParts[2], string.Join('/', pathParts.Skip(3)));
        }
        if (pathParts.Length >= 3)
        {
            return (pathParts[1], string.Join('/', pathParts.Skip(2)));
        }
        if (pathParts.Length == 2)
        {
            return (pathParts[0], pathParts[1]);
        }

        var separator = fileName.IndexOf("__", StringComparison.Ordinal);
        return separator > 0
            ? (fileName[..separator], fileName[(separator + 2)..])
            : ("默认说话人", pathParts.FirstOrDefault() ?? fileName);
    }

    private void UpdateTrainingAudioActionButtons()
    {
        var hasAudio = _trainingAudioFiles.Count > 0;
        TrainingAudioSummaryText.Text = $"{_trainingSpeakerGroups.Count} 个音频组 · {_trainingAudioFiles.Count} 个音频文件";
        TrainingSelectAllButton.IsEnabled = hasAudio;
        TrainingClearButton.IsEnabled = hasAudio;
        TrainingOrganizeButton.IsEnabled = _client.IsConnected && hasAudio && !_trainingOrganizePending;
    }

    private void UpdateSuggestedTrainingName()
    {
        if (!_trainingNameAutoManaged) return;

        var occupied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var job in _trainingJobs)
        {
            if (!string.IsNullOrWhiteSpace(job.Name)) occupied.Add(job.Name.Trim());
        }
        foreach (var model in _voiceModelsManagement)
        {
            if (!string.IsNullOrWhiteSpace(model.Name)) occupied.Add(model.Name.Trim());
        }
        foreach (var file in _serverFilesRaw)
        {
            var parts = file.Name.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && string.Equals(parts[1], "dataset", StringComparison.OrdinalIgnoreCase))
            {
                occupied.Add(parts[0]);
            }
        }

        SetTrainingName(TrainingNameHelper.GetAvailableModelName(occupied));
    }

    private void SetTrainingName(string value)
    {
        if (string.Equals(TrainingNameBox.Text, value, StringComparison.Ordinal)) return;
        _settingTrainingName = true;
        TrainingNameBox.Text = value;
        _settingTrainingName = false;
    }

    private void ApplyVoiceModelsFromServer(JsonElement voiceElement)
    {
        var previousSelectionId = _selectedVoiceModelId;
        var previousManagementSelectionId =
            (VoiceModelManagementListBox.SelectedItem as VoiceModelItem)?.Id;
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
                var speakerId = modelElement.TryGetProperty("speaker_id", out var speakerElement) && speakerElement.TryGetInt32(out var parsedSpeakerId)
                    ? Math.Max(0, parsedSpeakerId)
                    : 0;
                var isActive = modelElement.TryGetProperty("active", out var activeElement) && activeElement.ValueKind == JsonValueKind.True;
                var isLoaded = modelElement.TryGetProperty("loaded", out var loadedElement) && loadedElement.ValueKind == JsonValueKind.True;
                var isLoading = !isLoaded
                    && (string.Equals(id, _pendingPreloadModelId, StringComparison.Ordinal)
                        || (_modelState == ModelState.Loading
                            && string.Equals(id, _selectedVoiceModelId, StringComparison.Ordinal)));
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
                    SpeakerId = speakerId,
                    IsActive = isActive,
                    IsLoaded = isLoaded,
                    IsLoading = isLoading,
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
                else if (isLoading)
                {
                    justAdded.StatusBrush = new SolidColorBrush(Color.Parse("#2196F3"));
                    justAdded.StatusHint = "加载中…";
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

        if (!string.IsNullOrEmpty(_pendingPreloadModelId)
            && list.Any(item => string.Equals(item.Id, _pendingPreloadModelId, StringComparison.Ordinal) && item.IsLoaded))
        {
            _pendingPreloadModelId = null;
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

        bool resolvedBypass = string.Equals(resolvedId, VoiceModelItem.RawId, StringComparison.Ordinal);
        if (_isPlaying && _bypassServerVoice != resolvedBypass)
        {
            StopStreaming();
        }

        bool runtimeSelectionChanged = false;
        if (resolvedBypass)
        {
            runtimeSelectionChanged = !_bypassServerVoice || _serverPassthroughVoice
                || !string.IsNullOrEmpty(_modelPath) || !string.IsNullOrEmpty(_indexPath);
            _bypassServerVoice = true;
            _serverPassthroughVoice = false;
            _modelPath = string.Empty;
            _indexPath = string.Empty;
            _speakerId = 0;
        }
        else if (string.Equals(resolvedId, VoiceModelItem.ServerRawId, StringComparison.Ordinal))
        {
            runtimeSelectionChanged = _bypassServerVoice || !_serverPassthroughVoice
                || !string.IsNullOrEmpty(_modelPath) || !string.IsNullOrEmpty(_indexPath);
            _bypassServerVoice = false;
            _serverPassthroughVoice = true;
            _modelPath = string.Empty;
            _indexPath = string.Empty;
            _speakerId = 0;
        }
        else
        {
            var selectedRuntimeModel = list.FirstOrDefault(item => string.Equals(item.Id, resolvedId, StringComparison.Ordinal));
            if (selectedRuntimeModel != null)
            {
                runtimeSelectionChanged = _bypassServerVoice || _serverPassthroughVoice
                    || !string.Equals(_modelPath, selectedRuntimeModel.Pth, StringComparison.Ordinal)
                    || !string.Equals(_indexPath, selectedRuntimeModel.Index, StringComparison.Ordinal)
                    || _speakerId != selectedRuntimeModel.SpeakerId;
                _bypassServerVoice = false;
                _serverPassthroughVoice = false;
                _modelPath = selectedRuntimeModel.Pth;
                _indexPath = selectedRuntimeModel.Index;
                _speakerId = selectedRuntimeModel.SpeakerId;
            }
        }

        if (runtimeSelectionChanged && _client.IsConnected && !_bypassServerVoice)
        {
            _ = SendConfigurationAsync(true);
        }

        var selectedModelConfirmed = list.FirstOrDefault(
            item => string.Equals(item.Id, _selectedVoiceModelId, StringComparison.Ordinal));
        if (!string.IsNullOrEmpty(_prevSelectedVoiceModelId)
            && _modelState == ModelState.Ready
            && string.Equals(activeId, _selectedVoiceModelId, StringComparison.Ordinal)
            && selectedModelConfirmed?.IsLoaded == true)
        {
            _prevSelectedVoiceModelId = null;
        }

        _suppressModelCardSelectionChanged = true;
        try
        {
            VoiceModelManagementListBox.SelectedItem = string.IsNullOrWhiteSpace(previousManagementSelectionId)
                ? null
                : _voiceModelsManagement.FirstOrDefault(
                    item => string.Equals(item.Id, previousManagementSelectionId, StringComparison.Ordinal));
        }
        finally
        {
            _suppressModelCardSelectionChanged = false;
        }
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
        UpdateSuggestedTrainingName();

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
            failedVm.IsLoading = false;
            failedVm.IsLoaded = false;
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
                _speakerId = vm.SpeakerId;
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

        CurrentVoiceNameTextBlock.Text = _selectedVoiceModelId switch
        {
            VoiceModelItem.RawId => "原声",
            VoiceModelItem.ServerRawId => "原声（服务端）",
            _ => _voiceModelsManagement.FirstOrDefault(vm =>
                     string.Equals(vm.Id, _selectedVoiceModelId, StringComparison.Ordinal))?.Name
                 ?? ModelStatusTextBlock.Text,
        };
    }

    private void UpdateSlotPlaceholderVisibility()
    {
        HubertSlotPlaceholder.IsVisible = _hubertSlotItems.Count == 0;
        RmvpeSlotPlaceholder.IsVisible = _rmvpeSlotItems.Count == 0;
        PymssWeightSlotPlaceholder.IsVisible = _pymssWeightSlotItems.Count == 0;
        PymssConfigSlotPlaceholder.IsVisible = _pymssConfigSlotItems.Count == 0;
        PretrainedGeneratorSlotPlaceholder.IsVisible = _pretrainedGeneratorSlotItems.Count == 0;
        PretrainedDiscriminatorSlotPlaceholder.IsVisible = _pretrainedDiscriminatorSlotItems.Count == 0;
    }

    private void SetVoiceModelLoadingState(string id, string hint)
    {
        var model = _voiceModelsManagement.FirstOrDefault(vm => string.Equals(vm.Id, id, StringComparison.Ordinal))
            ?? _voiceModelsSelection.FirstOrDefault(vm => string.Equals(vm.Id, id, StringComparison.Ordinal));
        if (model == null)
        {
            return;
        }

        _failedVoiceModelIds.Remove(id);
        model.IsLoaded = false;
        model.IsLoading = true;
        model.StatusBrush = new SolidColorBrush(Color.Parse("#2196F3"));
        model.StatusHint = hint;
    }

    private bool EnsureRequiredBaseModelSlotsConfigured()
    {
        var missingHubert = !_hubertSlotItems.Any(item => item.IsActive);
        var missingRmvpe = string.Equals(_f0Method, "rmvpe", StringComparison.OrdinalIgnoreCase)
            && !_rmvpeSlotItems.Any(item => item.IsActive);

        string message;
        if (missingHubert && missingRmvpe)
        {
            message = "未配置 HuBERT Base 和 RMVPE 模型槽位";
        }
        else if (missingHubert)
        {
            message = "未配置 HuBERT Base 模型槽位";
        }
        else if (missingRmvpe)
        {
            message = "未配置 RMVPE 模型槽位";
        }
        else
        {
            _lastBaseModelSlotWarning = string.Empty;
            return true;
        }

        SetModelState(ModelState.NotReady, message);
        if (!string.Equals(_lastBaseModelSlotWarning, message, StringComparison.Ordinal))
        {
            _lastBaseModelSlotWarning = message;
            Log($"{message}，请先在“模型与文件”中设置。");
            ShowErrorToast(message);
        }
        return false;
    }

    private static string GetModelLoadErrorToastText(string errorMessage)
    {
        var markerIndex = errorMessage.IndexOf("未配置 ", StringComparison.Ordinal);
        return markerIndex >= 0 ? errorMessage[markerIndex..].TrimEnd('。') : "模型加载失败";
    }

    private void ResetVoiceModelsForDisconnectedState()
    {
        VoiceModelManagementListBox.SelectedItem = null;
        _voiceModelsManagement.Clear();
        _voiceModelsSelection.Clear();
        _voiceModelsSelection.Add(_rawVoiceModelItem);

        _rawVoiceModelItem.IsActive = false;
        _rawVoiceModelItem.IsLoaded = false;
        _rawVoiceModelItem.IsLoading = false;
        _serverRawVoiceModelItem.IsActive = false;
        _serverRawVoiceModelItem.IsLoaded = false;
        _serverRawVoiceModelItem.IsLoading = false;
        _selectedVoiceModelId = VoiceModelItem.RawId;
        _prevSelectedVoiceModelId = null;
        _pendingPreloadModelId = null;
        _lastBaseModelSlotWarning = string.Empty;
        _recentUnloadedVoiceModelId = string.Empty;
        _failedVoiceModelIds.Clear();
        _modelPath = string.Empty;
        _indexPath = string.Empty;
        _speakerId = 0;
        _bypassServerVoice = true;
        _serverPassthroughVoice = false;
        ModelStatusTextBlock.Text = "原声";
        UpdateVoiceModelSelectionState();
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
        var previousActive = slot switch
        {
            "hubert_base" => _hubertSlotItems.FirstOrDefault(item => item.IsActive)?.FileName ?? string.Empty,
            "rmvpe" => _rmvpeSlotItems.FirstOrDefault(item => item.IsActive)?.FileName ?? string.Empty,
            "pymss_weight" => _pymssWeightSlotItems.FirstOrDefault(item => item.IsActive)?.FileName ?? string.Empty,
            "pymss_config" => _pymssConfigSlotItems.FirstOrDefault(item => item.IsActive)?.FileName ?? string.Empty,
            "pretrained_g" => _pretrainedGeneratorSlotItems.FirstOrDefault(item => item.IsActive)?.FileName ?? string.Empty,
            "pretrained_d" => _pretrainedDiscriminatorSlotItems.FirstOrDefault(item => item.IsActive)?.FileName ?? string.Empty,
            _ => string.Empty,
        };
        var activeChanged = !string.Equals(previousActive, active, StringComparison.OrdinalIgnoreCase);

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
            "pymss_weight" => _pymssWeightSlotItems,
            "pymss_config" => _pymssConfigSlotItems,
            "pretrained_g" => _pretrainedGeneratorSlotItems,
            "pretrained_d" => _pretrainedDiscriminatorSlotItems,
            _ => null,
        };

        var listBox = slot switch
        {
            "hubert_base" => HubertSlotListBox,
            "rmvpe" => RmvpeSlotListBox,
            "pymss_weight" => PymssWeightSlotListBox,
            "pymss_config" => PymssConfigSlotListBox,
            "pretrained_g" => PretrainedGeneratorSlotListBox,
            "pretrained_d" => PretrainedDiscriminatorSlotListBox,
            _ => null,
        };

        if (list == null || listBox == null)
        {
            return false;
        }

        var previousUiSelection = (listBox.SelectedItem as SlotBindingItem)?.FileName;
        _suppressSlotSelectionChanged = true;
        try
        {
            var existingFiles = list.Select(item => item.FileName).ToList();
            var filesChanged = existingFiles.Count != files.Count || !existingFiles.SequenceEqual(files, StringComparer.OrdinalIgnoreCase);

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
                }
            }
            else
            {
                foreach (var item in list)
                {
                    item.IsActive = string.Equals(item.FileName, active, StringComparison.OrdinalIgnoreCase);
                    item.StatusBrush = item.IsActive ? new SolidColorBrush(Color.Parse("#2E9F4D")) : new SolidColorBrush(Color.Parse("#8B8B8B"));
                    item.StatusHint = item.IsActive ? "已激活" : "未激活";
                }
            }

            // Runtime activation is represented by the status dot, not by the
            // ListBox selection. Only restore a selection that originated from
            // an earlier user click; never auto-select the active slot item.
            listBox.SelectedItem = string.IsNullOrWhiteSpace(previousUiSelection)
                ? null
                : list.FirstOrDefault(item =>
                    string.Equals(item.FileName, previousUiSelection, StringComparison.OrdinalIgnoreCase));
            return filesChanged || activeChanged;
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

        foreach (var item in _pymssWeightSlotItems)
        {
            _boundFiles.Add(item.FileName);
        }

        foreach (var item in _pymssConfigSlotItems)
        {
            _boundFiles.Add(item.FileName);
        }

        foreach (var item in _pretrainedGeneratorSlotItems)
        {
            _boundFiles.Add(item.FileName);
        }

        foreach (var item in _pretrainedDiscriminatorSlotItems)
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

    private void RefreshServerFilesView(
        string? animatedFolderPath = null,
        ServerFileLayoutSnapshot? previousLayout = null)
    {
        var reflowVersion = ++_serverFileReflowVersion;
        var desired = new List<ServerFileItem>();
        desired.AddRange(_uploadingFiles);

        IEnumerable<ServerFileItem> query = _serverFilesRaw;
        var uploadingNames = new HashSet<string>(_uploadingFiles.Select(item => item.Name), StringComparer.OrdinalIgnoreCase);
        query = query.Where(item => !uploadingNames.Contains(item.Name));

        if (_hideBoundFiles && _boundFiles.Count > 0)
        {
            query = query.Where(item => !_boundFiles.Contains(item.Name));
        }

        var files = query.ToList();
        foreach (var file in files)
        {
            var parts = file.Name.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            file.IsFolder = false;
            file.DisplayName = parts.LastOrDefault() ?? file.Name;
            file.ParentPath = parts.Length > 1 ? string.Join('/', parts.Take(parts.Length - 1)) : string.Empty;
            file.TreeIndent = Math.Max(0, parts.Length - 1) * 18;
            file.TreeOpacity = 1.0;
            file.TreeOffsetY = 0.0;
        }

        var folderPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            var parts = file.Name.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            for (var depth = 1; depth < parts.Length; depth++)
            {
                folderPaths.Add(string.Join('/', parts.Take(depth)));
            }
        }
        _expandedServerFolders.IntersectWith(folderPaths);

        foreach (var stalePath in _serverFolderCache.Keys.Where(path => !folderPaths.Contains(path)).ToList())
        {
            _serverFolderCache.Remove(stalePath);
            _serverFolderAnimationVersions.Remove(stalePath);
        }
        var folders = new Dictionary<string, ServerFileItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in folderPaths)
        {
            if (!_serverFolderCache.TryGetValue(path, out var folder))
            {
                folder = new ServerFileItem { Name = path, IsFolder = true };
                _serverFolderCache[path] = folder;
            }
            var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var prefix = path + "/";
            var descendants = files.Where(file => file.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
            folder.DisplayName = parts.LastOrDefault() ?? path;
            folder.ParentPath = parts.Length > 1 ? string.Join('/', parts.Take(parts.Length - 1)) : string.Empty;
            folder.TreeIndent = Math.Max(0, parts.Length - 1) * 18;
            folder.ChildCount = descendants.Count;
            folder.Size = descendants.Sum(file => file.Size);
            folder.ModifiedAt = descendants.Count > 0 ? descendants.Max(file => file.ModifiedAt) : DateTimeOffset.MinValue;
            folder.TreeOpacity = 1.0;
            folder.TreeOffsetY = 0.0;
            folder.IsExpanded = _expandedServerFolders.Contains(path);
            folder.IsModelRootFolder = parts.Length == 1
                && (folderPaths.Contains($"{path}/dataset")
                    || descendants.Any(file =>
                        file.Name.EndsWith(".pth", StringComparison.OrdinalIgnoreCase)
                        || file.Name.EndsWith(".index", StringComparison.OrdinalIgnoreCase)));
            folders[path] = folder;
        }

        IEnumerable<ServerFileItem> SortItems(IEnumerable<ServerFileItem> items) => _fileSortMode switch
        {
            "time_asc" => items.OrderBy(item => item.ModifiedAt),
            "name_asc" => items.OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase),
            "name_desc" => items.OrderByDescending(item => item.DisplayName, StringComparer.OrdinalIgnoreCase),
            _ => items.OrderByDescending(item => item.ModifiedAt),
        };

        void AppendLevel(string parentPath)
        {
            var childFolders = folders.Values.Where(folder =>
                string.Equals(folder.ParentPath, parentPath, StringComparison.OrdinalIgnoreCase));
            foreach (var folder in SortItems(childFolders))
            {
                desired.Add(folder);
                if (folder.IsExpanded)
                {
                    AppendLevel(folder.Name);
                }
            }

            var childFiles = files.Where(file =>
                string.Equals(file.ParentPath, parentPath, StringComparison.OrdinalIgnoreCase));
            desired.AddRange(SortItems(childFiles));
        }

        AppendLevel(string.Empty);

        var enteringItems = string.IsNullOrWhiteSpace(animatedFolderPath)
            ? []
            : desired.Where(item => IsServerPathDescendant(item.Name, animatedFolderPath)).ToList();
        foreach (var item in enteringItems)
        {
            item.TreeOpacity = 0.0;
            item.TreeOffsetY = -5.0;
        }

        for (var index = 0; index < desired.Count; index++)
        {
            var desiredItem = desired[index];
            if (index < _serverFiles.Count && ReferenceEquals(_serverFiles[index], desiredItem))
            {
                continue;
            }
            var existingIndex = -1;
            for (var candidate = index; candidate < _serverFiles.Count; candidate++)
            {
                if (ReferenceEquals(_serverFiles[candidate], desiredItem))
                {
                    existingIndex = candidate;
                    break;
                }
            }
            if (existingIndex >= 0)
            {
                _serverFiles.Move(existingIndex, index);
            }
            else
            {
                _serverFiles.Insert(index, desiredItem);
            }
        }
        while (_serverFiles.Count > desired.Count)
        {
            _serverFiles.RemoveAt(_serverFiles.Count - 1);
        }

        if (previousLayout is not null)
        {
            Dispatcher.UIThread.Post(
                () => AnimateServerFileReflow(previousLayout, reflowVersion),
                DispatcherPriority.Loaded);
        }

        if (enteringItems.Count > 0)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (reflowVersion != _serverFileReflowVersion)
                {
                    return;
                }
                foreach (var item in enteringItems)
                {
                    item.TreeOpacity = 1.0;
                    item.TreeOffsetY = 0.0;
                }
            }, DispatcherPriority.Loaded);
        }
    }

    private ServerFileLayoutSnapshot CaptureServerFileLayout()
    {
        var snapshot = new ServerFileLayoutSnapshot();
        if (FindServerFilesScrollViewer() is { } scrollViewer)
        {
            snapshot.ScrollOffset = scrollViewer.Offset;
        }
        var realizedItems = new List<(int Index, double Y, double Height)>();
        for (var index = 0; index < _serverFiles.Count; index++)
        {
            var item = _serverFiles[index];
            snapshot.ItemIndices[item] = index;
            if (ServerFilesListBox.ContainerFromItem(item) is not Control container)
            {
                continue;
            }

            var position = container.TranslatePoint(new Point(0, 0), ServerFilesListBox);
            if (position.HasValue)
            {
                snapshot.VisiblePositions[item] = position.Value.Y;
                realizedItems.Add((index, position.Value.Y, container.Bounds.Height));
            }
        }

        var measuredStrides = new List<double>();
        for (var index = 1; index < realizedItems.Count; index++)
        {
            var previous = realizedItems[index - 1];
            var current = realizedItems[index];
            var indexDistance = current.Index - previous.Index;
            var stride = indexDistance > 0 ? (current.Y - previous.Y) / indexDistance : 0.0;
            if (stride > 1.0 && double.IsFinite(stride))
            {
                measuredStrides.Add(stride);
            }
        }

        if (measuredStrides.Count == 0)
        {
            measuredStrides.AddRange(realizedItems
                .Select(item => item.Height)
                .Where(height => height > 1.0 && double.IsFinite(height)));
        }

        if (measuredStrides.Count > 0)
        {
            measuredStrides.Sort();
            snapshot.ItemStride = measuredStrides[measuredStrides.Count / 2];
        }

        return snapshot;
    }

    private ScrollViewer? FindServerFilesScrollViewer()
    {
        return ServerFilesListBox
            .GetVisualDescendants()
            .OfType<ScrollViewer>()
            .FirstOrDefault();
    }

    private void RestoreServerFileScrollAnchor(ServerFileLayoutSnapshot previousLayout)
    {
        var scrollViewer = FindServerFilesScrollViewer();
        if (scrollViewer is null || previousLayout.ScrollOffset is not { } previousOffset)
        {
            return;
        }

        // Avalonia may preserve the old raw offset while the extent is shrinking and
        // then clamp it to the new maximum. With a virtualized ListBox this can move
        // the viewport straight to the bottom. Reconstruct the intended offset from
        // the first previously visible item that survived the tree change.
        var anchor = previousLayout.VisiblePositions
            .Where(entry => previousLayout.ItemIndices.ContainsKey(entry.Key)
                && _serverFiles.IndexOf(entry.Key) >= 0)
            .OrderBy(entry => Math.Abs(entry.Value))
            .FirstOrDefault();

        var targetY = previousOffset.Y;
        if (anchor.Key is not null
            && previousLayout.ItemIndices.TryGetValue(anchor.Key, out var oldIndex))
        {
            var newIndex = _serverFiles.IndexOf(anchor.Key);
            if (ServerFilesListBox.ContainerFromItem(anchor.Key) is Control container
                && container.TranslatePoint(new Point(0, 0), ServerFilesListBox) is { } newPosition)
            {
                targetY = scrollViewer.Offset.Y + newPosition.Y - anchor.Value;
            }
            else if (previousLayout.ItemStride > 0.0)
            {
                targetY += (newIndex - oldIndex) * previousLayout.ItemStride;
            }
        }

        var maximumY = Math.Max(0.0, scrollViewer.Extent.Height - scrollViewer.Viewport.Height);
        targetY = Math.Clamp(targetY, 0.0, maximumY);
        if (Math.Abs(scrollViewer.Offset.Y - targetY) > 0.1)
        {
            scrollViewer.Offset = new Vector(previousOffset.X, targetY);
        }
    }

    private void AnimateServerFileReflow(ServerFileLayoutSnapshot previousLayout, int reflowVersion)
    {
        if (reflowVersion != _serverFileReflowVersion)
        {
            return;
        }

        RestoreServerFileScrollAnchor(previousLayout);
        Dispatcher.UIThread.Post(
            () => AnimateServerFileReflowCore(previousLayout, reflowVersion),
            DispatcherPriority.Render);
    }

    private void AnimateServerFileReflowCore(ServerFileLayoutSnapshot previousLayout, int reflowVersion)
    {
        if (reflowVersion != _serverFileReflowVersion)
        {
            return;
        }

        // Loaded 与 Render 之间虚拟化面板还可能更新一次 Extent 并再次钳制
        // Offset；在最终测量前再锚定一次，避免出现一帧后的向下跳动。
        RestoreServerFileScrollAnchor(previousLayout);

        var containers = new List<(ServerFileItem Item, int NewIndex, Control Container)>();
        for (var newIndex = 0; newIndex < _serverFiles.Count; newIndex++)
        {
            var item = _serverFiles[newIndex];
            if (ServerFilesListBox.ContainerFromItem(item) is not Control container)
            {
                continue;
            }

            // The list owns this transform. Clear any earlier reflow animation before
            // measuring the item's new layout position.
            container.RenderTransform = null;
            containers.Add((item, newIndex, container));
        }

        foreach (var (item, newIndex, container) in containers)
        {
            var position = container.TranslatePoint(new Point(0, 0), ServerFilesListBox);
            if (!position.HasValue)
            {
                continue;
            }

            double offsetY;
            if (previousLayout.VisiblePositions.TryGetValue(item, out var oldY))
            {
                offsetY = oldY - position.Value.Y;
            }
            else if (previousLayout.ItemStride > 0.0
                && previousLayout.ItemIndices.TryGetValue(item, out var oldIndex))
            {
                // The item was outside the realized viewport before the change,
                // but is visible now. Its index delta reconstructs the old visual
                // position so it joins the same reflow animation instead of popping in.
                offsetY = (oldIndex - newIndex) * previousLayout.ItemStride;
            }
            else
            {
                continue;
            }

            if (Math.Abs(offsetY) < 0.5)
            {
                continue;
            }

            var transform = new TranslateTransform { Y = offsetY };
            transform.Transitions = new Transitions
            {
                new DoubleTransition
                {
                    Property = TranslateTransform.YProperty,
                    Duration = TimeSpan.FromMilliseconds(190),
                    Easing = new CubicEaseOut(),
                },
            };
            container.RenderTransform = transform;

            Dispatcher.UIThread.Post(
                () => transform.Y = 0.0,
                DispatcherPriority.Loaded);
        }
    }

    private static bool IsServerPathDescendant(string candidate, string folderPath)
    {
        return candidate.StartsWith(folderPath + "/", StringComparison.OrdinalIgnoreCase);
    }

    private async Task BindSelectedFilesToSlotAsync(string slot)
    {
        var selectedItems = ServerFilesListBox.SelectedItems?.OfType<ServerFileItem>()
            .Where(item => !item.IsFolder && !item.IsUploadFolder).ToList() ?? [];
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

    private async Task<string?> UploadFileToServerAsync(
        string filePath,
        string? remoteName = null,
        ServerFileItem? folderChild = null)
    {
        string? finalName = null;
        ServerFileItem? uploadItem = folderChild;
        string? activeUploadId = null;
        await _uploadSerialLock.WaitAsync();
        try
        {
            if (!_client.IsConnected)
            {
                Log("未连接到服务器。");
                return null;
            }

            var fileInfo = new FileInfo(filePath);
            var requestedName = NormalizeRemotePath(string.IsNullOrWhiteSpace(remoteName) ? fileInfo.Name : remoteName);
            uploadItem ??= new ServerFileItem
            {
                Name = requestedName,
                IsUploading = true,
                Status = "计算 SHA256",
                TotalBytes = fileInfo.Length,
                SentBytes = 0,
                ModifiedAt = DateTimeOffset.Now,
            };

            uploadItem.IsUploading = true;
            uploadItem.UploadCompleted = false;
            uploadItem.UploadFailed = false;
            uploadItem.Status = "计算 SHA256";
            uploadItem.TotalBytes = fileInfo.Length;
            uploadItem.SentBytes = 0;
            if (folderChild is null)
            {
                _uploadingFiles.RemoveAll(item => string.Equals(item.Name, uploadItem.Name, StringComparison.OrdinalIgnoreCase));
                _uploadingFiles.Insert(0, uploadItem);
                RefreshServerFilesView();
            }

            var sha256 = await ComputeSha256HexAsync(filePath);
            uploadItem.Status = "准备上传";

            _uploadReadyTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
            await _client.SendCommandAsync(new { command = "upload_init", name = requestedName, size = fileInfo.Length, sha256 });
            var ready = await _uploadReadyTcs.Task.WaitAsync(TimeSpan.FromSeconds(30));
            activeUploadId = ready.UploadId;

            _uploadItemsById[ready.UploadId] = uploadItem;
            if (folderChild is null)
            {
                uploadItem.Name = ready.Name;
            }
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
            var done = await _uploadDoneTcs.Task.WaitAsync(TimeSpan.FromMinutes(2));

            _uploadItemsById.TryRemove(ready.UploadId, out _);
            uploadItem.IsUploading = false;
            if (folderChild is null)
            {
                uploadItem.Name = done.FinalName;
            }
            finalName = done.FinalName;
            _hiddenTrainingAudioFiles.Remove(finalName);
            uploadItem.Status = "完成";
            uploadItem.UploadCompleted = true;
            if (folderChild is null)
            {
                _uploadingFiles.RemoveAll(item => ReferenceEquals(item, uploadItem));
            }
            if (folderChild is null)
            {
                await _client.SendCommandAsync(new { command = "files_list" });
            }
        }
        catch (OperationCanceledException)
        {
            Log("上传已取消：连接已断开。");
            if (uploadItem is not null)
            {
                uploadItem.IsUploading = false;
                uploadItem.UploadFailed = true;
                uploadItem.Status = "已取消";
            }
        }
        catch (Exception ex)
        {
            Log($"上传失败: {ex.Message}");
            if (folderChild is null)
            {
                ShowErrorToast("上传失败");
            }
            if (uploadItem is not null)
            {
                uploadItem.IsUploading = false;
                uploadItem.UploadFailed = true;
                uploadItem.Status = ex is TimeoutException ? "上传超时" : "上传失败";
            }
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(activeUploadId))
            {
                _uploadItemsById.TryRemove(activeUploadId, out _);
            }
            if (folderChild is null && uploadItem is not null && !uploadItem.UploadCompleted)
            {
                _uploadingFiles.RemoveAll(item => ReferenceEquals(item, uploadItem));
                RefreshServerFilesView();
            }
            _uploadReadyTcs = null;
            _uploadDoneTcs = null;
            _uploadSerialLock.Release();
        }
        return finalName;
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
                    vm.IsLoading = false;
                    vm.IsLoaded = false;
                    vm.StatusBrush = errorBrush;
                    vm.StatusHint = "加载失败";
                }
                else if (isLoading)
                {
                    vm.IsLoading = true;
                    vm.IsLoaded = false;
                    vm.StatusBrush = loadingBrush;
                    vm.StatusHint = "加载中…";
                }
                else
                {
                    vm.IsLoading = false;
                    vm.IsLoaded = true;
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
        bool canStartViaServer = _client.IsConnected
            && _serverConfigurationAccepted
            && (_serverPassthroughVoice || _modelState == ModelState.Ready);
        StreamingToggleButton.IsEnabled = _isPlaying || canStartBypass || canStartViaServer;
    }

    private void UpdateStreamingUi(bool isStreaming)
    {
        _isPlaying = isStreaming;
        StreamingToggleButton.Content = isStreaming ? "停止" : "开始变声";
        InputDeviceComboBox.IsEnabled = !isStreaming && _audioInputDevices.Count > 0;
        OutputDeviceComboBox.IsEnabled = !isStreaming && _audioOutputDevices.Count > 0;
        GlobalStatusTextBlock.Text = isStreaming
            ? "变声中"
            : _client.IsConnected
                ? (_client.IsLocalConnection ? "本地直连" : "已连接")
                : "未连接";
    }

    private void ScheduleRealtimeConfigSend()
    {
        ScheduleClientSettingsSave();
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
            "speaker_id",
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
            "stream_chunk_ms",
            "formant_shift",
            "index_rate",
            "speaker_id",
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

        if (!_serverPassthroughVoice && !_bypassServerVoice && !EnsureRequiredBaseModelSlotsConfigured())
        {
            return;
        }
        // Local raw mode does not send audio to the server, but the newly
        // connected audio endpoint still needs one valid baseline config.
        // Put it in server passthrough until the user selects a server mode.
        var passthrough = _serverPassthroughVoice || _bypassServerVoice;
        var modelPath = passthrough ? string.Empty : _modelPath;
        var indexPath = passthrough ? string.Empty : _indexPath;
        var indexRate = passthrough ? 0.0f : _indexRate;

        var currentConfig = new Dictionary<string, object>
        {
            { "model_path", modelPath },
            { "index_path", indexPath },
            { "speaker_id", _speakerId },
            { "f0_up_key", _f0UpKey },
            { "block_time", _blockTime },
            { "crossfade_length", _crossfadeLength },
            { "extra_time", _extraTime },
            { "stream_chunk_ms", _serverStreamChunkMs },
            { "formant_shift", _formantShift },
            { "f0method", _f0Method },
            { "index_rate", indexRate },
            { "passthrough", passthrough },
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
            "speaker_id",
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
        _emaServerQueueLatencyMs = 0;
        _hasLatencyEstimate = false;
        _hasOutputSequence = false;
        _lastOutputSequence = 0;
        Interlocked.Exchange(ref _lastLatencyUiUpdateNs, 0);
        _latencySamples.Clear();
    }

    private double GetAdaptivePacketDurationMs(double observedPacketMs = 0.0)
    {
        if (double.IsFinite(observedPacketMs) && observedPacketMs > 0.0)
        {
            return observedPacketMs;
        }

        double estimatedPacketMs = _jitterEstimator.PacketDurationMs;
        if (estimatedPacketMs > 0.0)
        {
            return estimatedPacketMs;
        }

        if (_effectiveServerChunkMs > 0)
        {
            return _effectiveServerChunkMs;
        }

        return Math.Max(10.0, _serverStreamChunkMs);
    }

    private int GetEffectiveTargetBufferMs(double observedPacketMs = 0.0)
    {
        if (!_useAdaptiveBuffer)
        {
            return _targetBufferLatency;
        }

        int targetMs = _jitterEstimator.GetTargetBufferMs(
            GetAdaptivePacketDurationMs(observedPacketMs),
            AdaptiveSchedulerSlackMs);
        RefreshAdaptiveBufferStatus(targetMs);
        return targetMs;
    }

    private void RefreshAdaptiveBufferStatus(int targetMs, bool force = false)
    {
        long nowNs = GetMonoNs();
        long previousNs = Interlocked.Read(ref _lastAdaptiveStatusUpdateNs);
        if (!force && previousNs > 0 && nowNs - previousNs < AdaptiveStatusUpdateIntervalNs)
        {
            return;
        }
        Interlocked.Exchange(ref _lastAdaptiveStatusUpdateNs, nowNs);

        double baseMs = _jitterEstimator.BaseTargetMs;
        double protectionMs = _jitterEstimator.ProtectionMs;
        double lateP95Ms = _jitterEstimator.LateQuantileMs;
        double underrunMs = _jitterEstimator.UnderrunBoostMs;
        string text = $"自动目标 {targetMs} ms · 设备/分包下限 {baseMs:F0} ms · 网络保护 {protectionMs:F0} ms · P95 {lateP95Ms:F0} ms · 欠载 {underrunMs:F0} ms";

        void ApplyText()
        {
            if (AdaptiveBufferStatusText != null)
            {
                AdaptiveBufferStatusText.Text = text;
            }
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            ApplyText();
        }
        else
        {
            Dispatcher.UIThread.Post(ApplyText);
        }
    }

    private bool ShouldHoldPlayback(int bufferedBytesBeforeRead, int requestedBytes)
    {
        if (!_useAdaptiveBuffer
            || _bypassServerVoice
            || !_isPlaying
            || !_playbackStarted
            || requestedBytes <= 0)
        {
            return false;
        }

        double bufferedMs = bufferedBytesBeforeRead * 1000.0 / (SampleRate * 4.0);
        double requestedMs = requestedBytes * 1000.0 / (SampleRate * 4.0);
        double packetMs = GetAdaptivePacketDurationMs();
        int targetMs = GetEffectiveTargetBufferMs(packetMs);

        if (Volatile.Read(ref _adaptiveRebuffering) != 0)
        {
            if (bufferedMs + 0.01 < targetMs)
            {
                return true;
            }

            if (Interlocked.Exchange(ref _adaptiveRebuffering, 0) != 0)
            {
                Dispatcher.UIThread.Post(() =>
                    Log($"自动缓冲已恢复：{bufferedMs:F0}ms / 目标 {targetMs}ms"));
            }
            return false;
        }

        if (bufferedMs + 0.01 >= requestedMs)
        {
            return false;
        }

        double shortageMs = Math.Max(requestedMs - bufferedMs, targetMs - bufferedMs);
        _jitterEstimator.ReportUnderrun(shortageMs, packetMs);
        int raisedTargetMs = GetEffectiveTargetBufferMs(packetMs);
        Interlocked.Exchange(ref _adaptiveRebuffering, 1);
        int underrunCount = Interlocked.Increment(ref _adaptiveUnderrunCount);
        Dispatcher.UIThread.Post(() =>
            Log($"自动缓冲检测到欠载 #{underrunCount}：剩余 {bufferedMs:F0}ms，重缓冲至 {raisedTargetMs}ms"));
        return true;
    }

    private async Task StartStreamingAsync()
    {
        if (_isPlaying)
        {
            return;
        }


        if (!_bypassServerVoice && !_serverPassthroughVoice && !EnsureRequiredBaseModelSlotsConfigured())
        {
            return;
        }
        if (!_bypassServerVoice && !_serverConfigurationAccepted)
        {
            throw new InvalidOperationException("服务器尚未确认本次连接的参数配置，请稍候重试。");
        }
        if (!_bypassServerVoice && !_serverPassthroughVoice && _modelState != ModelState.Ready)
        {
            throw new InvalidOperationException("模型尚未就绪，请先选择并等待模型加载完成。");
        }

        _streamStartNs = GetMonoNs();
        _captureMediaCursorNs = 0;
        Interlocked.Exchange(ref _audioSequence, 0);
        long sessionLong = Interlocked.Increment(ref _streamSessionId);
        ulong sessionId = unchecked((ulong)sessionLong);
        Interlocked.Exchange(ref _pendingLatencyReset, 0);
        Interlocked.Exchange(ref _adaptiveRebuffering, 0);
        Interlocked.Exchange(ref _adaptiveUnderrunCount, 0);
        ResetLatencyTracking();

        if (!_bypassServerVoice)
        {
            await _client.SendCommandAsync(new { command = "stream_start", session_id = sessionId, protocol = 2 });
        }

        _waveProvider = new BufferedWaveProvider(WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, Channels))
        {
            BufferDuration = TimeSpan.FromMilliseconds(_bufferCapacityMs),
            DiscardOnBufferOverflow = true,
            ReadFully = false,
        };
        ResetWaveformHistory();
        _playbackWaveProvider = new PlaybackTapWaveProvider(
            _waveProvider,
            _playbackTimestampSync,
            ShouldHoldPlayback,
            CapturePlaybackWaveform);

        var selectedOutput = OutputDeviceComboBox.SelectedItem as AudioDeviceItem;
        if (selectedOutput != null && !string.IsNullOrWhiteSpace(selectedOutput.Id))
        {
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                _outputDevice = enumerator.GetDevice(selectedOutput.Id);
                _waveOut = new WasapiOut(_outputDevice, AudioClientShareMode.Shared, true, AudioDeviceBufferMs);
            }
            catch
            {
                _outputDevice?.Dispose();
                _outputDevice = null;
                _waveOut = new WasapiOut(AudioClientShareMode.Shared, AudioDeviceBufferMs);
            }
        }
        else
        {
            _waveOut = new WasapiOut(AudioClientShareMode.Shared, AudioDeviceBufferMs);
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
        _waveIn = inputDevice != null ? new WasapiCapture(inputDevice, true, AudioDeviceBufferMs) : new WasapiCapture();
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

        UpdateCaptureReadBufferSize();
        Interlocked.Exchange(ref _captureActive, 1);
        _waveIn.StartRecording();

        Log(_bypassServerVoice ? "音频录制已开始 - 原声输出中" : _serverPassthroughVoice ? "音频录制已开始 - 原声经服务器输出中" : "音频录制已开始 - 变声进行中");
    }

    private void UpdateCaptureReadBufferSize()
    {
        int blockMs = Math.Max(10, (int)Math.Round(_blockTime * 1000.0));
        int effectiveSliceMs = Math.Max(10, Math.Min(_networkSliceMs, blockMs));
        int chunkBytes = Math.Max(4, SampleRate * effectiveSliceMs * 4 / 1000);
        chunkBytes -= chunkBytes % 4;
        lock (_captureLock)
        {
            if (_captureReadBuffer.Length != chunkBytes)
                _captureReadBuffer = new byte[chunkBytes];
        }
    }

    private void StartWaveformTimer()
    {
        if (_waveformTimer != null) return;

        _waveformTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _waveformTimer.Tick += (_, _) =>
        {
            ExtendWaveformWithSilence();
            DrawWaveform();
        };
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

    private void ExtendWaveformWithSilence()
    {
        if (!_isPlaying)
        {
            return;
        }

        long nowNs = GetMonoNs();
        long lastWallNs = Interlocked.Read(ref _waveformLastDataWallNs);
        if (lastWallNs <= 0)
        {
            Interlocked.Exchange(ref _waveformLastDataWallNs, nowNs);
            return;
        }

        long missingFrameCount = (nowNs - lastWallNs) / WaveformFrameDurationNs - 1;
        if (missingFrameCount <= 0)
        {
            return;
        }

        int maximumFrames = (int)(WaveformWindowNs / WaveformFrameDurationNs) + 2;
        int framesToAppend = (int)Math.Min(missingFrameCount, maximumFrames);

        lock (_waveformOutputLock)
        {
            long timelineNs = _waveformPlaybackTimelineNs;
            if (timelineNs <= 0)
            {
                timelineNs = nowNs - (framesToAppend + 1L) * WaveformFrameDurationNs;
            }
            else if (missingFrameCount > framesToAppend)
            {
                timelineNs = Math.Max(
                    timelineNs,
                    nowNs - (framesToAppend + 1L) * WaveformFrameDurationNs);
            }

            lock (_waveformInputLock)
            {
                for (int index = 0; index < framesToAppend; index++)
                {
                    timelineNs += WaveformFrameDurationNs;
                    var silencePoint = new WaveformPoint(timelineNs, 0f);
                    _waveformInputHistory.Add(silencePoint);
                    _waveformOutputHistory.Add(silencePoint);
                }

                long cutoffNs = timelineNs - WaveformRetentionNs;
                _waveformInputHistory.RemoveAll(point => point.TimestampNs < cutoffNs);
                _waveformOutputHistory.RemoveAll(point => point.TimestampNs < cutoffNs);
            }

            _waveformPlaybackTimelineNs = timelineNs;
        }

        long advancedWallNs = missingFrameCount > framesToAppend
            ? Math.Max(0, nowNs - lastWallNs - WaveformFrameDurationNs)
            : framesToAppend * WaveformFrameDurationNs;
        Interlocked.CompareExchange(
            ref _waveformLastDataWallNs,
            lastWallNs + advancedWallNs,
            lastWallNs);
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

            Interlocked.Exchange(ref _captureActive, 0);
            long endingSession = Interlocked.Read(ref _streamSessionId);
            if (!_bypassServerVoice && _client.IsConnected && endingSession > 0)
            {
                _ = _client.SendCommandAsync(new { command = "stream_stop", session_id = unchecked((ulong)endingSession) });
            }
            lock (_captureLock)
            {
                StopAudioSendLoop();
                _captureMediaCursorNs = 0;
            }
            _streamStartNs = 0;
            Interlocked.Increment(ref _streamSessionId);

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
            Interlocked.Exchange(ref _adaptiveRebuffering, 0);
            Interlocked.Exchange(ref _adaptiveUnderrunCount, 0);
            UpdateStreamingUi(false);
            TotalLatencyTextBlock.Text = "-- ms";
            ServerQueueLatencyTextBlock.Text = "-- ms";
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
        if (_audioSendLoopTask != null && !_audioSendLoopTask.IsCompleted
            && _streamingCts is { IsCancellationRequested: false })
        {
            return;
        }

        _streamingCts?.Cancel();
        _streamingCts?.Dispose();
        _streamingCts = new CancellationTokenSource();
        var token = _streamingCts.Token;
        var signal = new SemaphoreSlim(0);
        _audioSendSignal = signal;
        _audioSendLoopTask = Task.Run(() => AudioSendLoopAsync(signal, token), token);
    }

    private void StopAudioSendLoop()
    {
        var cts = _streamingCts;
        _streamingCts = null;
        _audioSendSignal = null;
        cts?.Cancel();
        cts?.Dispose();
        _audioSendLoopTask = null;

        while (_audioSendQueue.TryDequeue(out _))
        {
        }

        Interlocked.Exchange(ref _audioSendQueueCount, 0);
    }

    private async Task AudioSendLoopAsync(SemaphoreSlim signal, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await signal.WaitAsync(cancellationToken);

                while (!cancellationToken.IsCancellationRequested
                    && _audioSendQueue.TryDequeue(out var messageBytes))
                {
                    Interlocked.Decrement(ref _audioSendQueueCount);
                    await _client.SendAudioAsync(messageBytes, cancellationToken);
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
        finally
        {
            signal.Dispose();
        }
    }

    private void ResyncCaptureTimelineForCallback(int bytesRecorded)
    {
        var captureBuffer = _captureBuffer;
        if (captureBuffer == null || bytesRecorded <= 0) return;

        var format = captureBuffer.WaveFormat;
        int sourceFrames = format.BlockAlign > 0 ? bytesRecorded / format.BlockAlign : 0;
        if (sourceFrames <= 0 || format.SampleRate <= 0) return;

        long incomingDurationNs = (long)Math.Round(sourceFrames * 1_000_000_000.0 / format.SampleRate);
        long queuedDurationNs = (long)Math.Round(captureBuffer.BufferedDuration.TotalMilliseconds * 1_000_000.0);
        long observedOldestNs = GetMonoNs() - incomingDurationNs - queuedDurationNs;
        long cursor = Interlocked.Read(ref _captureMediaCursorNs);
        if (cursor <= 0 || Math.Abs(observedOldestNs - cursor) > CaptureTimestampResyncThresholdNs)
        {
            Interlocked.Exchange(ref _captureMediaCursorNs, observedOldestNs);
        }
    }

    private long TakeCaptureTimestampNs(int sampleCount)
    {
        long durationNs = sampleCount * NsPerSample;
        long start = Interlocked.Read(ref _captureMediaCursorNs);
        if (start <= 0)
        {
            start = GetMonoNs() - durationNs;
            Interlocked.Exchange(ref _captureMediaCursorNs, start);
        }
        Interlocked.Add(ref _captureMediaCursorNs, durationNs);
        return start;
    }

    private void OnAudioDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded == 0 || Volatile.Read(ref _captureActive) == 0)
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
                if (Volatile.Read(ref _captureActive) == 0)
                    return;
                ResyncCaptureTimelineForCallback(e.BytesRecorded);
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
                        long waveformStartNs = TakeCaptureTimestampNs(alignedRead / 4);
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

                    long tsNs = TakeCaptureTimestampNs(alignedRead / 4);

                    long sessionLong = Interlocked.Read(ref _streamSessionId);
                    if (sessionLong <= 0)
                    {
                        continue;
                    }
                    uint sequence = unchecked((uint)(Interlocked.Increment(ref _audioSequence) - 1));
                    var messageBytes = AudioProtocol.BuildInputFrame(
                        unchecked((ulong)sessionLong), sequence, unchecked((ulong)tsNs), 0,
                        _captureReadBuffer, alignedRead);
                    AppendInputSourceSamples(_waveformInputSourceHistory, _waveformInputAccumulator, _waveformInputSourceLock, _captureReadBuffer, 0, alignedRead, tsNs);


                    if (Volatile.Read(ref _captureActive) == 0)
                        return;

                    _audioSendQueue.Enqueue(messageBytes);
                    var currentCount = Interlocked.Increment(ref _audioSendQueueCount);
                    var dropped = false;
                    while (Volatile.Read(ref _audioSendQueueCount) > _maxAudioSendQueuePackets
                        && _audioSendQueue.TryDequeue(out _))
                    {
                        Interlocked.Decrement(ref _audioSendQueueCount);
                        dropped = true;
                    }
                    currentCount = Volatile.Read(ref _audioSendQueueCount);
                    if (dropped)
                    {
                        var now = GetMonoNs();
                        if (now - _lastSendDropLogNs > 2_000_000_000)
                        {
                            _lastSendDropLogNs = now;
                            Dispatcher.UIThread.Post(() => Log("警告: 发送队列溢出，音频丢包"));
                        }
                    }

                    if (currentCount == 1)
                    {
                        _audioSendSignal?.Release();
                    }

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

    private void TrimPlaybackBufferTo(int targetMs)
    {
        var provider = _waveProvider;
        if (provider == null) return;
        int targetBytes = Math.Max(0, targetMs) * SampleRate * 4 / 1000;

        lock (_playbackTimestampSync)
        {
            int bytesToDrop = Math.Max(0, provider.BufferedBytes - targetBytes);
            bytesToDrop -= bytesToDrop % 4;
            if (bytesToDrop <= 0) return;

            var scratch = new byte[Math.Min(bytesToDrop, 64 * 1024)];
            int remainingBytes = bytesToDrop;
            int droppedSamples = 0;
            while (remainingBytes > 0)
            {
                int request = Math.Min(remainingBytes, scratch.Length);
                int read = provider.Read(scratch, 0, request);
                if (read <= 0) break;
                int mediaBytes = Math.Min(read, request);
                droppedSamples += mediaBytes / 4;
                remainingBytes -= mediaBytes;
            }

            int remainingSamples = droppedSamples;
            while (remainingSamples > 0 && _playbackTimestampSegments.Count > 0)
            {
                var segment = _playbackTimestampSegments.Peek();
                int take = Math.Min(remainingSamples, segment.RemainingSamples);
                segment.NextTimestampNs += take * NsPerSample;
                segment.RemainingSamples -= take;
                remainingSamples -= take;
                if (segment.RemainingSamples == 0)
                    _playbackTimestampSegments.Dequeue();
            }

            _playbackExpectedTimestampNs = _playbackTimestampSegments.Count > 0
                ? _playbackTimestampSegments.Peek().NextTimestampNs
                : 0;
        }
    }

    private void HandleBinaryMessage(byte[] messageData)
    {
        if (!_isPlaying || _waveProvider == null) return;

        try
        {
            if (!AudioProtocol.TryParseOutputFrame(messageData, out var header, out int audioOffset))
                return;

            long currentSession = Interlocked.Read(ref _streamSessionId);
            if (currentSession <= 0 || header.SessionId != unchecked((ulong)currentSession))
                return;

            int audioLength = messageData.Length - audioOffset;
            if (audioLength <= 0) return;

            double incomingMs = audioLength / 4.0 * 1000.0 / SampleRate;
            long arrivalNs = GetMonoNs();
            bool hasValidMediaTimestamp = _streamStartNs > 0
                && header.TimestampNs >= (ulong)_streamStartNs
                && header.TimestampNs <= (ulong)long.MaxValue;

            bool sequenceGap = _hasOutputSequence
                && header.Sequence != unchecked(_lastOutputSequence + 1);
            bool discontinuity = sequenceGap || (header.Flags & AudioProtocol.FlagDiscontinuity) != 0;
            _lastOutputSequence = header.Sequence;
            _hasOutputSequence = true;

            if (discontinuity)
            {
                TrimPlaybackBufferTo(0);
                ResetLatencyTracking();
                _lastOutputSequence = header.Sequence;
                _hasOutputSequence = true;
                if (_useAdaptiveBuffer && _playbackStarted)
                {
                    Interlocked.Exchange(ref _adaptiveRebuffering, 1);
                    Dispatcher.UIThread.Post(() => Log("检测到音频时间线中断，自动重新蓄水。"));
                }
            }

            if (hasValidMediaTimestamp)
            {
                if (Interlocked.Exchange(ref _pendingLatencyReset, 0) != 0)
                    ResetLatencyTracking();
                _jitterEstimator.Update((long)header.TimestampNs, arrivalNs, incomingMs);
            }

            int effectiveTargetLatency = GetEffectiveTargetBufferMs(hasValidMediaTimestamp ? 0.0 : incomingMs);
            double bufferBeforeAddMs = _waveProvider.BufferedDuration.TotalMilliseconds;
            double hardLimitMs = Math.Min(_maxBufferMs, Math.Max(0, _bufferCapacityMs - incomingMs));
            if (bufferBeforeAddMs > hardLimitMs
                || bufferBeforeAddMs + incomingMs > _bufferCapacityMs)
            {
                // Always make room for the newest real-time packet. This remains
                // a last-resort bound; normal automatic correction happens only
                // by shortening silent excess below.
                int trimTargetMs = Math.Min(effectiveTargetLatency, Math.Max(0, _bufferCapacityMs / 2));
                TrimPlaybackBufferTo(trimTargetMs);
                bufferBeforeAddMs = _waveProvider.BufferedDuration.TotalMilliseconds;
            }

            bool isSilent = CalculateRms(messageData, audioOffset, audioLength) < _silenceThreshold;
            int bytesToAdd = audioLength;
            if (_useAdaptiveBuffer && isSilent)
            {
                // The post-arrival high watermark includes one packet because a
                // paced stream naturally has a saw-tooth occupancy. Shorten only
                // the exact silent excess; dropping an entire inference block was
                // the source of periodic 250 ms buffer collapses.
                double highWatermarkMs = effectiveTargetLatency + incomingMs + _silenceDropOffset;
                double excessMs = bufferBeforeAddMs + incomingMs - highWatermarkMs;
                if (excessMs > 0.0)
                {
                    int bytesToDrop = (int)Math.Floor(excessMs * SampleRate * 4.0 / 1000.0);
                    bytesToDrop -= bytesToDrop % 4;
                    bytesToAdd = Math.Max(0, audioLength - bytesToDrop);
                }
            }
            else if (!_useAdaptiveBuffer
                && bufferBeforeAddMs > effectiveTargetLatency + _silenceDropOffset
                && isSilent)
            {
                // Preserve the manual mode's existing fixed-buffer behavior.
                return;
            }

            if (bytesToAdd > 0)
            {
                AddPlaybackSamples(
                    messageData, audioOffset, bytesToAdd,
                    hasValidMediaTimestamp ? (long)header.TimestampNs : 0);
            }

            if (!_playbackStarted && _waveOut != null && _waveProvider.BufferedBytes > 0)
            {
                var minStartBufferMs = Math.Max(effectiveTargetLatency, 30);
                if (_waveProvider.BufferedDuration.TotalMilliseconds >= minStartBufferMs)
                {
                    _waveOut.Play();
                    _playbackStarted = true;
                    Log($"缓冲达到 {_waveProvider.BufferedDuration.TotalMilliseconds:F0}ms，开始播放（目标 {effectiveTargetLatency}ms）");
                }
            }

            if (hasValidMediaTimestamp)
            {
                double ageAtReceiveMs = (arrivalNs - (long)header.TimestampNs) / 1_000_000.0;
                double totalMsNow = ageAtReceiveMs + bufferBeforeAddMs;
                double serverQueueMs = header.InputQueueMs + header.OutputQueueMs;

                if (!_hasLatencyEstimate)
                {
                    _emaTotalLatencyMs = totalMsNow;
                    _emaInferLatencyMs = header.ProcessingMs;
                    _emaServerQueueLatencyMs = serverQueueMs;
                    _hasLatencyEstimate = true;
                }
                else
                {
                    _emaTotalLatencyMs = LatencyEmaAlpha * totalMsNow + (1.0 - LatencyEmaAlpha) * _emaTotalLatencyMs;
                    _emaInferLatencyMs = LatencyEmaAlpha * header.ProcessingMs + (1.0 - LatencyEmaAlpha) * _emaInferLatencyMs;
                    _emaServerQueueLatencyMs = LatencyEmaAlpha * serverQueueMs + (1.0 - LatencyEmaAlpha) * _emaServerQueueLatencyMs;
                }

                _latencySamples.Add(new LatencySample
                {
                    TsNs = arrivalNs,
                    TotalMs = totalMsNow,
                    ServerQueueMs = serverQueueMs,
                    InferMs = header.ProcessingMs,
                });
                long cutoff = arrivalNs - (long)(LatencySampleWindowSeconds * 1_000_000_000.0);
                while (_latencySamples.Count > 0 && _latencySamples[0].TsNs < cutoff)
                    _latencySamples.RemoveAt(0);

                long lastUiUpdateNs = Interlocked.Read(ref _lastLatencyUiUpdateNs);
                if (lastUiUpdateNs <= 0 || arrivalNs - lastUiUpdateNs >= LatencyUiUpdateIntervalNs)
                {
                    Interlocked.Exchange(ref _lastLatencyUiUpdateNs, arrivalNs);
                    double totalLatencyMs = _emaTotalLatencyMs;
                    double serverQueueLatencyMs = _emaServerQueueLatencyMs;
                    double inferLatencyMs = _emaInferLatencyMs;
                    Dispatcher.UIThread.Post(() =>
                    {
                        TotalLatencyTextBlock.Text = $"{totalLatencyMs:F0} ms";
                        ServerQueueLatencyTextBlock.Text = $"{serverQueueLatencyMs:F0} ms";
                        InferenceLatencyTextBlock.Text = $"{inferLatencyMs:F0} ms";
                    });
                }
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

        long nowNs = GetMonoNs();
        canvas.Children.Clear();

        long latestInputNs = inputHistory.Length > 0 ? inputHistory[^1].TimestampNs : 0;
        long latestOutputNs = outputHistory.Length > 0 ? outputHistory[^1].TimestampNs : 0;
        long availableEndNs = latestInputNs == 0
            ? latestOutputNs == 0 ? nowNs : latestOutputNs
            : latestOutputNs == 0
                ? latestInputNs
                : Math.Min(latestInputNs, latestOutputNs);

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
            long previousTimestampNs = startTimestampNs;
            bool hasVisiblePoint = false;

            foreach (var point in history)
            {
                if (point.TimestampNs < startTimestampNs || point.TimestampNs > endTimestampNs)
                {
                    continue;
                }

                if (point.TimestampNs - previousTimestampNs > WaveformFrameDurationNs * 2)
                {
                    double gapStartX = hasVisiblePoint
                        ? (previousTimestampNs + WaveformFrameDurationNs - startTimestampNs) * width / WaveformWindowNs
                        : 0.0;
                    double gapEndX = (point.TimestampNs - WaveformFrameDurationNs - startTimestampNs) * width / WaveformWindowNs;
                    points.Add(new Avalonia.Point(Math.Clamp(gapStartX, 0.0, width), baselineY));
                    points.Add(new Avalonia.Point(Math.Clamp(gapEndX, 0.0, width), baselineY));
                }

                double x = (point.TimestampNs - startTimestampNs) * width / WaveformWindowNs;
                double db = 20.0 * Math.Log10(Math.Max(point.Rms, 0.000001f));
                double normalized = Math.Clamp(
                    (db - WaveformFloorDb) / (WaveformCeilingDb - WaveformFloorDb),
                    0.0,
                    1.0);
                points.Add(new Avalonia.Point(x, baselineY - normalized * amplitude));
                previousTimestampNs = point.TimestampNs;
                hasVisiblePoint = true;
            }

            if (!hasVisiblePoint)
            {
                points.Add(new Avalonia.Point(0.0, baselineY));
                points.Add(new Avalonia.Point(width, baselineY));
            }
            else if (endTimestampNs - previousTimestampNs > WaveformFrameDurationNs)
            {
                double silenceStartX = (previousTimestampNs + WaveformFrameDurationNs - startTimestampNs)
                    * width / WaveformWindowNs;
                points.Add(new Avalonia.Point(Math.Clamp(silenceStartX, 0.0, width), baselineY));
                points.Add(new Avalonia.Point(width, baselineY));
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
