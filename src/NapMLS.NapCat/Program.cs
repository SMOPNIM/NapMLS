using Microsoft.Extensions.Logging;
using NapMLS.NapCat;

var loggerFactory = LoggerFactory.Create(builder =>
{
    builder.AddConsole();
    builder.SetMinimumLevel(LogLevel.Information);
});

var logger = loggerFactory.CreateLogger("NapMLS");

// Parse --port and --token from command line
int port = 18080;
string token = "test-token-123";
for (int i = 0; i < args.Length - 1; i++)
{
    if (args[i] == "--port" && int.TryParse(args[i + 1], out var p))
        port = p;
    if (args[i] == "--token")
        token = args[i + 1];
}

Console.WriteLine($"=== NapMLS WS Server ===");
Console.WriteLine($"Listening on ws://127.0.0.1:{port}");
Console.WriteLine($"Token: {token}");
Console.WriteLine();

var server = new NapCatServer(
    host: "127.0.0.1",
    port: port,
    token: token,
    logger: logger);

server.OnConnectionChanged += connected =>
{
    Console.WriteLine(connected
        ? "[Server] NapCat CONNECTED"
        : "[Server] NapCat DISCONNECTED");
};

server.OnEventReceived += async json =>
{
    var evt = OneBotParser.ParseEvent(json);
    if (evt == null)
    {
        Console.WriteLine($"[Server] Failed to parse event: {json[..Math.Min(300, json.Length)]}");
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
        else
        {
            Console.WriteLine($"[Server] Lifecycle: {evt.SubType}");
        }
    }
    else if (evt.MessageType == "group")
    {
        var sender = OneBotParser.ParseSender(evt.Sender);
        Console.WriteLine($"[Server] Group {evt.GroupId} from {sender?.Nickname ?? "?"}({evt.UserId}): {text}");

        if (OneBotParser.IsMlsMessage(text))
        {
            Console.WriteLine($"[Server] MLS message detected, length={text.Length}");
        }
    }
    else if (evt.MessageType == "private")
    {
        var sender = OneBotParser.ParseSender(evt.Sender);
        Console.WriteLine($"[Server] Private from {sender?.Nickname ?? "?"}({evt.UserId}): {text}");
    }
    else
    {
        Console.WriteLine($"[Server] Unknown message_type={evt.MessageType}");
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
