using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ClientAvalonia.Services;

public sealed record LocalServerEnvironmentCheckResult(
    bool Success,
    string Summary,
    string Details);

public sealed record LocalServerSourceUpdateCheckResult(
    bool Success,
    bool UpdateAvailable,
    string Summary,
    string Details,
    string LocalCommit,
    string RemoteCommit);

public static class LocalServerEnvironmentChecker
{
    public const string ServerRepositoryUrl = "https://github.com/wen999di/RVC_Streaming_Server";
    private const string ServerRepositoryBranch = "main";
    private const string ServerArchiveUrl = ServerRepositoryUrl + "/archive/refs/heads/" + ServerRepositoryBranch + ".zip";
    private const string ServerCommitApiUrl = "https://api.github.com/repos/wen999di/RVC_Streaming_Server/commits/" + ServerRepositoryBranch;
    private const string SourceMetadataDirectoryName = "source-versions";
    private const int MaxDetailsLength = 50_000;
    private const string CheckScript =
        "import importlib; " +
        "mods=['numpy','scipy','librosa','websockets','torch','torchaudio','torchfcpe','faiss','huggingface_hub','modelscope_hub']; " +
        "[importlib.import_module(m) for m in mods]; " +
        "print('RVC_LOCAL_ENV_OK')";
    private static readonly HttpClient DownloadClient = CreateDownloadClient();

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
    private sealed record DownloadedSourceSnapshot(string TemporaryRoot, string SourceDirectory, string Commit);
    private sealed class SourceUpdateTransaction
    {
        public List<string> BackedUpFiles { get; } = new();
        public List<string> CreatedFiles { get; } = new();
    }
    private sealed class SourceMetadata
    {
        public int Version { get; set; } = 1;
        public string Repository { get; set; } = ServerRepositoryUrl;
        public string Branch { get; set; } = ServerRepositoryBranch;
        public string Commit { get; set; } = string.Empty;
        public string ServerDirectory { get; set; } = string.Empty;
        public List<string> Files { get; set; } = new();
    }

    public static bool HasServerLayout(string? serverDirectory)
    {
        if (string.IsNullOrWhiteSpace(serverDirectory)) return false;
        try
        {
            var fullPath = NormalizeDirectory(serverDirectory);
            return Directory.Exists(fullPath)
                && File.Exists(Path.Combine(fullPath, "server.py"))
                && File.Exists(Path.Combine(fullPath, "pixi.toml"));
        }
        catch
        {
            return false;
        }
    }

    public static bool CanDownloadServer(string? serverDirectory)
    {
        if (string.IsNullOrWhiteSpace(serverDirectory)) return false;
        try
        {
            var fullPath = NormalizeDirectory(serverDirectory);
            if (Directory.GetParent(fullPath) is null) return false;
            return !Directory.Exists(fullPath)
                || !Directory.EnumerateFileSystemEntries(fullPath).Any();
        }
        catch
        {
            return false;
        }
    }

    public static bool HasInstalledEnvironment(string? serverDirectory)
    {
        if (!HasServerLayout(serverDirectory)) return false;
        var fullPath = NormalizeDirectory(serverDirectory!);
        var python = OperatingSystem.IsWindows()
            ? Path.Combine(fullPath, ".pixi", "envs", "default", "python.exe")
            : Path.Combine(fullPath, ".pixi", "envs", "default", "bin", "python");
        return File.Exists(python);
    }

    public static async Task<LocalServerEnvironmentCheckResult> DownloadServerAsync(
        string serverDirectory,
        IProgress<string>? output = null,
        CancellationToken cancellationToken = default)
    {
        if (!CanDownloadServer(serverDirectory))
        {
            return new LocalServerEnvironmentCheckResult(
                false,
                "目标目录不是空目录",
                "请选择空目录，或使用尚未创建的默认 localServer 目录。为避免覆盖本地文件，下载不会合并到非空目录。"
            );
        }

        var targetDirectory = NormalizeDirectory(serverDirectory);
        var parentDirectory = Directory.GetParent(targetDirectory)?.FullName;
        if (string.IsNullOrWhiteSpace(parentDirectory))
        {
            return new LocalServerEnvironmentCheckResult(false, "目标路径无效", targetDirectory);
        }

        var operationId = Guid.NewGuid().ToString("N");
        var stagingDirectory = Path.Combine(
            parentDirectory,
            "." + Path.GetFileName(targetDirectory) + ".download-" + operationId
        );
        DownloadedSourceSnapshot? snapshot = null;

        try
        {
            Directory.CreateDirectory(parentDirectory);
            snapshot = await DownloadSourceSnapshotAsync(output, cancellationToken);
            CopyDirectory(snapshot.SourceDirectory, stagingDirectory, cancellationToken, output);
            if (Directory.Exists(Path.Combine(stagingDirectory, ".git")))
            {
                throw new InvalidDataException("下载结果意外包含 .git 目录。");
            }
            if (!HasServerLayout(stagingDirectory))
            {
                throw new InvalidDataException("解压后的 Server 目录结构无效。");
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (Directory.Exists(targetDirectory))
            {
                // A concurrent write makes this fail instead of deleting files
                // that appeared after the initial empty-directory check.
                Directory.Delete(targetDirectory, recursive: false);
            }
            Directory.Move(stagingDirectory, targetDirectory);
            WriteSourceMetadata(
                targetDirectory,
                snapshot.Commit,
                GetSnapshotFiles(snapshot.SourceDirectory)
            );
            output?.Report($"> Server 源码已安装到 {targetDirectory}（{ShortCommit(snapshot.Commit)}）");
            return new LocalServerEnvironmentCheckResult(
                true,
                "Server 源码下载完成",
                "源码快照不包含 Git 历史。下一步请下载依赖。"
            );
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            output?.Report($"> 下载失败：{ex.Message}");
            return new LocalServerEnvironmentCheckResult(false, "Server 下载失败", ex.Message);
        }
        finally
        {
            if (snapshot is not null) TryDeleteDirectory(snapshot.TemporaryRoot);
            TryDeleteDirectory(stagingDirectory);
        }
    }

    public static async Task<LocalServerSourceUpdateCheckResult> CheckForUpdatesAsync(
        string serverDirectory,
        IProgress<string>? output = null,
        CancellationToken cancellationToken = default)
    {
        if (!HasServerLayout(serverDirectory))
        {
            return new LocalServerSourceUpdateCheckResult(
                false,
                false,
                "无法检查更新",
                "Server 路径无效。",
                string.Empty,
                string.Empty
            );
        }

        var fullPath = NormalizeDirectory(serverDirectory);
        DownloadedSourceSnapshot? snapshot = null;
        try
        {
            output?.Report($"> 正在检查 {ServerRepositoryBranch} 分支更新...");
            var remoteCommit = await GetRemoteCommitAsync(cancellationToken);
            var metadata = TryReadSourceMetadata(fullPath);
            if (metadata is not null)
            {
                var updateAvailable = !string.Equals(
                    metadata.Commit,
                    remoteCommit,
                    StringComparison.OrdinalIgnoreCase
                );
                output?.Report($"> 本地版本 {ShortCommit(metadata.Commit)}，远端版本 {ShortCommit(remoteCommit)}");
                output?.Report(updateAvailable ? "> 发现可用更新" : "> 当前已是最新版本");
                return new LocalServerSourceUpdateCheckResult(
                    true,
                    updateAvailable,
                    updateAvailable ? "发现 Server 源码更新" : "Server 源码已是最新",
                    string.Empty,
                    metadata.Commit,
                    remoteCommit
                );
            }

            output?.Report("> 本地源码没有版本记录，正在进行一次内容比对...");
            snapshot = await DownloadSourceSnapshotAsync(output, cancellationToken, remoteCommit);
            var matches = await Task.Run(
                () => SnapshotMatches(snapshot.SourceDirectory, fullPath, cancellationToken),
                cancellationToken
            );
            if (matches)
            {
                WriteSourceMetadata(
                    fullPath,
                    remoteCommit,
                    GetSnapshotFiles(snapshot.SourceDirectory)
                );
                output?.Report($"> 内容与远端 {ShortCommit(remoteCommit)} 一致，已补充版本记录");
            }
            else
            {
                output?.Report("> 本地源码与远端版本不同，发现可用更新");
            }

            return new LocalServerSourceUpdateCheckResult(
                true,
                !matches,
                matches ? "Server 源码已是最新" : "发现 Server 源码更新",
                string.Empty,
                matches ? remoteCommit : string.Empty,
                remoteCommit
            );
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            output?.Report($"> 检查更新失败：{ex.Message}");
            return new LocalServerSourceUpdateCheckResult(
                false,
                false,
                "检查更新失败",
                ex.Message,
                string.Empty,
                string.Empty
            );
        }
        finally
        {
            if (snapshot is not null) TryDeleteDirectory(snapshot.TemporaryRoot);
        }
    }

    public static async Task<LocalServerEnvironmentCheckResult> UpdateServerAsync(
        string serverDirectory,
        IProgress<string>? output = null,
        CancellationToken cancellationToken = default)
    {
        if (!HasServerLayout(serverDirectory)) return InvalidServerLayout();

        var targetDirectory = NormalizeDirectory(serverDirectory);
        var parentDirectory = Directory.GetParent(targetDirectory)?.FullName;
        if (string.IsNullOrWhiteSpace(parentDirectory))
        {
            return new LocalServerEnvironmentCheckResult(false, "目标路径无效", targetDirectory);
        }

        var operationId = Guid.NewGuid().ToString("N");
        var backupDirectory = Path.Combine(
            AppPaths.LocalServerDataDirectory,
            "temp",
            "source-update-backup-" + operationId
        );
        DownloadedSourceSnapshot? snapshot = null;
        SourceUpdateTransaction? transaction = null;

        try
        {
            snapshot = await DownloadSourceSnapshotAsync(output, cancellationToken);
            if (!HasServerLayout(snapshot.SourceDirectory))
            {
                throw new InvalidDataException("更新后的 Server 目录结构无效。");
            }

            cancellationToken.ThrowIfCancellationRequested();
            output?.Report("> 正在更新源码文件；Pixi 环境和本地数据将保留...");
            var sourceFiles = GetSnapshotFiles(snapshot.SourceDirectory);
            var previousFiles = TryReadSourceMetadata(targetDirectory)?.Files ?? new List<string>();
            transaction = new SourceUpdateTransaction();
            ApplySourceUpdate(
                snapshot.SourceDirectory,
                targetDirectory,
                backupDirectory,
                previousFiles,
                sourceFiles,
                transaction,
                cancellationToken
            );
            WriteSourceMetadata(targetDirectory, snapshot.Commit, sourceFiles);
            TryDeleteDirectory(backupDirectory);
            output?.Report($"> Server 源码已更新到 {ShortCommit(snapshot.Commit)}");
            return new LocalServerEnvironmentCheckResult(
                true,
                "Server 源码更新完成",
                "Pixi 环境已保留，请重新下载并检查依赖。"
            );
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (transaction is not null)
            {
                RollBackSourceUpdate(targetDirectory, backupDirectory, transaction);
            }
            output?.Report($"> 更新失败：{ex.Message}");
            return new LocalServerEnvironmentCheckResult(false, "Server 源码更新失败", ex.Message);
        }
        finally
        {
            if (snapshot is not null) TryDeleteDirectory(snapshot.TemporaryRoot);
            TryDeleteDirectory(backupDirectory);
        }
    }

    public static async Task<LocalServerEnvironmentCheckResult> InstallDependenciesAsync(
        string serverDirectory,
        IProgress<string>? output = null,
        CancellationToken cancellationToken = default)
    {
        if (!HasServerLayout(serverDirectory))
        {
            return InvalidServerLayout();
        }
        var fullPath = NormalizeDirectory(serverDirectory);
        var startInfo = CreatePixiStartInfo(fullPath);
        startInfo.ArgumentList.Add("install");
        startInfo.ArgumentList.Add("--manifest-path");
        startInfo.ArgumentList.Add(Path.Combine(fullPath, "pixi.toml"));
        startInfo.ArgumentList.Add("--environment");
        startInfo.ArgumentList.Add("default");
        startInfo.ArgumentList.Add("--locked");
        startInfo.ArgumentList.Add("-vv");
        output?.Report("> pixi install --environment default --locked -vv");

        try
        {
            var result = await RunProcessAsync(startInfo, output, cancellationToken);
            var details = JoinOutput(result.StandardOutput, result.StandardError);
            var success = result.ExitCode == 0 && HasInstalledEnvironment(fullPath);
            return success
                ? new LocalServerEnvironmentCheckResult(true, "依赖下载完成", details)
                : new LocalServerEnvironmentCheckResult(false, "依赖下载失败", details);
        }
        catch (Win32Exception)
        {
            return PixiNotFound();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new LocalServerEnvironmentCheckResult(false, "依赖下载失败", ex.Message);
        }
    }

    public static async Task<LocalServerEnvironmentCheckResult> CheckAsync(
        string serverDirectory,
        CancellationToken cancellationToken = default,
        IProgress<string>? output = null)
    {
        if (!HasServerLayout(serverDirectory))
        {
            return InvalidServerLayout();
        }
        if (!HasInstalledEnvironment(serverDirectory))
        {
            output?.Report("> 尚未检测到 Pixi default 环境，请先下载依赖");
            return new LocalServerEnvironmentCheckResult(
                false,
                "依赖尚未下载",
                "请先点击“下载依赖”。"
            );
        }

        var fullPath = NormalizeDirectory(serverDirectory);
        var startInfo = CreatePixiStartInfo(fullPath);
        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add("--executable");
        startInfo.ArgumentList.Add("--manifest-path");
        startInfo.ArgumentList.Add(Path.Combine(fullPath, "pixi.toml"));
        startInfo.ArgumentList.Add("--environment");
        startInfo.ArgumentList.Add("default");
        startInfo.ArgumentList.Add("--no-install");
        startInfo.ArgumentList.Add("--frozen");
        startInfo.ArgumentList.Add("python");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(CheckScript);
        output?.Report("> pixi run --environment default --no-install --frozen python -c <依赖检查>");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromMinutes(2));
        try
        {
            var result = await RunProcessAsync(startInfo, output, timeoutCts.Token);
            var details = JoinOutput(result.StandardOutput, result.StandardError);
            var success = result.ExitCode == 0
                && result.StandardOutput.Contains("RVC_LOCAL_ENV_OK", StringComparison.Ordinal);
            return success
                ? new LocalServerEnvironmentCheckResult(true, "本地环境已就绪", details)
                : new LocalServerEnvironmentCheckResult(false, "依赖检查未通过", details);
        }
        catch (Win32Exception)
        {
            return PixiNotFound();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new LocalServerEnvironmentCheckResult(false, "依赖检查超时", "Pixi 在 2 分钟内没有完成检查。");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new LocalServerEnvironmentCheckResult(false, "依赖检查失败", ex.Message);
        }
    }

    private static ProcessStartInfo CreatePixiStartInfo(string workingDirectory)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "pixi",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        AppPaths.ConfigureLocalServerProcess(startInfo);
        startInfo.Environment["PIXI_COLOR"] = "never";
        startInfo.Environment["PIXI_NO_PROGRESS"] = "true";
        return startInfo;
    }

    private static async Task<ProcessResult> RunProcessAsync(
        ProcessStartInfo startInfo,
        IProgress<string>? output,
        CancellationToken cancellationToken)
    {
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Process.Start 未返回进程实例。");
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var stdoutTask = PumpOutputAsync(process.StandardOutput, stdout, output);
        var stderrTask = PumpOutputAsync(process.StandardError, stderr, output);
        using var cancellationRegistration = cancellationToken.Register(() => TryKill(process));
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            return new ProcessResult(process.ExitCode, stdout.ToString(), stderr.ToString());
        }
        catch
        {
            TryKill(process);
            try
            {
                await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch
            {
            }
            await IgnoreOutputTaskAsync(stdoutTask);
            await IgnoreOutputTaskAsync(stderrTask);
            throw;
        }
    }

    private static async Task PumpOutputAsync(
        StreamReader reader,
        StringBuilder destination,
        IProgress<string>? output)
    {
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            lock (destination)
            {
                destination.AppendLine(line);
            }
            output?.Report(line);
        }
    }

    private static async Task<DownloadedSourceSnapshot> DownloadSourceSnapshotAsync(
        IProgress<string>? output,
        CancellationToken cancellationToken,
        string? knownCommit = null)
    {
        var operationId = Guid.NewGuid().ToString("N");
        var downloadRoot = Path.Combine(AppPaths.LocalServerDataDirectory, "temp", "github-" + operationId);
        var archivePath = Path.Combine(downloadRoot, "server.zip");
        var extractionDirectory = Path.Combine(downloadRoot, "extracted");
        try
        {
            var commit = knownCommit ?? await GetRemoteCommitAsync(cancellationToken);
            Directory.CreateDirectory(downloadRoot);
            Directory.CreateDirectory(extractionDirectory);
            output?.Report($"> 从 {ServerRepositoryUrl} 下载 {ServerRepositoryBranch} 源码快照");
            output?.Report($"> 远端版本 {ShortCommit(commit)}；下载内容不包含 .git 或 Git 历史");

            using var request = new HttpRequestMessage(HttpMethod.Get, ServerArchiveUrl);
            using var response = await DownloadClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken
            ).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength;
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var destination = new FileStream(
                archivePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                81_920,
                useAsync: true))
            {
                var buffer = new byte[81_920];
                long receivedBytes = 0;
                var lastPercent = -10;
                long nextByteReport = 2 * 1024 * 1024;
                while (true)
                {
                    var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (read == 0) break;
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    receivedBytes += read;

                    if (totalBytes is > 0)
                    {
                        var percent = (int)(receivedBytes * 100 / totalBytes.Value);
                        if (percent >= lastPercent + 10 || percent == 100)
                        {
                            lastPercent = percent;
                            output?.Report($"> 已下载 {FormatBytes(receivedBytes)} / {FormatBytes(totalBytes.Value)} ({percent}%)");
                        }
                    }
                    else if (receivedBytes >= nextByteReport)
                    {
                        output?.Report($"> 已下载 {FormatBytes(receivedBytes)}");
                        nextByteReport += 2 * 1024 * 1024;
                    }
                }
                output?.Report($"> 下载完成，共 {FormatBytes(receivedBytes)}");
            }

            cancellationToken.ThrowIfCancellationRequested();
            output?.Report("> 正在解压并校验 Server 文件...");
            ZipFile.ExtractToDirectory(archivePath, extractionDirectory);
            var extractedRoot = Directory.EnumerateDirectories(extractionDirectory)
                .FirstOrDefault(HasServerLayout);
            if (extractedRoot is null)
            {
                throw new InvalidDataException("GitHub 下载包中未找到 server.py 和 pixi.toml。");
            }
            if (Directory.Exists(Path.Combine(extractedRoot, ".git")))
            {
                throw new InvalidDataException("下载结果意外包含 .git 目录。");
            }
            return new DownloadedSourceSnapshot(downloadRoot, extractedRoot, commit);
        }
        catch
        {
            TryDeleteDirectory(downloadRoot);
            throw;
        }
    }

    private static async Task<string> GetRemoteCommitAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ServerCommitApiUrl);
        using var response = await DownloadClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken
        ).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!document.RootElement.TryGetProperty("sha", out var shaElement))
        {
            throw new InvalidDataException("GitHub 更新响应缺少提交版本。");
        }

        var commit = shaElement.GetString()?.Trim() ?? string.Empty;
        if (commit.Length < 7 || commit.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidDataException("GitHub 返回了无效的提交版本。");
        }
        return commit;
    }

    private static SourceMetadata? TryReadSourceMetadata(string serverDirectory)
    {
        try
        {
            var normalizedDirectory = NormalizeDirectory(serverDirectory);
            var path = GetSourceMetadataPath(normalizedDirectory);
            if (!File.Exists(path)) return null;
            var metadata = JsonSerializer.Deserialize<SourceMetadata>(File.ReadAllText(path));
            if (metadata is null
                || !string.Equals(metadata.Repository, ServerRepositoryUrl, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(metadata.Branch, ServerRepositoryBranch, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(metadata.ServerDirectory, normalizedDirectory, StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(metadata.Commit))
            {
                return null;
            }
            return metadata;
        }
        catch
        {
            return null;
        }
    }

    private static void WriteSourceMetadata(
        string serverDirectory,
        string commit,
        IEnumerable<string> files)
    {
        var normalizedDirectory = NormalizeDirectory(serverDirectory);
        var path = GetSourceMetadataPath(normalizedDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = path + ".tmp";
        var metadata = new SourceMetadata
        {
            Commit = commit,
            ServerDirectory = normalizedDirectory,
            Files = files.OrderBy(path => path, StringComparer.Ordinal).ToList(),
        };
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(metadata, new JsonSerializerOptions
        {
            WriteIndented = true,
        }));
        File.Move(temporaryPath, path, true);
    }

    private static string GetSourceMetadataPath(string serverDirectory)
    {
        var identity = OperatingSystem.IsWindows()
            ? NormalizeDirectory(serverDirectory).ToUpperInvariant()
            : NormalizeDirectory(serverDirectory);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
        return Path.Combine(
            AppPaths.LocalServerDataDirectory,
            SourceMetadataDirectoryName,
            key + ".json"
        );
    }

    private static List<string> GetSnapshotFiles(string snapshotDirectory) =>
        Directory.EnumerateFiles(snapshotDirectory, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(snapshotDirectory, path))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

    private static void ApplySourceUpdate(
        string snapshotDirectory,
        string targetDirectory,
        string backupDirectory,
        IEnumerable<string> previousFiles,
        IReadOnlyCollection<string> sourceFiles,
        SourceUpdateTransaction transaction,
        CancellationToken cancellationToken)
    {
        var newFiles = sourceFiles.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var oldFiles = previousFiles
            .Where(IsSafeRelativePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var affectedFiles = newFiles
            .Concat(oldFiles)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        Directory.CreateDirectory(backupDirectory);
        foreach (var relativePath in affectedFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var targetPath = Path.Combine(targetDirectory, relativePath);
            if (File.Exists(targetPath))
            {
                var backupPath = Path.Combine(backupDirectory, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
                File.Copy(targetPath, backupPath, overwrite: false);
                transaction.BackedUpFiles.Add(relativePath);
            }
            else if (newFiles.Contains(relativePath))
            {
                transaction.CreatedFiles.Add(relativePath);
            }
        }

        foreach (var relativePath in sourceFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourcePath = Path.Combine(snapshotDirectory, relativePath);
            var targetPath = Path.Combine(targetDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            File.Copy(sourcePath, targetPath, overwrite: true);
        }

        foreach (var relativePath in oldFiles.Except(newFiles, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var targetPath = Path.Combine(targetDirectory, relativePath);
            if (File.Exists(targetPath)) File.Delete(targetPath);
        }
    }

    private static void RollBackSourceUpdate(
        string targetDirectory,
        string backupDirectory,
        SourceUpdateTransaction transaction)
    {
        try
        {
            foreach (var relativePath in transaction.CreatedFiles)
            {
                var targetPath = Path.Combine(targetDirectory, relativePath);
                if (File.Exists(targetPath)) File.Delete(targetPath);
            }
            foreach (var relativePath in transaction.BackedUpFiles)
            {
                var backupPath = Path.Combine(backupDirectory, relativePath);
                if (!File.Exists(backupPath)) continue;
                var targetPath = Path.Combine(targetDirectory, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                File.Copy(backupPath, targetPath, overwrite: true);
            }
        }
        catch
        {
        }
    }

    private static bool IsSafeRelativePath(string path) =>
        !string.IsNullOrWhiteSpace(path)
        && !Path.IsPathRooted(path)
        && path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .All(part => part is not "" and not "." and not "..");

    private static bool SnapshotMatches(
        string snapshotDirectory,
        string serverDirectory,
        CancellationToken cancellationToken)
    {
        foreach (var snapshotPath in Directory.EnumerateFiles(snapshotDirectory, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = Path.GetRelativePath(snapshotDirectory, snapshotPath);
            var localPath = Path.Combine(serverDirectory, relativePath);
            if (!File.Exists(localPath)) return false;

            var snapshotInfo = new FileInfo(snapshotPath);
            var localInfo = new FileInfo(localPath);
            if (snapshotInfo.Length != localInfo.Length) return false;
            using var snapshotStream = File.OpenRead(snapshotPath);
            using var localStream = File.OpenRead(localPath);
            if (!SHA256.HashData(snapshotStream).SequenceEqual(SHA256.HashData(localStream))) return false;
        }
        return true;
    }

    private static string ShortCommit(string commit) =>
        commit.Length <= 7 ? commit : commit[..7];

    private static async Task IgnoreOutputTaskAsync(Task task)
    {
        try
        {
            await task;
        }
        catch
        {
        }
    }

    private static void CopyDirectory(
        string sourceDirectory,
        string destinationDirectory,
        CancellationToken cancellationToken,
        IProgress<string>? output)
    {
        var files = Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories).ToList();
        foreach (var directory in Directory.EnumerateDirectories(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = Path.GetRelativePath(sourceDirectory, directory);
            Directory.CreateDirectory(Path.Combine(destinationDirectory, relativePath));
        }
        Directory.CreateDirectory(destinationDirectory);
        for (var index = 0; index < files.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = Path.GetRelativePath(sourceDirectory, files[index]);
            var targetPath = Path.Combine(destinationDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            File.Copy(files[index], targetPath, overwrite: false);
            if ((index + 1) % 25 == 0 || index + 1 == files.Count)
            {
                output?.Report($"> 已解压 {index + 1} / {files.Count} 个文件");
            }
        }
    }

    private static string NormalizeDirectory(string directory) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));

    private static string JoinOutput(string stdout, string stderr)
    {
        var content = string.Join(
            Environment.NewLine,
            new[] { stdout.Trim(), stderr.Trim() }.Where(value => !string.IsNullOrWhiteSpace(value))
        );
        if (string.IsNullOrWhiteSpace(content)) return "Pixi 未返回详细信息。";
        return content.Length <= MaxDetailsLength ? content : content[^MaxDetailsLength..];
    }

    private static LocalServerEnvironmentCheckResult InvalidServerLayout() => new(
        false,
        "Server 路径无效",
        "目录中需要包含 server.py 和 pixi.toml。"
    );

    private static LocalServerEnvironmentCheckResult PixiNotFound() => new(
        false,
        "未找到 Pixi",
        "请先安装 Pixi，并确保 pixi 命令已加入 PATH。"
    );

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024L) return $"{bytes / (1024d * 1024d):F1} MB";
        if (bytes >= 1024L) return $"{bytes / 1024d:F1} KB";
        return $"{bytes} B";
    }

    private static HttpClient CreateDownloadClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("RVC-Streaming-Client/1.0");
        return client;
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        catch
        {
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch
        {
        }
    }
}
