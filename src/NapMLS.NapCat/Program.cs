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

server.OnEventReceived += async json =>
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
        }
        // Suppress heartbeat spam in console — only log lifecycle
        else
        {
            Console.WriteLine($"[Server] Lifecycle: {evt.SubType}");
        }
    }
    else if (evt.MessageType == "group")
    {
        var sender = OneBotParser.ParseSender(evt.Sender);
        Console.WriteLine($"[Server] 📨 Group {evt.GroupId} from {sender?.Nickname ?? "?"}({evt.UserId}): {text}");

        if (OneBotParser.IsMlsMessage(text))
        {
            Console.WriteLine($"[Server] 🔒 MLS message detected, length={text.Length}");
        }

        if (evt.GroupId.HasValue)
        {
            var ok = await server.SendGroupMessageAsync(evt.GroupId.Value, text);
            Console.WriteLine($"[Server] 📤 Echo to group {evt.GroupId}: {(ok ? "✅ sent" : "❌ failed")}");
        }
    }
    else if (evt.MessageType == "private")
    {
        var sender = OneBotParser.ParseSender(evt.Sender);
        Console.WriteLine($"[Server] 📨 Private from {sender?.Nickname ?? "?"}({evt.UserId}): {text}");

        var ok = await server.SendPrivateMessageAsync(evt.UserId, text);
        Console.WriteLine($"[Server] 📤 Echo to {evt.UserId}: {(ok ? "✅ sent" : "❌ failed")}");
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
