using System.Collections.Concurrent;
using System.Text;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using ClientAvalonia.Services;
using Material.Icons;

namespace ClientAvalonia.Dialogs;

public sealed record LocalEnvironmentConfiguration(string ServerDirectory, bool IsVerified);

public partial class LocalEnvironmentWindow : Window
{
    private sealed class DirectProgress(Action<string> handler) : IProgress<string>
    {
        public void Report(string value) => handler(value);
    }

    private const int MaxOutputLength = 60_000;
    private readonly ConcurrentQueue<string> _pendingOutput = new();
    private readonly DispatcherTimer _outputFlushTimer;
    private CancellationTokenSource? _operationCts;
    private bool _verified;
    private bool _busy;
    private bool _updateAvailable;
    private bool _sourceChanged;
    private string _verifiedDirectory = string.Empty;

    public bool UpdateAvailable => _updateAvailable;
    public bool RequiresEnvironmentRecheck => _sourceChanged;

    public LocalEnvironmentWindow()
        : this(AppPaths.DefaultLocalServerDirectory, false, false)
    {
    }

    public LocalEnvironmentWindow(string serverDirectory, bool isVerified, bool updateAvailable = false)
    {
        InitializeComponent();
        _outputFlushTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(75) };
        _outputFlushTimer.Tick += (_, _) => FlushPendingOutput();
        _updateAvailable = updateAvailable;
        ServerDirectoryTextBox.Text = string.IsNullOrWhiteSpace(serverDirectory)
            ? AppPaths.DefaultLocalServerDirectory
            : Path.GetFullPath(serverDirectory);
        if (isVerified && LocalServerEnvironmentChecker.HasInstalledEnvironment(ServerDirectoryTextBox.Text))
        {
            _verified = true;
            _verifiedDirectory = ServerDirectoryTextBox.Text;
            SetStatus(true, "本地环境已就绪", "依赖检查已通过。");
            SaveButton.IsEnabled = true;
        }
        else
        {
            SetUnverifiedStatus();
        }
        RefreshActionButtons();
    }

    protected override void OnClosed(EventArgs e)
    {
        _outputFlushTimer.Stop();
        _operationCts?.Cancel();
        _operationCts?.Dispose();
        _operationCts = null;
        base.OnClosed(e);
    }

    private async void Browse_OnClick(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择 RVC Streaming Server 文件夹",
            AllowMultiple = false,
        });
        var folderPath = folders.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(folderPath)) return;

        ServerDirectoryTextBox.Text = Path.GetFullPath(folderPath);
        InvalidateVerification();
        _updateAvailable = false;
        DependencyOutputTextBox.Text = string.Empty;
        DependencyOutputTextBox.IsVisible = false;
        SetUnverifiedStatus();
        RefreshActionButtons();
    }

    private async void DownloadServer_OnClick(object? sender, RoutedEventArgs e)
    {
        var serverDirectory = ServerDirectoryTextBox.Text?.Trim() ?? string.Empty;
        var (token, output) = BeginOperation("正在从 GitHub 下载 Server");
        try
        {
            var result = await LocalServerEnvironmentChecker.DownloadServerAsync(
                serverDirectory,
                output,
                token
            );
            AppendResultDetails(result);
            if (result.Success) _updateAvailable = false;
            SetStatus(
                result.Success ? null : false,
                result.Summary,
                result.Success ? "请继续下载依赖。" : string.Empty
            );
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            EndOperation();
        }
    }

    private async void InstallDependencies_OnClick(object? sender, RoutedEventArgs e)
    {
        var serverDirectory = ServerDirectoryTextBox.Text?.Trim() ?? string.Empty;
        var (token, output) = BeginOperation("正在通过 Pixi 下载依赖");
        try
        {
            var installResult = await LocalServerEnvironmentChecker.InstallDependenciesAsync(
                serverDirectory,
                output,
                token
            );
            AppendResultDetails(installResult);
            if (!installResult.Success)
            {
                SetStatus(false, installResult.Summary, string.Empty);
                return;
            }

            SetStatus(null, "依赖下载完成，正在验证", string.Empty);
            var checkResult = await LocalServerEnvironmentChecker.CheckAsync(
                serverDirectory,
                token,
                output
            );
            ApplyCheckResult(serverDirectory, checkResult);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            EndOperation();
        }
    }

    private async void CheckDependencies_OnClick(object? sender, RoutedEventArgs e)
    {
        var serverDirectory = ServerDirectoryTextBox.Text?.Trim() ?? string.Empty;
        var (token, output) = BeginOperation("正在通过 Pixi 检查依赖");
        try
        {
            var result = await LocalServerEnvironmentChecker.CheckAsync(serverDirectory, token, output);
            ApplyCheckResult(serverDirectory, result);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            EndOperation();
        }
    }

    private async void UpdateSource_OnClick(object? sender, RoutedEventArgs e)
    {
        var serverDirectory = ServerDirectoryTextBox.Text?.Trim() ?? string.Empty;
        if (!_updateAvailable)
        {
            var (token, output) = BeginOperation("正在检查 Server 源码更新", invalidateVerification: false);
            try
            {
                var result = await LocalServerEnvironmentChecker.CheckForUpdatesAsync(
                    serverDirectory,
                    output,
                    token
                );
                _updateAvailable = result.Success && result.UpdateAvailable;
                SetStatus(
                    result.Success ? (_updateAvailable ? null : true) : false,
                    result.Summary,
                    _updateAvailable ? "点击“更新源码”安装 main 最新版本。" : string.Empty
                );
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                EndOperation();
            }
            return;
        }

        var (updateToken, updateOutput) = BeginOperation("正在更新 Server 源码");
        try
        {
            var result = await LocalServerEnvironmentChecker.UpdateServerAsync(
                serverDirectory,
                updateOutput,
                updateToken
            );
            AppendResultDetails(result);
            if (result.Success)
            {
                _updateAvailable = false;
                _sourceChanged = true;
            }
            SetStatus(
                result.Success ? null : false,
                result.Summary,
                result.Success ? "请重新下载依赖并完成检查。" : string.Empty
            );
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            EndOperation();
        }
    }

    private (CancellationToken Token, IProgress<string> Output) BeginOperation(
        string title,
        bool invalidateVerification = true)
    {
        if (invalidateVerification) InvalidateVerification();
        _busy = true;
        SetStatus(null, title, string.Empty);
        ClearPendingOutput();
        DependencyOutputTextBox.Text = string.Empty;
        DependencyOutputTextBox.IsVisible = true;
        _operationCts?.Cancel();
        _operationCts?.Dispose();
        _operationCts = new CancellationTokenSource();
        RefreshActionButtons();
        EnvironmentCheckProgress.IsVisible = true;
        _outputFlushTimer.Start();
        return (_operationCts.Token, new DirectProgress(QueueOutput));
    }

    private void EndOperation()
    {
        FlushPendingOutput();
        _outputFlushTimer.Stop();
        _busy = false;
        EnvironmentCheckProgress.IsVisible = false;
        _operationCts?.Dispose();
        _operationCts = null;
        RefreshActionButtons();
    }

    private void InvalidateVerification()
    {
        _verified = false;
        _verifiedDirectory = string.Empty;
        SaveButton.IsEnabled = false;
    }

    private void ApplyCheckResult(string serverDirectory, LocalServerEnvironmentCheckResult result)
    {
        AppendResultDetails(result);
        SetStatus(
            result.Success,
            result.Summary,
            result.Success ? "Pixi 环境和运行依赖均可用。" : string.Empty
        );
        if (!result.Success) return;
        _verified = true;
        _verifiedDirectory = Path.GetFullPath(serverDirectory);
    }

    private void SetUnverifiedStatus()
    {
        var serverDirectory = ServerDirectoryTextBox.Text;
        if (LocalServerEnvironmentChecker.HasServerLayout(serverDirectory))
        {
            SetStatus(null, "Server 源码已就绪", "请下载依赖或检查现有环境。");
        }
        else if (LocalServerEnvironmentChecker.CanDownloadServer(serverDirectory))
        {
            SetStatus(null, "尚未安装 Server", "可从 GitHub 下载不含 Git 历史的源码快照。");
        }
        else
        {
            SetStatus(false, "Server 路径不可用", "请选择有效的 Server 目录或空目录。");
        }
    }

    private void RefreshActionButtons()
    {
        var serverDirectory = ServerDirectoryTextBox.Text;
        var hasLayout = LocalServerEnvironmentChecker.HasServerLayout(serverDirectory);
        BrowseButton.IsEnabled = !_busy;
        DownloadServerButton.IsEnabled = !_busy && LocalServerEnvironmentChecker.CanDownloadServer(serverDirectory);
        InstallDependenciesButton.IsEnabled = !_busy && hasLayout;
        CheckDependenciesButton.IsEnabled = !_busy && hasLayout;
        UpdateSourceButton.IsEnabled = !_busy && hasLayout;
        UpdateSourceButtonText.Text = _updateAvailable ? "更新源码" : "检查更新";
        SaveButton.IsEnabled = !_busy && _verified;
    }

    private void AppendResultDetails(LocalServerEnvironmentCheckResult result)
    {
        FlushPendingOutput();
        if (result.Success || string.IsNullOrWhiteSpace(result.Details)) return;
        var current = DependencyOutputTextBox.Text ?? string.Empty;
        if (!current.Contains(result.Details, StringComparison.Ordinal)) AppendOutput(result.Details);
    }

    private void AppendOutput(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        var current = DependencyOutputTextBox.Text ?? string.Empty;
        var updated = string.IsNullOrEmpty(current)
            ? line
            : current + Environment.NewLine + line;
        if (updated.Length > MaxOutputLength)
        {
            updated = "..." + Environment.NewLine + updated[^MaxOutputLength..];
        }
        DependencyOutputTextBox.Text = updated;
        DependencyOutputTextBox.CaretIndex = updated.Length;
    }

    private void QueueOutput(string line)
    {
        if (!string.IsNullOrWhiteSpace(line)) _pendingOutput.Enqueue(line);
    }

    private void FlushPendingOutput()
    {
        if (_pendingOutput.IsEmpty) return;
        var lines = new StringBuilder();
        while (_pendingOutput.TryDequeue(out var line))
        {
            if (lines.Length > 0) lines.AppendLine();
            lines.Append(line);
        }
        AppendOutput(lines.ToString());
    }

    private void ClearPendingOutput()
    {
        while (_pendingOutput.TryDequeue(out _))
        {
        }
    }

    private void SetStatus(bool? success, string title, string details)
    {
        EnvironmentStatusPanel.Classes.Set("success", success == true);
        EnvironmentStatusPanel.Classes.Set("error", success == false);
        EnvironmentStatusTitle.Text = title;
        EnvironmentStatusDetails.Text = details;
        EnvironmentStatusDetails.IsVisible = !string.IsNullOrWhiteSpace(details);
        EnvironmentStatusIcon.Kind = success switch
        {
            true => MaterialIconKind.CheckCircleOutline,
            false => MaterialIconKind.AlertCircleOutline,
            null => MaterialIconKind.ClockOutline,
        };
    }

    private void Save_OnClick(object? sender, RoutedEventArgs e)
    {
        if (!_verified || string.IsNullOrWhiteSpace(_verifiedDirectory)) return;
        Close(new LocalEnvironmentConfiguration(_verifiedDirectory, true));
    }

    private void Cancel_OnClick(object? sender, RoutedEventArgs e) => Close(null);

    private void CloseButton_OnClick(object? sender, RoutedEventArgs e) => Close(null);

    private void Window_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        Close(null);
    }

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
    }
}
