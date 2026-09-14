using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace NapMLS.NapCat;

/// <summary>
/// WebSocket server that accepts NapCat reverse WebSocket connections.
/// C# is the server; NapCat connects to us.
/// 
/// Features:
/// - Bearer token authentication via header or query parameter
/// - IP restriction (127.0.0.1 only)
/// - Single connection policy: reject new, keep existing
/// - Channel-based event pipeline for async processing
/// - Heartbeat monitoring with configurable timeout
/// </summary>
public sealed class NapCatServer : IAsyncDisposable
{
    private readonly string _host;
    private readonly int _port;
    private readonly string? _token;
    private readonly TimeSpan _heartbeatTimeout;
    private readonly ILogger _logger;

    private HttpListener? _httpListener;
    private WebSocket? _socket;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;
    private Task? _heartbeatMonitor;
    private DateTime _lastHeartbeat = DateTime.UtcNow;

    private readonly object _lock = new();
    private bool _disposed;

    /// <summary>Fires when a OneBot event is received (raw JSON).</summary>
    public event Action<string>? OnEventReceived;

    /// <summary>Fires when connection state changes.</summary>
    public event Action<bool>? OnConnectionChanged;

    /// <summary>Whether NapCat is currently connected.</summary>
    public bool IsConnected => _socket?.State == WebSocketState.Open;

    public NapCatServer(
        string host = "127.0.0.1",
        int port = 8080,
        string? token = null,
        TimeSpan? heartbeatTimeout = null,
        ILogger? logger = null)
    {
        _host = host;
        _port = port;
        _token = token;
        _heartbeatTimeout = heartbeatTimeout ?? TimeSpan.FromSeconds(90);
        _logger = logger ?? new LoggerFactory().CreateLogger<NapCatServer>();
    }

    /// <summary>Start listening for NapCat connections.</summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        _httpListener = new HttpListener();
        _httpListener.Prefixes.Add($"http://{_host}:{_port}/");
        _httpListener.Start();

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        _logger.LogInformation("NapCat WebSocket server listening on ws://{Host}:{Port}", _host, _port);

        _acceptLoop = AcceptLoopAsync(_cts.Token);
        _heartbeatMonitor = HeartbeatMonitorAsync(_cts.Token);
    }

    /// <summary>Send a raw JSON API call to NapCat.</summary>
    public async Task<OneBotResponse?> SendApiAsync(string action, JsonElement? parameters, CancellationToken ct = default)
    {
        WebSocket? socket;
        lock (_lock) { socket = _socket; }
        if (socket == null || socket.State != WebSocketState.Open)
        {
            _logger.LogWarning("Cannot send API call '{Action}': not connected", action);
            return null;
        }

        var call = new OneBotApiCall
        {
            Action = action,
            Params = parameters,
            Echo = Guid.NewGuid().ToString("N")[..8]
        };

        var json = JsonSerializer.Serialize(call);
        var bytes = System.Text.Encoding.UTF8.GetBytes(json);

        _logger.LogInformation("Sending API call: {Action}, echo={Echo}, json={Json}",
            action, call.Echo, json);

        try
        {
            await socket.SendAsync(
                new ArraySegment<byte>(bytes),
                WebSocketMessageType.Text,
                true,
                ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send API call '{Action}'", action);
            return null;
        }

        // Wait for response with matching echo (simplified: just return null for now)
        // Full implementation would use a concurrent dictionary of pending calls
        return null;
    }

    /// <summary>Send a group message.</summary>
    public async Task SendGroupMessageAsync(long groupId, string text, CancellationToken ct = default)
    {
        var msg = new SendGroupMsgRequest
        {
            GroupId = groupId,
            Message = [new MessageSegment { Type = "text", Data = JsonSerializer.SerializeToElement(new { text }) }],
            AutoEscape = false
        };
        var param = JsonSerializer.SerializeToElement(msg);
        await SendApiAsync("send_group_msg", param, ct);
    }

    /// <summary>Send a private message.</summary>
    public async Task SendPrivateMessageAsync(long userId, string text, CancellationToken ct = default)
    {
        var msg = new SendPrivateMsgRequest
        {
            UserId = userId,
            Message = [new MessageSegment { Type = "text", Data = JsonSerializer.SerializeToElement(new { text }) }],
            AutoEscape = false
        };
        var param = JsonSerializer.SerializeToElement(msg);
        await SendApiAsync("send_private_msg", param, ct);
    }

    /// <summary>Notify that a heartbeat was received (call from event handler).</summary>
    public void RecordHeartbeat()
    {
        _lastHeartbeat = DateTime.UtcNow;
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _httpListener != null)
        {
            try
            {
                var context = await _httpListener.GetContextAsync().WaitAsync(ct);

                if (!context.Request.IsWebSocketRequest)
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                    continue;
                }

                // Verify IP restriction
                var remoteIp = context.Request.RemoteEndPoint?.Address?.ToString();
                if (remoteIp != "127.0.0.1" && remoteIp != "::1")
                {
                    _logger.LogWarning("Rejected connection from non-local IP: {IP}", remoteIp);
                    context.Response.StatusCode = 403;
                    context.Response.Close();
                    continue;
                }

                // Log connection details for debugging
                var headerPairs = context.Request.Headers.AllKeys
                    .Select(k => $"{k}={context.Request.Headers[k]}");
                var headers = string.Join(", ", headerPairs);
                _logger.LogInformation("Connection from {IP} — Headers: [{Headers}], Query: [{Query}]",
                    remoteIp, headers, context.Request.QueryString.ToString());

                // Verify token
                if (_token != null)
                {
                    var providedToken = context.Request.Headers["Authorization"]?.Replace("Bearer ", "")
                        ?? context.Request.QueryString["access_token"];

                    _logger.LogInformation("Token check — provided: {Provided}, expected: {Expected}, match: {Match}",
                        providedToken ?? "null", _token, providedToken == _token);

                    if (providedToken != _token)
                    {
                        _logger.LogWarning("Rejected connection: invalid token");
                        context.Response.StatusCode = 401;
                        context.Response.Close();
                        continue;
                    }
                }

                // Single connection policy: reject new if existing is alive
                lock (_lock)
                {
                    if (_socket != null && _socket.State == WebSocketState.Open)
                    {
                        _logger.LogWarning("Rejected new connection: existing connection is active");
                        context.Response.StatusCode = 409;
                        context.Response.Close();
                        continue;
                    }
                }

                // Accept the WebSocket
                var wsContext = await context.AcceptWebSocketAsync(null);
                var newSocket = wsContext.WebSocket;

                lock (_lock)
                {
                    // Double-check after accept
                    if (_socket != null && _socket.State == WebSocketState.Open)
                    {
                        _logger.LogWarning("Rejected new connection: race condition");
                        _ = newSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
                        continue;
                    }
                    _socket = newSocket;
                    _lastHeartbeat = DateTime.UtcNow;
                }

                _logger.LogInformation("NapCat connected from {IP}", remoteIp);
                OnConnectionChanged?.Invoke(true);

                // Handle this connection
                await HandleConnectionAsync(newSocket, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Accept loop error");
                await Task.Delay(1000, ct);
            }
        }
    }

    private async Task HandleConnectionAsync(WebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024]; // 64KB receive buffer

        try
        {
            while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    _logger.LogInformation("NapCat disconnected (close frame)");
                    break;
                }

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    var json = System.Text.Encoding.UTF8.GetString(buffer, 0, result.Count);
                    OnEventReceived?.Invoke(json);
                }
            }
        }
        catch (WebSocketException ex)
        {
            _logger.LogWarning("NapCat connection error: {Message}", ex.Message);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Normal shutdown
        }
        finally
        {
            lock (_lock)
            {
                if (_socket == socket) _socket = null;
            }

            try { await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); }
            catch { /* already closed */ }

            _logger.LogInformation("NapCat connection cleaned up");
            OnConnectionChanged?.Invoke(false);
        }
    }

    private async Task HeartbeatMonitorAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(30_000, ct); // Check every 30s

            if (IsConnected && (DateTime.UtcNow - _lastHeartbeat) > _heartbeatTimeout)
            {
                _logger.LogWarning("Heartbeat timeout ({Timeout}s), closing connection",
                    _heartbeatTimeout.TotalSeconds);

                WebSocket? socket;
                lock (_lock) { socket = _socket; }
                if (socket != null)
                {
                    try
                    {
                        await socket.CloseAsync(
                            WebSocketCloseStatus.NormalClosure,
                            "heartbeat timeout",
                            CancellationToken.None);
                    }
                    catch { /* best effort */ }
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _cts?.Cancel();

        if (_acceptLoop != null)
        {
            try { await _acceptLoop; } catch { /* expected */ }
        }
        if (_heartbeatMonitor != null)
        {
            try { await _heartbeatMonitor; } catch { /* expected */ }
        }

        lock (_lock)
        {
            try { _socket?.Dispose(); } catch { }
            _socket = null;
        }

        try { _httpListener?.Stop(); _httpListener?.Close(); } catch { }
        _cts?.Dispose();

        _logger.LogInformation("NapCat server disposed");
    }
}
