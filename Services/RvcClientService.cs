using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace ClientAvalonia.Services;

public sealed class RvcClientService : IAsyncDisposable
{
    private ClientWebSocket? _controlSocket;
    private ClientWebSocket? _audioSocket;
    private LocalServerSession? _localSession;
    private CancellationTokenSource? _connectionCts;
    private CancellationTokenSource? _pingCts;
    private readonly SemaphoreSlim _controlSendLock = new(1, 1);
    private readonly SemaphoreSlim _audioSendLock = new(1, 1);
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private long _generation;

    public bool IsConnected =>
        _localSession?.IsOpen == true
        || (_controlSocket?.State == WebSocketState.Open && _audioSocket?.State == WebSocketState.Open);

    public bool IsLocalConnection => _localSession?.IsOpen == true;

    public long ConnectionGeneration => Interlocked.Read(ref _generation);

    public event EventHandler<string>? LogReceived;
    public event EventHandler<bool>? ConnectionStateChanged;
    public event EventHandler<string>? TextMessageReceived;
    public event EventHandler<byte[]>? BinaryMessageReceived;

    public async Task ConnectAsync(string serverUri)
    {
        await _lifecycleLock.WaitAsync();
        try
        {
            if (IsConnected) return;
            if (!Uri.TryCreate(serverUri, UriKind.Absolute, out var baseUri)
                || (baseUri.Scheme != "ws" && baseUri.Scheme != "wss"))
                throw new InvalidOperationException("服务器地址必须使用 ws:// 或 wss://。");

            var token = Environment.GetEnvironmentVariable("RVC_STREAMING_TOKEN");
            bool insecureRemote = baseUri.Scheme == "ws" && !baseUri.IsLoopback;
            if (insecureRemote
                && !string.Equals(Environment.GetEnvironmentVariable("RVC_ALLOW_INSECURE_WS"), "1", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "远程连接默认要求 wss://。仅可信私有网络可设置 RVC_ALLOW_INSECURE_WS=1。"
                );
            }
            if (!baseUri.IsLoopback && string.IsNullOrWhiteSpace(token))
                throw new InvalidOperationException("远程连接需要设置 RVC_STREAMING_TOKEN。");

            await DisconnectCoreAsync(null, false);
            var cts = new CancellationTokenSource();
            var control = CreateSocket();
            var audio = CreateSocket();
            var generation = Interlocked.Increment(ref _generation);

            try
            {
                await control.ConnectAsync(BuildEndpoint(baseUri, "/control"), cts.Token);
                await audio.ConnectAsync(BuildEndpoint(baseUri, "/audio"), cts.Token);
            }
            catch
            {
                cts.Cancel();
                control.Dispose();
                audio.Dispose();
                cts.Dispose();
                throw;
            }

            _controlSocket = control;
            _audioSocket = audio;
            _connectionCts = cts;
            _pingCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);

            _ = Task.Run(() => ReceiveLoopAsync(control, generation, cts.Token), cts.Token);
            _ = Task.Run(() => ReceiveLoopAsync(audio, generation, cts.Token), cts.Token);
            _ = Task.Run(() => PingLoopAsync(_pingCts.Token), _pingCts.Token);

            RaiseLog($"已连接到 {serverUri}（control/audio 双通道）");
            ConnectionStateChanged?.Invoke(this, true);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async Task ConnectLocalAsync(string serverDirectory)
    {
        await _lifecycleLock.WaitAsync();
        try
        {
            if (IsConnected) return;
            await DisconnectCoreAsync(null, false);

            var cts = new CancellationTokenSource();
            var generation = Interlocked.Increment(ref _generation);
            var session = new LocalServerSession(serverDirectory);
            session.LogReceived += (_, message) => RaiseLog(message);
            session.TextMessageReceived += (_, message) =>
            {
                if (generation == Interlocked.Read(ref _generation))
                    TextMessageReceived?.Invoke(this, message);
            };
            session.BinaryMessageReceived += (_, message) =>
            {
                if (generation == Interlocked.Read(ref _generation))
                    BinaryMessageReceived?.Invoke(this, message);
            };
            session.Closed += (_, reason) =>
            {
                if (generation == Interlocked.Read(ref _generation))
                    _ = DisconnectAsync(reason);
            };

            try
            {
                await session.StartAsync(cts.Token);
            }
            catch
            {
                cts.Cancel();
                await session.DisposeAsync();
                cts.Dispose();
                throw;
            }

            _localSession = session;
            _connectionCts = cts;
            _pingCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
            _ = Task.Run(() => PingLoopAsync(_pingCts.Token), _pingCts.Token);

            RaiseLog($"已通过私有进程管道连接本地 Server：{Path.GetFullPath(serverDirectory)}");
            ConnectionStateChanged?.Invoke(this, true);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async Task DisconnectAsync(string reason = "已断开连接")
    {
        await _lifecycleLock.WaitAsync();
        try
        {
            await DisconnectCoreAsync(reason, true);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public Task SendCommandAsync(object commandObj, CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.Serialize(commandObj);
        var bytes = Encoding.UTF8.GetBytes(json);
        bool audioRoute = IsAudioControl(json);
        if (_localSession is { } localSession)
        {
            return SendLocalAsync(
                localSession,
                audioRoute ? LocalServerChannel.Audio : LocalServerChannel.Control,
                bytes,
                true,
                cancellationToken,
                audioRoute ? "音频控制命令" : "命令");
        }
        return SendAsync(
            audioRoute ? _audioSocket : _controlSocket,
            audioRoute ? _audioSendLock : _controlSendLock,
            bytes,
            WebSocketMessageType.Text,
            cancellationToken,
            audioRoute ? "音频控制命令" : "命令");
    }

    // File-transfer binary frames use the control channel.
    public Task SendBinaryAsync(byte[] payload, CancellationToken cancellationToken = default) =>
        _localSession is { } localSession
            ? SendLocalAsync(localSession, LocalServerChannel.Control, payload, false, cancellationToken, "文件数据")
            : SendAsync(_controlSocket, _controlSendLock, payload, WebSocketMessageType.Binary, cancellationToken, "文件数据");

    public Task SendAudioAsync(byte[] payload, CancellationToken cancellationToken = default) =>
        _localSession is { } localSession
            ? SendLocalAsync(localSession, LocalServerChannel.Audio, payload, false, cancellationToken, "音频数据")
            : SendAsync(_audioSocket, _audioSendLock, payload, WebSocketMessageType.Binary, cancellationToken, "音频数据");

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        _controlSendLock.Dispose();
        _audioSendLock.Dispose();
        _lifecycleLock.Dispose();
    }

    private static Uri BuildEndpoint(Uri baseUri, string path)
    {
        var builder = new UriBuilder(baseUri) { Path = path, Query = string.Empty };
        return builder.Uri;
    }

    private static ClientWebSocket CreateSocket()
    {
        var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        var token = Environment.GetEnvironmentVariable("RVC_STREAMING_TOKEN");
        if (!string.IsNullOrWhiteSpace(token))
            socket.Options.SetRequestHeader("Authorization", $"Bearer {token.Trim()}");
        return socket;
    }

    private static bool IsAudioControl(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("config", out _)) return true;
            if (!root.TryGetProperty("command", out var commandElement)) return false;
            var command = commandElement.GetString() ?? string.Empty;
            return command is "stream_start" or "stream_stop";
        }
        catch
        {
            return false;
        }
    }

    private async Task SendAsync(
        ClientWebSocket? socket,
        SemaphoreSlim sendLock,
        byte[] payload,
        WebSocketMessageType type,
        CancellationToken cancellationToken,
        string label)
    {
        if (socket?.State != WebSocketState.Open)
        {
            RaiseLog($"{label}发送失败：连接未就绪");
            if (_connectionCts != null)
                await DisconnectAsync("连接已关闭");
            return;
        }

        try
        {
            await sendLock.WaitAsync(cancellationToken);
            try
            {
                await socket.SendAsync(payload, type, true, cancellationToken);
            }
            finally
            {
                sendLock.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            RaiseLog($"{label}发送失败: {ex.Message}");
            await DisconnectAsync("连接已关闭");
        }
    }

    private async Task SendLocalAsync(
        LocalServerSession session,
        LocalServerChannel channel,
        byte[] payload,
        bool isText,
        CancellationToken cancellationToken,
        string label)
    {
        if (!session.IsOpen)
        {
            RaiseLog($"{label}发送失败：本地进程管道未就绪");
            if (_connectionCts != null)
                await DisconnectAsync("连接已关闭");
            return;
        }

        try
        {
            if (isText)
                await session.SendTextAsync(channel, payload, cancellationToken);
            else
                await session.SendBinaryAsync(channel, payload, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            RaiseLog($"{label}发送失败: {ex.Message}");
            await DisconnectAsync("连接已关闭");
        }
    }

    private async Task PingLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                if (IsConnected)
                {
                    var ts = Stopwatch.GetTimestamp();
                    await SendCommandAsync(new { command = "ping", ts }, token);
                }
                await Task.Delay(2000, token);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            RaiseLog($"Ping 循环出错: {ex.Message}");
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket, long generation, CancellationToken token)
    {
        var receiveBuffer = new byte[16 * 1024];
        try
        {
            while (socket.State == WebSocketState.Open && !token.IsCancellationRequested)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(receiveBuffer, token);
                    if (result.MessageType == WebSocketMessageType.Close) break;
                    message.Write(receiveBuffer, 0, result.Count);
                } while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Close) break;
                var data = message.ToArray();
                if (result.MessageType == WebSocketMessageType.Text)
                    TextMessageReceived?.Invoke(this, Encoding.UTF8.GetString(data));
                else if (result.MessageType == WebSocketMessageType.Binary)
                    BinaryMessageReceived?.Invoke(this, data);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested)
                RaiseLog($"接收循环出错: {ex.Message}");
        }
        finally
        {
            if (!token.IsCancellationRequested && generation == Interlocked.Read(ref _generation))
                await DisconnectAsync("连接已关闭");
        }
    }

    private async Task DisconnectCoreAsync(string? reason, bool notify)
    {
        var control = _controlSocket;
        var audio = _audioSocket;
        var local = _localSession;
        var cts = _connectionCts;
        var pingCts = _pingCts;
        bool hadConnection = control != null || audio != null || local != null;

        _controlSocket = null;
        _audioSocket = null;
        _localSession = null;
        _connectionCts = null;
        _pingCts = null;
        Interlocked.Increment(ref _generation);

        try { pingCts?.Cancel(); } catch { }
        try { cts?.Cancel(); } catch { }

        await CloseSocketAsync(control);
        await CloseSocketAsync(audio);
        if (local is not null)
            await local.DisposeAsync();
        cts?.Dispose();
        pingCts?.Dispose();

        if (notify && hadConnection)
        {
            ConnectionStateChanged?.Invoke(this, false);
            if (!string.IsNullOrWhiteSpace(reason)) RaiseLog(reason);
        }
    }

    private static async Task CloseSocketAsync(ClientWebSocket? socket)
    {
        if (socket == null) return;
        try
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Client disconnecting", CancellationToken.None);
        }
        catch
        {
        }
        finally
        {
            socket.Dispose();
        }
    }

    private void RaiseLog(string message) => LogReceived?.Invoke(this, message);
}
