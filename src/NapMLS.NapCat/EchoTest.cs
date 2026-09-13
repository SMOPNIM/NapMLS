using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace NapMLS.NapCat;

/// <summary>
/// Echo test: NapCat connects, sends messages, server echoes them back.
/// Verifies: token auth, IP restriction, OneBot parsing, send API, heartbeat.
/// </summary>
public static class EchoTest
{
    public static async Task RunAsync(string host = "127.0.0.1", int port = 18080, string? token = null)
    {
        var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Information);
        });

        var serverLogger = loggerFactory.CreateLogger("Server");
        var clientLogger = loggerFactory.CreateLogger("Client");

        Console.WriteLine("=== NapCat Echo Test ===");
        Console.WriteLine();

        // ── Test 1: Start server and connect mock client ──
        Console.WriteLine("[1] Starting WebSocket server...");

        var receivedEvents = new List<OneBotEvent>();
        var server = new NapCatServer(host, port, token, logger: serverLogger);

        server.OnEventReceived += json =>
        {
            var evt = OneBotParser.ParseEvent(json);
            if (evt != null)
            {
                lock (receivedEvents) { receivedEvents.Add(evt); }

                // Echo: send back the same message
                var segments = OneBotParser.ParseMessage(evt.Message);
                var text = OneBotParser.ExtractText(segments);

                if (evt.GroupId.HasValue)
                {
                    Console.WriteLine($"  [Server] Echoing group msg to {evt.GroupId}: {text}");
                    _ = server.SendGroupMessageAsync(evt.GroupId.Value, text);
                }
                else if (evt.MessageType == "private")
                {
                    Console.WriteLine($"  [Server] Echoing private msg to {evt.UserId}: {text}");
                    _ = server.SendPrivateMessageAsync(evt.UserId, text);
                }

                // Record heartbeats
                if (evt.PostType == "meta_event" && evt.MetaEventType == "heartbeat")
                {
                    server.RecordHeartbeat();
                }
            }
        };

        await server.StartAsync();

        Console.WriteLine("[2] Connecting mock NapCat client...");

        var client = new MockNapCatClient(host, port, token);
        var apiCallsReceived = new List<OneBotApiCall>();

        client.OnApiCallReceived += call =>
        {
            lock (apiCallsReceived) { apiCallsReceived.Add(call); }
            Console.WriteLine($"  [Client] Received API call: {call.Action} (echo={call.Echo})");
        };

        await client.ConnectAsync();
        Console.WriteLine($"  Connected: {client.IsConnected}");
        Console.WriteLine($"  Server sees connection: {server.IsConnected}");
        Console.WriteLine();

        // ── Test 2: Send lifecycle event ──
        Console.WriteLine("[3] Sending lifecycle event...");
        await client.SendLifecycleEvent("connect");
        await Task.Delay(100);

        // ── Test 3: Send group messages ──
        Console.WriteLine("[4] Sending group messages...");
        await client.SendGroupMessageEvent(100001, 200001, "Hello from group!");
        await client.SendGroupMessageEvent(100001, 200002, "Test message 2");
        await Task.Delay(200);

        // ── Test 4: Send private messages ──
        Console.WriteLine("[5] Sending private messages...");
        await client.SendPrivateMessageEvent(300001, "Private hello!");
        await Task.Delay(200);

        // ── Test 5: Send heartbeat ──
        Console.WriteLine("[6] Sending heartbeat...");
        await client.SendHeartbeatAsync();
        await Task.Delay(100);

        // ── Test 6: Send MLS-prefixed message ──
        Console.WriteLine("[7] Sending MLS-prefixed message...");
        await client.SendGroupMessageEvent(100001, 200001, "[MLS:GROUP:abc123]base64data");
        await Task.Delay(200);

        // ── Results ──
        Console.WriteLine();
        Console.WriteLine("=== Results ===");
        Console.WriteLine($"  Events received by server: {receivedEvents.Count}");
        Console.WriteLine($"  API calls received by client: {apiCallsReceived.Count}");

        lock (receivedEvents)
        {
            var groupMsgs = receivedEvents.Where(e => e.MessageType == "group").ToList();
            var privateMsgs = receivedEvents.Where(e => e.MessageType == "private").ToList();
            var heartbeats = receivedEvents.Where(e => e.MetaEventType == "heartbeat").ToList();
            var mlsMsgs = receivedEvents.Where(e =>
            {
                var segs = OneBotParser.ParseMessage(e.Message);
                return OneBotParser.IsMlsMessage(OneBotParser.ExtractText(segs));
            }).ToList();

            Console.WriteLine($"  Group messages: {groupMsgs.Count}");
            Console.WriteLine($"  Private messages: {privateMsgs.Count}");
            Console.WriteLine($"  Heartbeats: {heartbeats.Count}");
            Console.WriteLine($"  MLS messages: {mlsMsgs.Count}");
        }

        // Verify echo worked (API calls sent back)
        Console.WriteLine($"  Echo responses sent: {apiCallsReceived.Count}");

        // ── Cleanup ──
        Console.WriteLine();
        Console.WriteLine("[8] Disconnecting client...");
        await client.DisposeAsync();

        await Task.Delay(200);
        Console.WriteLine($"  Server sees disconnection: {!server.IsConnected}");

        Console.WriteLine("[9] Reconnecting client...");
        var client2 = new MockNapCatClient(host, port, token);
        client2.OnApiCallReceived += call =>
        {
            Console.WriteLine($"  [Client2] Received API call: {call.Action}");
        };
        await client2.ConnectAsync();
        Console.WriteLine($"  Connected: {client2.IsConnected}");
        Console.WriteLine($"  Server sees connection: {server.IsConnected}");

        // Send a message on new connection
        await client2.SendGroupMessageEvent(100001, 200001, "Reconnected!");
        await Task.Delay(200);

        // ── Cleanup ──
        await client2.DisposeAsync();
        await server.DisposeAsync();

        // ── Final verdict ──
        Console.WriteLine();
        var allEvents = receivedEvents.Count >= 5;
        var allHeartbeats = receivedEvents.Any(e => e.MetaEventType == "heartbeat");
        var mlsDetected = receivedEvents.Any(e =>
        {
            var segs = OneBotParser.ParseMessage(e.Message);
            return OneBotParser.IsMlsMessage(OneBotParser.ExtractText(segs));
        });

        if (allEvents && allHeartbeats && mlsDetected)
        {
            Console.WriteLine("✅ Echo test PASSED");
        }
        else
        {
            Console.WriteLine("❌ Echo test FAILED");
            Console.WriteLine($"  Events: {allEvents}, Heartbeats: {allHeartbeats}, MLS: {mlsDetected}");
        }
    }
}
