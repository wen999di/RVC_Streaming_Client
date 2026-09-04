using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Text;

namespace ClientAvalonia.Services;

public sealed record LocalServerEnvironmentCheckResult(
    bool Success,
    string Summary,
    string Details);

public static class LocalServerEnvironmentChecker
{
    public const string ServerRepositoryUrl = "https://github.com/wen999di/RVC_Streaming_Server";
    private const string ServerRepositoryBranch = "main";
    private const string ServerArchiveUrl = ServerRepositoryUrl + "/archive/refs/heads/" + ServerRepositoryBranch + ".zip";
    private const int MaxDetailsLength = 50_000;
    private const string CheckScript =
        "import importlib; " +
        "mods=['numpy','scipy','librosa','websockets','torch','torchaudio','torchfcpe','faiss','huggingface_hub','modelscope_hub']; " +
        "[importlib.import_module(m) for m in mods]; " +
        "print('RVC_LOCAL_ENV_OK')";
    private static readonly HttpClient DownloadClient = CreateDownloadClient();

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

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
        var downloadRoot = Path.Combine(AppPaths.LocalServerDataDirectory, "temp", "github-" + operationId);
        var archivePath = Path.Combine(downloadRoot, "server.zip");
        var extractionDirectory = Path.Combine(downloadRoot, "extracted");
        var stagingDirectory = Path.Combine(
            parentDirectory,
            "." + Path.GetFileName(targetDirectory) + ".download-" + operationId
        );

        try
        {
            Directory.CreateDirectory(downloadRoot);
            Directory.CreateDirectory(extractionDirectory);
            Directory.CreateDirectory(parentDirectory);
            output?.Report($"> 从 {ServerRepositoryUrl} 下载 {ServerRepositoryBranch} 源码快照");
            output?.Report("> 下载内容不包含 .git 目录或 Git 历史");

            using var request = new HttpRequestMessage(HttpMethod.Get, ServerArchiveUrl);
            using var response = await DownloadClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken
            );
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength;
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
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
                    var read = await source.ReadAsync(buffer, cancellationToken);
                    if (read == 0) break;
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
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

            CopyDirectory(extractedRoot, stagingDirectory, cancellationToken, output);
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
            output?.Report($"> Server 源码已安装到 {targetDirectory}");
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
            TryDeleteDirectory(downloadRoot);
            TryDeleteDirectory(stagingDirectory);
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
        output?.Report("> pixi install --environment default --locked");

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
            await process.WaitForExitAsync(cancellationToken);
            await Task.WhenAll(stdoutTask, stderrTask);
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
        while (await reader.ReadLineAsync() is { } line)
        {
            lock (destination)
            {
                destination.AppendLine(line);
            }
            output?.Report(line);
        }
    }

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
