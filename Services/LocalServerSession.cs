using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;

namespace ClientAvalonia.Services;

internal enum LocalServerChannel : byte
{
    Control = 0,
    Audio = 1,
}

internal sealed class LocalServerSession : IAsyncDisposable
{
    private const int HeaderSize = 10;
    private const int MaxMessageBytes = 2 * 1024 * 1024;
    private const byte TransportChannel = 255;
    private const byte TextKind = 1;
    private const byte BinaryKind = 2;
    private const byte CloseKind = 3;
    private const byte ReadyKind = 4;
    private static readonly byte[] Magic = "RVCP"u8.ToArray();

    private readonly string _serverDirectory;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Process? _process;
    private Task? _readTask;
    private Task? _stderrTask;
    private int _readyState;
    private int _disposing;
    private int _closedRaised;

    public LocalServerSession(string serverDirectory)
    {
        _serverDirectory = Path.GetFullPath(serverDirectory);
    }

    public bool IsOpen =>
        Volatile.Read(ref _readyState) != 0
        && Volatile.Read(ref _disposing) == 0
        && _process is { HasExited: false };

    public event EventHandler<string>? LogReceived;
    public event EventHandler<string>? TextMessageReceived;
    public event EventHandler<byte[]>? BinaryMessageReceived;
    public event EventHandler<string>? Closed;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var serverScript = Path.Combine(_serverDirectory, "server.py");
        var manifest = Path.Combine(_serverDirectory, "pixi.toml");
        if (!Directory.Exists(_serverDirectory) || !File.Exists(serverScript) || !File.Exists(manifest))
        {
            throw new DirectoryNotFoundException("所选文件夹不是有效的 RVC Streaming Server 目录（需要 server.py 和 pixi.toml）。");
        }

        var interpreter = ResolveServerPython(_serverDirectory);
        if (interpreter is null)
        {
            throw new FileNotFoundException("未找到 Server 的 Pixi Python 环境。请先在该文件夹运行 pixi install。");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = interpreter,
            WorkingDirectory = _serverDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(serverScript);
        startInfo.ArgumentList.Add("--stdio");
        startInfo.Environment["PYTHONUNBUFFERED"] = "1";
        startInfo.Environment["RVC_STREAMING_TOKEN"] = string.Empty;
        startInfo.Environment["RVC_STREAMING_TRANSPORT"] = "stdio";
        AppPaths.ConfigureLocalServerProcess(startInfo);

        _process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("无法启动本地 RVC Server 进程。");
        _readTask = Task.Run(() => ReadLoopAsync(_lifetimeCts.Token));
        _stderrTask = Task.Run(() => ReadStderrAsync(_lifetimeCts.Token));

        using var startupCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        startupCts.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            await _ready.Task.WaitAsync(startupCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("本地 Server 在 60 秒内没有建立进程管道。");
        }
    }

    public Task SendTextAsync(
        LocalServerChannel channel,
        byte[] payload,
        CancellationToken cancellationToken = default) =>
        SendFrameAsync((byte)channel, TextKind, payload, cancellationToken);

    public Task SendBinaryAsync(
        LocalServerChannel channel,
        byte[] payload,
        CancellationToken cancellationToken = default) =>
        SendFrameAsync((byte)channel, BinaryKind, payload, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposing, 1) != 0) return;
        var process = _process;
        if (process is null)
        {
            _lifetimeCts.Dispose();
            _sendLock.Dispose();
            return;
        }

        if (!process.HasExited)
        {
            try
            {
                await SendFrameAsync((byte)LocalServerChannel.Control, CloseKind, [], CancellationToken.None, allowDuringDispose: true);
                await SendFrameAsync((byte)LocalServerChannel.Audio, CloseKind, [], CancellationToken.None, allowDuringDispose: true);
            }
            catch
            {
            }
            try { process.StandardInput.Close(); } catch { }

            try
            {
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (TimeoutException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch
                {
                }
            }
        }

        _lifetimeCts.Cancel();
        await IgnoreCancellationAsync(_readTask);
        await IgnoreCancellationAsync(_stderrTask);
        process.Dispose();
        _process = null;
        _lifetimeCts.Dispose();
        _sendLock.Dispose();
    }

    private static string? ResolveServerPython(string serverDirectory)
    {
        var candidates = OperatingSystem.IsWindows()
            ? new[]
            {
                Path.Combine(serverDirectory, ".pixi", "envs", "default", "python.exe"),
                Path.Combine(serverDirectory, "python.exe"),
            }
            : new[]
            {
                Path.Combine(serverDirectory, ".pixi", "envs", "default", "bin", "python"),
                Path.Combine(serverDirectory, "bin", "python"),
            };
        return candidates.FirstOrDefault(File.Exists);
    }

    private async Task SendFrameAsync(
        byte channel,
        byte kind,
        byte[] payload,
        CancellationToken cancellationToken,
        bool allowDuringDispose = false)
    {
        if (payload.Length > MaxMessageBytes)
            throw new InvalidOperationException($"本地管道消息超过 {MaxMessageBytes} 字节限制。");
        if (!allowDuringDispose && !IsOpen)
            throw new InvalidOperationException("本地 Server 管道尚未连接。");
        var process = _process ?? throw new InvalidOperationException("本地 Server 进程尚未启动。");

        var header = new byte[HeaderSize];
        Magic.CopyTo(header, 0);
        header[4] = channel;
        header[5] = kind;
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(6, 4), (uint)payload.Length);

        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            await process.StandardInput.BaseStream.WriteAsync(header, cancellationToken);
            if (payload.Length > 0)
                await process.StandardInput.BaseStream.WriteAsync(payload, cancellationToken);
            await process.StandardInput.BaseStream.FlushAsync(cancellationToken);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            var stream = (_process ?? throw new InvalidOperationException()).StandardOutput.BaseStream;
            var header = new byte[HeaderSize];
            while (!cancellationToken.IsCancellationRequested)
            {
                if (!await ReadExactlyOrEofAsync(stream, header, cancellationToken)) break;
                if (!header.AsSpan(0, 4).SequenceEqual(Magic))
                    throw new InvalidDataException("本地 Server 返回了无效的管道协议数据。");

                var channel = header[4];
                var kind = header[5];
                var payloadLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(6, 4)));
                if (payloadLength > MaxMessageBytes)
                    throw new InvalidDataException($"本地 Server 返回了过大的消息：{payloadLength} 字节。");
                var payload = new byte[payloadLength];
                if (payloadLength > 0 && !await ReadExactlyOrEofAsync(stream, payload, cancellationToken))
                    throw new EndOfStreamException("本地 Server 管道消息未完整写入。");

                if (channel == TransportChannel && kind == ReadyKind)
                {
                    Interlocked.Exchange(ref _readyState, 1);
                    _ready.TrySetResult();
                    continue;
                }
                if (channel is not (byte)LocalServerChannel.Control and not (byte)LocalServerChannel.Audio)
                    throw new InvalidDataException($"本地 Server 返回了未知通道：{channel}。");

                if (kind == TextKind)
                    TextMessageReceived?.Invoke(this, Encoding.UTF8.GetString(payload));
                else if (kind == BinaryKind)
                    BinaryMessageReceived?.Invoke(this, payload);
                else if (kind == CloseKind)
                    throw new EndOfStreamException("本地 Server 已关闭进程管道。");
                else
                    throw new InvalidDataException($"本地 Server 返回了未知消息类型：{kind}。");
            }

            if (Volatile.Read(ref _disposing) == 0)
                RaiseClosed("本地 Server 进程管道已关闭");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _ready.TrySetException(ex);
            if (Volatile.Read(ref _disposing) == 0)
            {
                LogReceived?.Invoke(this, $"本地 Server 管道错误: {ex.Message}");
                RaiseClosed("本地 Server 进程已结束");
            }
        }
        finally
        {
            if (Volatile.Read(ref _readyState) == 0)
                _ready.TrySetException(new InvalidOperationException("本地 Server 在管道就绪前退出。"));
        }
    }

    private async Task ReadStderrAsync(CancellationToken cancellationToken)
    {
        try
        {
            var reader = (_process ?? throw new InvalidOperationException()).StandardError;
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken);
                if (line is null) break;
                if (!string.IsNullOrWhiteSpace(line))
                    LogReceived?.Invoke(this, $"[本地 Server] {line}");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (Volatile.Read(ref _disposing) != 0)
        {
        }
    }

    private void RaiseClosed(string reason)
    {
        Interlocked.Exchange(ref _readyState, 0);
        if (Interlocked.Exchange(ref _closedRaised, 1) == 0)
            Closed?.Invoke(this, reason);
    }

    private static async Task<bool> ReadExactlyOrEofAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..], cancellationToken);
            if (read == 0)
            {
                if (offset == 0) return false;
                throw new EndOfStreamException("本地 Server 管道帧被截断。");
            }
            offset += read;
        }
        return true;
    }

    private static async Task IgnoreCancellationAsync(Task? task)
    {
        if (task is null) return;
        try { await task; }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (IOException) { }
    }
}
