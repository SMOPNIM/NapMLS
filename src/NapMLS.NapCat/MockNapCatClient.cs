using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace NapMLS.NapCat;

/// <summary>
/// Mock NapCat client for testing.
/// Simulates NapCat reverse WebSocket connection behavior:
/// - Connects to C# server
/// - Sends OneBot 11 events (group messages, private messages, heartbeats)
/// - Receives API calls from server
/// - Sends heartbeats every 30s
/// </summary>
public sealed class MockNapCatClient : IAsyncDisposable
{
    private readonly string _url;
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private Task? _receiveLoop;
    private Task? _heartbeatLoop;

    public bool IsConnected => _ws?.State == WebSocketState.Open;

    /// <summary>Fires when an API call is received from the server.</summary>
    public event Action<OneBotApiCall>? OnApiCallReceived;

    public MockNapCatClient(string host = "127.0.0.1", int port = 8080, string? token = null)
    {
        var query = token != null ? $"?access_token={token}" : "";
        _url = $"ws://{host}:{port}{query}";
    }

    /// <summary>Connect to the C# server.</summary>
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        _ws = new ClientWebSocket();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        await _ws.ConnectAsync(new Uri(_url), ct);

        _receiveLoop = ReceiveLoopAsync(_cts.Token);
        _heartbeatLoop = HeartbeatLoopAsync(_cts.Token);
    }

    /// <summary>Send a mock group message event.</summary>
    public async Task SendGroupMessageEvent(
        long groupId,
        long userId,
        string text,
        string nickname = "TestUser")
    {
        var segments = new List<object>
        {
            new { type = "text", data = new { text } }
        };

        var evt = new
        {
            time = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            self_id = 123456789L,
            post_type = "message",
            message_type = "group",
            sub_type = "normal",
            message_id = Random.Shared.NextInt64(),
            user_id = userId,
            group_id = groupId,
            message = segments,
            raw_message = text,
            sender = new
            {
                user_id = userId,
                nickname = nickname,
                card = nickname,
                role = "member"
            }
        };

        await SendEventAsync(evt);
    }

    /// <summary>Send a mock private message event.</summary>
    public async Task SendPrivateMessageEvent(long userId, string text, string nickname = "TestUser")
    {
        var segments = new List<object>
        {
            new { type = "text", data = new { text } }
        };

        var evt = new
        {
            time = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            self_id = 123456789L,
            post_type = "message",
            message_type = "private",
            sub_type = "friend",
            message_id = Random.Shared.NextInt64(),
            user_id = userId,
            message = segments,
            raw_message = text,
            sender = new
            {
                user_id = userId,
                nickname = nickname,
                card = nickname,
                role = "friend"
            }
        };

        await SendEventAsync(evt);
    }

    /// <summary>Send a mock heartbeat event.</summary>
    public async Task SendHeartbeatAsync()
    {
        var hb = new
        {
            time = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            self_id = 123456789L,
            post_type = "meta_event",
            meta_event_type = "heartbeat",
            status = new
            {
                good = true,
                online = true
            },
            interval = 30000
        };

        await SendEventAsync(hb);
    }

    /// <summary>Send a mock lifecycle event (connect).</summary>
    public async Task SendLifecycleEvent(string subType = "connect")
    {
        var evt = new
        {
            time = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            self_id = 123456789L,
            post_type = "meta_event",
            meta_event_type = "lifecycle",
            sub_type = subType
        };

        await SendEventAsync(evt);
    }

    private async Task SendEventAsync(object evt)
    {
        if (_ws == null || _ws.State != WebSocketState.Open)
            throw new InvalidOperationException("Not connected");

        var json = JsonSerializer.Serialize(evt);
        var bytes = Encoding.UTF8.GetBytes(json);

        await _ws.SendAsync(
            new ArraySegment<byte>(bytes),
            WebSocketMessageType.Text,
            true,
            CancellationToken.None);
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];

        try
        {
            while (_ws != null && _ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);

                if (result.MessageType == WebSocketMessageType.Close)
                    break;

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    var json = Encoding.UTF8.GetString(buffer, 0, result.Count);

                    // Try to parse as API call
                    try
                    {
                        var call = JsonSerializer.Deserialize<OneBotApiCall>(json);
                        if (call != null)
                            OnApiCallReceived?.Invoke(call);
                    }
                    catch { /* not an API call, ignore */ }
                }
            }
        }
        catch (WebSocketException) { }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(30_000, ct);

            if (IsConnected)
            {
                try
                {
                    await SendHeartbeatAsync();
                }
                catch { break; }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();

        if (_receiveLoop != null)
        {
            try { await _receiveLoop; } catch { }
        }
        if (_heartbeatLoop != null)
        {
            try { await _heartbeatLoop; } catch { }
        }

        if (_ws != null)
        {
            try
            {
                if (_ws.State == WebSocketState.Open)
                    await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
            }
            catch { }

            _ws.Dispose();
        }

        _cts?.Dispose();
    }
}
