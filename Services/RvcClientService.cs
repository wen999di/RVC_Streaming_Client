using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace ClientAvalonia.Services;

public sealed class RvcClientService : IAsyncDisposable
{
    private ClientWebSocket? _webSocket;
    private CancellationTokenSource? _connectionCts;
    private CancellationTokenSource? _pingCts;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public bool IsConnected => _webSocket?.State == WebSocketState.Open;

    public event EventHandler<string>? LogReceived;
    public event EventHandler<bool>? ConnectionStateChanged;
    public event EventHandler<string>? TextMessageReceived;
    public event EventHandler<byte[]>? BinaryMessageReceived;

    public async Task ConnectAsync(string serverUri)
    {
        if (IsConnected)
        {
            return;
        }

        if (!Uri.TryCreate(serverUri, UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException("无效的服务器地址。");
        }

        var socket = new ClientWebSocket();
        var cts = new CancellationTokenSource();

        try
        {
            await socket.ConnectAsync(uri, cts.Token);
        }
        catch
        {
            socket.Dispose();
            cts.Dispose();
            throw;
        }

        _webSocket = socket;
        _connectionCts = cts;
        RaiseLog($"已连接到 {serverUri}");
        ConnectionStateChanged?.Invoke(this, true);

        _ = Task.Run(() => ReceiveLoopAsync(cts.Token), cts.Token);

        _pingCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        _ = Task.Run(() => PingLoopAsync(_pingCts.Token), _pingCts.Token);
    }

    public async Task DisconnectAsync(string reason = "已断开连接")
    {
        var socket = _webSocket;
        var cts = _connectionCts;
        var pingCts = _pingCts;

        _webSocket = null;
        _connectionCts = null;
        _pingCts = null;

        try
        {
            pingCts?.Cancel();
        }
        catch
        {
        }

        try
        {
            cts?.Cancel();
        }
        catch
        {
        }

        try
        {
            if (socket is { State: WebSocketState.Open or WebSocketState.CloseReceived })
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Client disconnecting", CancellationToken.None);
            }
        }
        catch
        {
        }
        finally
        {
            socket?.Dispose();
            cts?.Dispose();
            pingCts?.Dispose();
        }

        ConnectionStateChanged?.Invoke(this, false);
        RaiseLog(reason);
    }

    public async Task SendCommandAsync(object commandObj, CancellationToken cancellationToken = default)
    {
        var socket = _webSocket;
        if (socket?.State != WebSocketState.Open)
        {
            RaiseLog("未连接到服务器");
            return;
        }

        try
        {
            var json = JsonSerializer.Serialize(commandObj);
            var bytes = Encoding.UTF8.GetBytes(json);
            await _sendLock.WaitAsync(cancellationToken);
            try
            {
                await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
            }
            finally
            {
                _sendLock.Release();
            }
        }
        catch (Exception ex)
        {
            RaiseLog($"发送命令失败: {ex.Message}");
        }
    }

    public async Task SendBinaryAsync(byte[] payload, CancellationToken cancellationToken = default)
    {
        var socket = _webSocket;
        if (socket?.State != WebSocketState.Open)
        {
            RaiseLog("未连接到服务器");
            return;
        }

        try
        {
            await _sendLock.WaitAsync(cancellationToken);
            try
            {
                await socket.SendAsync(payload, WebSocketMessageType.Binary, true, cancellationToken);
            }
            finally
            {
                _sendLock.Release();
            }
        }
        catch (Exception ex)
        {
            RaiseLog($"发送二进制数据失败: {ex.Message}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        _sendLock.Dispose();
    }

    private async Task PingLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                if (_webSocket?.State == WebSocketState.Open)
                {
                    var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
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

    private async Task ReceiveLoopAsync(CancellationToken token)
    {
        var receiveBuffer = new byte[4096];
        var messageBuffer = new List<byte>();

        try
        {
            while (_webSocket?.State == WebSocketState.Open && !token.IsCancellationRequested)
            {
                WebSocketReceiveResult result;
                messageBuffer.Clear();

                do
                {
                    result = await _webSocket.ReceiveAsync(receiveBuffer, token);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        break;
                    }

                    messageBuffer.AddRange(receiveBuffer.AsSpan(0, result.Count).ToArray());
                } while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    TextMessageReceived?.Invoke(this, Encoding.UTF8.GetString(messageBuffer.ToArray()));
                    continue;
                }

                if (result.MessageType == WebSocketMessageType.Binary)
                {
                    BinaryMessageReceived?.Invoke(this, messageBuffer.ToArray());
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            RaiseLog($"接收循环出错: {ex.Message}");
        }
        finally
        {
            if (!token.IsCancellationRequested && _webSocket != null)
            {
                await DisconnectAsync("连接已关闭");
            }
        }
    }

    private void RaiseLog(string message)
    {
        LogReceived?.Invoke(this, message);
    }
}