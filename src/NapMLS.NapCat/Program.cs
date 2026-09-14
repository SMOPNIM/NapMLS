using Microsoft.Extensions.Logging;
using NapMLS.NapCat;

var loggerFactory = LoggerFactory.Create(builder =>
{
    builder.AddConsole();
    builder.SetMinimumLevel(LogLevel.Information);
});

var logger = loggerFactory.CreateLogger("NapMLS");

Console.WriteLine("=== NapMLS — Real NapCat Test ===");
Console.WriteLine("Waiting for NapCat WebSocket connection on ws://127.0.0.1:18080");
Console.WriteLine("Token: test-token-123");
Console.WriteLine();

var server = new NapCatServer(
    host: "127.0.0.1",
    port: 18080,
    token: "test-token-123",
    logger: logger);

server.OnConnectionChanged += connected =>
{
    Console.WriteLine(connected
        ? "[Server] ✅ NapCat CONNECTED"
        : "[Server] ❌ NapCat DISCONNECTED");
};

server.OnEventReceived += json =>
{
    var evt = OneBotParser.ParseEvent(json);
    if (evt == null)
    {
        Console.WriteLine($"[Server] ⚠️ Failed to parse event: {json[..Math.Min(300, json.Length)]}");
        return;
    }

    var segments = OneBotParser.ParseMessage(evt.Message);
    var text = OneBotParser.ExtractText(segments);

    if (evt.PostType == "meta_event")
    {
        if (evt.MetaEventType == "heartbeat")
        {
            server.RecordHeartbeat();
            Console.WriteLine("[Server] 💓 Heartbeat");
        }
        else
        {
            Console.WriteLine($"[Server] Lifecycle: {evt.SubType}");
        }
    }
    else if (evt.MessageType == "group")
    {
        var sender = OneBotParser.ParseSender(evt.Sender);
        Console.WriteLine($"[Server] 📨 Group {evt.GroupId} from {sender?.Nickname ?? "?"}({evt.UserId}): {text}");
        Console.WriteLine($"[Server]    post_type={evt.PostType}, message_type={evt.MessageType}, group_id={evt.GroupId}, user_id={evt.UserId}");
        Console.WriteLine($"[Server]    message valueKind={evt.Message.ValueKind}, segments={segments.Count}");

        if (OneBotParser.IsMlsMessage(text))
        {
            Console.WriteLine($"[Server] 🔒 MLS message detected, length={text.Length}");
        }

        // Echo: send back the same message
        if (evt.GroupId.HasValue)
        {
            Console.WriteLine($"[Server] 📤 Echoing to group {evt.GroupId}: {text}");
            _ = server.SendGroupMessageAsync(evt.GroupId.Value, text);
        }
    }
    else if (evt.MessageType == "private")
    {
        var sender = OneBotParser.ParseSender(evt.Sender);
        Console.WriteLine($"[Server] 📨 Private from {sender?.Nickname ?? "?"}({evt.UserId}): {text}");
        Console.WriteLine($"[Server]    post_type={evt.PostType}, message_type={evt.MessageType}, user_id={evt.UserId}");

        // Echo private message
        Console.WriteLine($"[Server] 📤 Echoing to {evt.UserId}: {text}");
        _ = server.SendPrivateMessageAsync(evt.UserId, text);
    }
    else
    {
        Console.WriteLine($"[Server] ⚠️ Unknown message_type={evt.MessageType}");
        Console.WriteLine($"[Server]    Raw: {json[..Math.Min(200, json.Length)]}");
    }
};

await server.StartAsync();
Console.WriteLine("[Server] Listening. Press Ctrl+C to quit.");

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

try
{
    await Task.Delay(Timeout.Infinite, cts.Token);
}
catch (OperationCanceledException) { }

Console.WriteLine("[Server] Shutting down...");
await server.DisposeAsync();
