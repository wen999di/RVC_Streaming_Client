using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ClientAvalonia.Models;

namespace ClientAvalonia.Dialogs;

public partial class HubDownloadWindow : Window
{
    private const double TreeIndentSize = 18.0;
    private const int TreeAnimationDurationMs = 150;
    private static readonly List<string> RepositoryHistory = new();
    private static readonly List<string> RevisionHistory = new() { "main", "master", "latest" };

    private readonly Func<string, string, string, Task<HubRepositorySnapshot>>? _browseAsync;
    private readonly Func<HubDownloadRequest, IProgress<HubDownloadProgress>, CancellationToken, Task<HubDownloadResult>>? _downloadAsync;
    private readonly ObservableCollection<HubRepositoryNode> _roots = new();
    private readonly ObservableCollection<HubRepositoryNode> _visibleNodes = new();
    private readonly ObservableCollection<string> _repositorySuggestions = new();
    private readonly ObservableCollection<string> _revisionSuggestions = new();
    private readonly ObservableCollection<string> _destinationOptions = new();
    private readonly Dictionary<string, int> _folderAnimationVersions = new(StringComparer.OrdinalIgnoreCase);
    private HubRepositorySnapshot? _snapshot;
    private CancellationTokenSource? _downloadCancellation;
    private bool _downloadCompleted;
    private int _visibleReflowVersion;

    public HubDownloadWindow()
    {
        InitializeComponent();
        RepositoryTreeView.ItemsSource = _visibleNodes;
        RepositoryTextBox.ItemsSource = _repositorySuggestions;
        RevisionTextBox.ItemsSource = _revisionSuggestions;
        DestinationComboBox.ItemsSource = _destinationOptions;
        SyncSuggestions(_repositorySuggestions, RepositoryHistory);
        SyncSuggestions(_revisionSuggestions, RevisionHistory);
        DestinationComboBox.IsEnabled = false;

        PropertyChanged += (_, e) =>
        {
            if (e.Property != WindowStateProperty) return;
            var isMaximized = WindowState == WindowState.Maximized;
            OuterBorder.CornerRadius = isMaximized ? new CornerRadius(0) : new CornerRadius(8);
            OuterBorder.BorderThickness = isMaximized ? new Thickness(0) : new Thickness(1);
            MaximizeIcon.IsVisible = !isMaximized;
            RestoreIcon.IsVisible = isMaximized;
        };
    }

    public HubDownloadWindow(
        Func<string, string, string, Task<HubRepositorySnapshot>> browseAsync,
        Func<HubDownloadRequest, IProgress<HubDownloadProgress>, CancellationToken, Task<HubDownloadResult>> downloadAsync,
        IReadOnlyList<string> destinationOptions)
        : this()
    {
        _browseAsync = browseAsync;
        _downloadAsync = downloadAsync;
        foreach (var destination in destinationOptions)
        {
            AddSuggestion(_destinationOptions, destination);
        }
    }

    private async void OpenRepository_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_browseAsync is null) return;
        var repo = (RepositoryTextBox.Text ?? string.Empty).Trim();
        if (repo.Length == 0)
        {
            ErrorText.Text = "请输入模型仓库地址或仓库 ID。";
            RepositoryTextBox.Focus();
            return;
        }

        SetBrowseBusy(true);
        ErrorText.Text = string.Empty;
        RepositorySummaryText.Text = "正在读取仓库目录…";
        try
        {
            var provider = (ProviderComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "modelscope";
            _snapshot = await _browseAsync(provider, repo, (RevisionTextBox.Text ?? string.Empty).Trim());
            RepositoryTextBox.Text = _snapshot.RepoId;
            RevisionTextBox.Text = _snapshot.Revision;
            RememberSuggestion(RepositoryHistory, _repositorySuggestions, _snapshot.RepoId);
            RememberSuggestion(RevisionHistory, _revisionSuggestions, _snapshot.Revision);
            AddSuggestion(_destinationOptions, _snapshot.DefaultDestination, insertFirst: true);
            DestinationComboBox.SelectedItem = _snapshot.DefaultDestination;
            DestinationComboBox.IsEnabled = true;
            BuildTree(_snapshot.Files);
            RepositorySummaryText.Text = $"{_snapshot.RepoId} · {_snapshot.Files.Count} 个文件 · {FormatBytes(_snapshot.TotalBytes)}";
            DownloadStatusText.Text = "请选择文件";
        }
        catch (Exception ex)
        {
            _snapshot = null;
            _roots.Clear();
            _visibleNodes.Clear();
            _folderAnimationVersions.Clear();
            ++_visibleReflowVersion;
            RepositorySummaryText.Text = "仓库打开失败";
            ErrorText.Text = FriendlyError(ex);
        }
        finally
        {
            SetBrowseBusy(false);
        }
    }

    private async void Download_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_downloadCompleted)
        {
            Close(true);
            return;
        }
        if (_snapshot is null || _downloadAsync is null)
        {
            ErrorText.Text = "请先打开模型仓库。";
            return;
        }

        var paths = _roots.SelectMany(root => root.EnumerateFiles())
            .Where(file => file.IsSelected)
            .Select(file => file.Path)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (paths.Count == 0)
        {
            ErrorText.Text = "请至少选择一个文件。";
            return;
        }

        var destination = (DestinationComboBox.SelectedItem as string ?? string.Empty).Trim();
        if (destination.Length == 0)
        {
            ErrorText.Text = "请选择服务器保存位置。";
            DestinationComboBox.Focus();
            return;
        }

        ErrorText.Text = string.Empty;
        SetDownloadBusy(true);
        _downloadCancellation = new CancellationTokenSource();
        var progress = new Progress<HubDownloadProgress>(UpdateDownloadProgress);
        try
        {
            var result = await _downloadAsync(
                new HubDownloadRequest(
                    _snapshot.Provider,
                    _snapshot.RepoId,
                    _snapshot.Revision,
                    destination,
                    paths),
                progress,
                _downloadCancellation.Token);
            DownloadProgressBar.Value = 100;
            DownloadStatusText.Text = $"完成 · {result.Files.Count} 个文件";
            DownloadButton.Content = "完成";
            CancelButton.IsVisible = false;
            _downloadCompleted = true;
        }
        catch (OperationCanceledException)
        {
            DownloadStatusText.Text = "下载已取消";
        }
        catch (Exception ex)
        {
            DownloadStatusText.Text = "下载失败";
            ErrorText.Text = FriendlyError(ex);
        }
        finally
        {
            _downloadCancellation?.Dispose();
            _downloadCancellation = null;
            if (!_downloadCompleted) SetDownloadBusy(false);
        }
    }

    private void Cancel_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_downloadCancellation is not null)
        {
            CancelButton.IsEnabled = false;
            DownloadStatusText.Text = "正在取消…";
            _downloadCancellation.Cancel();
            return;
        }
        Close(false);
    }

    private void SelectAll_OnClick(object? sender, RoutedEventArgs e)
    {
        foreach (var root in _roots) root.IsSelected = true;
    }

    private void ClearSelection_OnClick(object? sender, RoutedEventArgs e)
    {
        foreach (var root in _roots) root.IsSelected = false;
    }

    private void RepositoryNode_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
            || (sender as Control)?.DataContext is not HubRepositoryNode node)
        {
            return;
        }

        node.IsSelected = !node.IsSelected;
        e.Handled = true;
    }

    private async void RepositoryExpander_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
            || (sender as Control)?.DataContext is not HubRepositoryNode node
            || !node.CanExpand)
        {
            return;
        }

        e.Handled = true;
        var previousLayout = CaptureRepositoryFileLayout();
        var animationVersion = _folderAnimationVersions.TryGetValue(node.Path, out var currentVersion)
            ? currentVersion + 1
            : 1;
        _folderAnimationVersions[node.Path] = animationVersion;

        if (node.IsExpanded)
        {
            node.IsExpanded = false;
            foreach (var descendant in _visibleNodes.Where(item => IsPathDescendant(item.Path, node.Path)))
            {
                descendant.TreeOpacity = 0.0;
                descendant.TreeOffsetY = -5.0;
            }

            // Keep the rows alive until their opacity/offset transition has finished.
            // This gives collapsing the same visual language as expanding.
            ++_visibleReflowVersion;
            await Task.Delay(TreeAnimationDurationMs);
            if (_folderAnimationVersions.TryGetValue(node.Path, out var latestVersion)
                && latestVersion == animationVersion
                && !node.IsExpanded)
            {
                RefreshVisibleNodes(previousLayout: previousLayout);
            }
        }
        else
        {
            node.IsExpanded = true;
            RefreshVisibleNodes(node.Path, previousLayout);
        }
    }

    private void UpdateDownloadProgress(HubDownloadProgress progress)
    {
        DownloadProgressBar.IsIndeterminate = string.Equals(progress.State, "downloading", StringComparison.Ordinal);
        var percent = progress.TotalBytes > 0
            ? progress.CompletedBytes * 100d / progress.TotalBytes
            : progress.FileCount > 0 ? (progress.FileIndex - (progress.State == "completed" ? 0 : 1)) * 100d / progress.FileCount : 0;
        DownloadProgressBar.Value = Math.Clamp(percent, 0, 100);
        var name = string.IsNullOrWhiteSpace(progress.Path) ? "文件" : System.IO.Path.GetFileName(progress.Path);
        DownloadStatusText.Text = progress.State == "completed"
            ? $"{progress.FileIndex}/{progress.FileCount} · 已完成 {name}"
            : $"{progress.FileIndex}/{progress.FileCount} · 正在下载 {name}";
    }

    private void BuildTree(IReadOnlyList<HubRepositoryFile> files)
    {
        _roots.Clear();
        _visibleNodes.Clear();
        _folderAnimationVersions.Clear();
        ++_visibleReflowVersion;

        foreach (var file in files)
        {
            var parts = file.Path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) continue;
            HubRepositoryNode? parent = null;
            var currentPath = string.Empty;
            for (var index = 0; index < parts.Length; index++)
            {
                var isFile = index == parts.Length - 1;
                currentPath = currentPath.Length == 0 ? parts[index] : $"{currentPath}/{parts[index]}";
                var collection = parent?.Children ?? _roots;
                var node = collection.FirstOrDefault(item => string.Equals(item.Name, parts[index], StringComparison.Ordinal));
                if (node is null)
                {
                    node = new HubRepositoryNode
                    {
                        Name = parts[index],
                        Path = currentPath,
                        IsFolder = !isFile,
                        Size = isFile ? file.Size : 0,
                        Parent = parent,
                    };
                    collection.Add(node);
                }
                if (!isFile) node.Size += file.Size;
                parent = node;
            }
        }
        SortNodes(_roots);
        RefreshVisibleNodes();
    }

    private void RefreshVisibleNodes(
        string? animatedFolderPath = null,
        RepositoryFileLayoutSnapshot? previousLayout = null)
    {
        var reflowVersion = ++_visibleReflowVersion;
        var desired = new List<HubRepositoryNode>();

        void AppendLevel(IEnumerable<HubRepositoryNode> nodes, int depth)
        {
            foreach (var node in nodes)
            {
                node.TreeIndent = depth * TreeIndentSize;
                node.TreeOpacity = 1.0;
                node.TreeOffsetY = 0.0;
                desired.Add(node);
                if (node.IsExpanded)
                {
                    AppendLevel(node.Children, depth + 1);
                }
            }
        }

        AppendLevel(_roots, 0);

        var enteringItems = string.IsNullOrWhiteSpace(animatedFolderPath)
            ? []
            : desired.Where(item => IsPathDescendant(item.Path, animatedFolderPath)).ToList();
        foreach (var item in enteringItems)
        {
            item.TreeOpacity = 0.0;
            item.TreeOffsetY = -5.0;
        }

        for (var index = 0; index < desired.Count; index++)
        {
            var desiredItem = desired[index];
            if (index < _visibleNodes.Count && ReferenceEquals(_visibleNodes[index], desiredItem))
            {
                continue;
            }

            var existingIndex = -1;
            for (var candidate = index; candidate < _visibleNodes.Count; candidate++)
            {
                if (ReferenceEquals(_visibleNodes[candidate], desiredItem))
                {
                    existingIndex = candidate;
                    break;
                }
            }

            if (existingIndex >= 0)
            {
                _visibleNodes.Move(existingIndex, index);
            }
            else
            {
                _visibleNodes.Insert(index, desiredItem);
            }
        }
        while (_visibleNodes.Count > desired.Count)
        {
            _visibleNodes.RemoveAt(_visibleNodes.Count - 1);
        }

        if (previousLayout is not null)
        {
            Dispatcher.UIThread.Post(
                () => AnimateRepositoryFileReflow(previousLayout, reflowVersion),
                DispatcherPriority.Loaded);
        }

        if (enteringItems.Count > 0)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (reflowVersion != _visibleReflowVersion) return;
                foreach (var item in enteringItems)
                {
                    item.TreeOpacity = 1.0;
                    item.TreeOffsetY = 0.0;
                }
            }, DispatcherPriority.Loaded);
        }
    }

    private RepositoryFileLayoutSnapshot CaptureRepositoryFileLayout()
    {
        var snapshot = new RepositoryFileLayoutSnapshot();
        if (FindRepositoryScrollViewer() is { } scrollViewer)
        {
            snapshot.ScrollOffset = scrollViewer.Offset;
        }

        var realizedItems = new List<(int Index, double Y, double Height)>();
        for (var index = 0; index < _visibleNodes.Count; index++)
        {
            var item = _visibleNodes[index];
            snapshot.ItemIndices[item] = index;
            if (RepositoryTreeView.ContainerFromItem(item) is not Control container)
            {
                continue;
            }

            if (container.TranslatePoint(new Point(0, 0), RepositoryTreeView) is { } position)
            {
                snapshot.VisiblePositions[item] = position.Y;
                realizedItems.Add((index, position.Y, container.Bounds.Height));
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

    private ScrollViewer? FindRepositoryScrollViewer() => RepositoryTreeView
        .GetVisualDescendants()
        .OfType<ScrollViewer>()
        .FirstOrDefault();

    private void RestoreRepositoryFileScrollAnchor(RepositoryFileLayoutSnapshot previousLayout)
    {
        var scrollViewer = FindRepositoryScrollViewer();
        if (scrollViewer is null || previousLayout.ScrollOffset is not { } previousOffset)
        {
            return;
        }

        var anchor = previousLayout.VisiblePositions
            .Where(entry => _visibleNodes.IndexOf(entry.Key) >= 0)
            .OrderBy(entry => Math.Abs(entry.Value))
            .FirstOrDefault();

        var targetY = previousOffset.Y;
        if (anchor.Key is not null
            && previousLayout.ItemIndices.TryGetValue(anchor.Key, out var oldIndex))
        {
            var newIndex = _visibleNodes.IndexOf(anchor.Key);
            if (RepositoryTreeView.ContainerFromItem(anchor.Key) is Control container
                && container.TranslatePoint(new Point(0, 0), RepositoryTreeView) is { } newPosition)
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

    private void AnimateRepositoryFileReflow(RepositoryFileLayoutSnapshot previousLayout, int reflowVersion)
    {
        if (reflowVersion != _visibleReflowVersion) return;

        RestoreRepositoryFileScrollAnchor(previousLayout);
        Dispatcher.UIThread.Post(
            () => AnimateRepositoryFileReflowCore(previousLayout, reflowVersion),
            DispatcherPriority.Render);
    }

    private void AnimateRepositoryFileReflowCore(RepositoryFileLayoutSnapshot previousLayout, int reflowVersion)
    {
        if (reflowVersion != _visibleReflowVersion) return;

        RestoreRepositoryFileScrollAnchor(previousLayout);
        var containers = new List<(int NewIndex, Control Container)>();
        for (var newIndex = 0; newIndex < _visibleNodes.Count; newIndex++)
        {
            var item = _visibleNodes[newIndex];
            if (RepositoryTreeView.ContainerFromItem(item) is not Control container)
            {
                continue;
            }

            container.RenderTransform = null;
            containers.Add((newIndex, container));
        }

        foreach (var (newIndex, container) in containers)
        {
            var item = _visibleNodes[newIndex];
            if (container.TranslatePoint(new Point(0, 0), RepositoryTreeView) is not { } position)
            {
                continue;
            }

            double offsetY;
            if (previousLayout.VisiblePositions.TryGetValue(item, out var oldY))
            {
                offsetY = oldY - position.Y;
            }
            else if (previousLayout.ItemStride > 0.0
                && previousLayout.ItemIndices.TryGetValue(item, out var oldIndex))
            {
                offsetY = (oldIndex - newIndex) * previousLayout.ItemStride;
            }
            else
            {
                continue;
            }

            if (Math.Abs(offsetY) < 0.5) continue;

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
            Dispatcher.UIThread.Post(() => transform.Y = 0.0, DispatcherPriority.Loaded);
        }
    }

    private void SetBrowseBusy(bool busy)
    {
        OpenRepositoryButton.IsEnabled = !busy && _downloadCancellation is null;
        ProviderComboBox.IsEnabled = !busy && _downloadCancellation is null;
        RepositoryTextBox.IsEnabled = !busy && _downloadCancellation is null;
        RevisionTextBox.IsEnabled = !busy && _downloadCancellation is null;
    }

    private void SetDownloadBusy(bool busy)
    {
        DownloadButton.IsEnabled = !busy;
        CancelButton.IsEnabled = true;
        DestinationComboBox.IsEnabled = !busy && _snapshot is not null;
        RepositoryTreeView.IsEnabled = !busy;
        SetBrowseBusy(busy);
    }

    private static void SyncSuggestions(ObservableCollection<string> target, IEnumerable<string> source)
    {
        foreach (var value in source)
        {
            AddSuggestion(target, value);
        }
    }

    private static void RememberSuggestion(
        List<string> history,
        ObservableCollection<string> target,
        string value)
    {
        var normalized = value.Trim();
        if (normalized.Length == 0) return;
        history.RemoveAll(item => string.Equals(item, normalized, StringComparison.OrdinalIgnoreCase));
        history.Insert(0, normalized);
        AddSuggestion(target, normalized, insertFirst: true);
    }

    private static void AddSuggestion(
        ObservableCollection<string> target,
        string value,
        bool insertFirst = false)
    {
        var normalized = value.Trim().Replace('\\', '/').Trim('/');
        if (normalized.Length == 0
            || target.Any(item => string.Equals(item, normalized, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        if (insertFirst) target.Insert(0, normalized);
        else target.Add(normalized);
    }

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    private void MinimizeBtn_Click(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaximizeBtn_Click(object? sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
        MaximizeIcon.IsVisible = WindowState != WindowState.Maximized;
        RestoreIcon.IsVisible = WindowState == WindowState.Maximized;
    }

    private void CloseButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_downloadCancellation is not null)
        {
            Cancel_OnClick(sender, e);
            return;
        }
        Close(false);
    }

    private void Window_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        if (_downloadCancellation is not null)
        {
            Cancel_OnClick(sender, e);
        }
        else
        {
            Close(false);
        }
    }

    private static void SortNodes(ObservableCollection<HubRepositoryNode> nodes)
    {
        var sorted = nodes.OrderByDescending(node => node.IsFolder)
            .ThenBy(node => node.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        nodes.Clear();
        foreach (var node in sorted)
        {
            SortNodes(node.Children);
            nodes.Add(node);
        }
    }

    private static bool IsPathDescendant(string candidate, string folderPath) =>
        candidate.StartsWith(folderPath + "/", StringComparison.OrdinalIgnoreCase);

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024L * 1024) return $"{bytes / 1024d:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024d * 1024):F2} MB";
        return $"{bytes / (1024d * 1024 * 1024):F2} GB";
    }

    private static string FriendlyError(Exception ex) =>
        ex is TimeoutException ? "服务器响应超时，请检查网络后重试。" : ex.Message;

    private sealed class RepositoryFileLayoutSnapshot
    {
        public Dictionary<HubRepositoryNode, double> VisiblePositions { get; } = new();
        public Dictionary<HubRepositoryNode, int> ItemIndices { get; } = new();
        public double ItemStride { get; set; }
        public Vector? ScrollOffset { get; set; }
    }
}
