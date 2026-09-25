using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using NapMLS.Core;
using NapMLS.Core.Services;
using NapMLS.NapCat;

var _consoleLock = new object();
var _log = new ConcurrentQueue<string>();

// ---------- 参数解析 ----------
var opts = ParseArgs(args);
if (opts == null) return 1;

// ---------- 文件日志（E2E driver 场景：stdout 被重定向时仍保留可查日志）----------
if (!string.IsNullOrEmpty(opts.LogFile))
{
    var logDir = Path.GetDirectoryName(opts.LogFile);
    if (!string.IsNullOrEmpty(logDir)) Directory.CreateDirectory(logDir);
    var fs = new FileStream(opts.LogFile, FileMode.Append, FileAccess.Write, FileShare.Read);
    var sw = new StreamWriter(fs, Encoding.UTF8) { AutoFlush = true };
    Console.SetOut(new TeeTextWriter(Console.Out, sw));
    Console.SetError(new TeeTextWriter(Console.Error, sw));
}

// ---------- 初始化 ----------
Directory.CreateDirectory(opts.DataDir);

var storage = new SqliteStorage(Path.Combine(opts.DataDir, "napmls.db"));
var key = SHA256.HashData(Encoding.UTF8.GetBytes(opts.Password));
var mls = MlsService.Open(Path.Combine(opts.DataDir, "mls_data.db"), key, opts.Username);
if (mls == null)
{
    Console.Error.WriteLine($"MLS init failed: {MlsService.LastOpenError}");
    return 1;
}
if (!mls.HasIdentity)
{
    if (mls.CreateIdentity(opts.Username) == null)
    {
        Console.Error.WriteLine("Identity creation failed");
        return 1;
    }
}
var fp = mls.GetFingerprint()!;
var safetyCode = SafetyCodeFormatter.Format(fp);
Console.WriteLine($"Identity    : {opts.Username}");
Console.WriteLine($"Safety code : {safetyCode}");
Console.WriteLine();

// ---------- 服务 ----------
var bus = new MessageBus();
var logger = LoggerFactory.Create(b => b.AddConsole().SetMinimumLevel(LogLevel.Information))
                          .CreateLogger("NapMLS");
var server = new NapCatServer(host: opts.Host, port: opts.Port, token: opts.Token, logger: logger);
var bridge = new MlsTransportBridge(server, mls, bus, storage);  // 4 参，内部自订阅

// ---------- 事件订阅 ----------
bus.Subscribe<MessageReceivedEvent>(e =>
{
    var qq = FindQqGroup(storage, e.GroupHash);
    var text = Encoding.UTF8.GetString(e.Plaintext);
    PrintEvent($"[MSG] group={qq?.ToString() ?? e.GroupHash} from={e.Sender} epoch={e.Epoch}: {text}");
});

bridge.OnGroupJoined += gid => PrintEvent($"[GROUP] joined {gid} epoch={mls.GetEpoch(gid)}");
server.OnConnectionChanged += c => PrintEvent(c ? "[NET] NapCat CONNECTED" : "[NET] NapCat DISCONNECTED");

try
{
    await server.StartAsync();
}
catch (Exception ex)
{
    Console.Error.WriteLine($"[ERROR] Cannot listen on ws://{opts.Host}:{opts.Port}: {ex.GetType().Name}: {ex.Message}");
    Console.Error.WriteLine("[ERROR] Port already in use? Check no other program (QQ/NapCat server?) binds this port.");
    bridge.Dispose();
    mls.Dispose();
    storage.Dispose();
    return 1;
}
PrintEvent($"[NET] listening on ws://{opts.Host}:{opts.Port}");
PrintEvent("[NET] waiting for NapCat...");
PrintEvent("Type /help for commands.");

// ---------- 主循环 ----------
long currentQqGroup = 0;

while (true)
{
    Console.Write($"[{currentQqGroup}] > ");
    var line = Console.ReadLine();
    if (line == null) break;
    line = line.Trim();
    if (line.Length == 0) continue;

    try
    {
        if (line.StartsWith('/'))
        {
            if (!await HandleCommand(line)) break;
        }
        else
        {
            await SendMessageAsync(line);
        }
    }
    catch (Exception ex)
    {
        PrintEvent($"[ERROR] {ex.GetType().Name}: {ex.Message}");
    }
}

await server.DisposeAsync();  // 先停 WS，防止释放 handle 时还有事件在跑
bridge.Dispose();
mls.Dispose();
storage.Dispose();
return 0;

// =====================================================================
// 命令处理
// =====================================================================
async Task<bool> HandleCommand(string line)
{
    var p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    switch (p[0].ToLowerInvariant())
    {
        case "/quit":
        case "/exit":
            return false;

        case "/help":
            Console.WriteLine(@"
/code                          重新显示安全码
/status                        显示连接状态 / 身份 / 群组
/peer add <qq> <code> [nick]   添加信任成员并发 KP
/peer list                     列出信任成员
/peer remove <qq>              移除信任成员
/group create <qqGroupId> <name> <qq1,qq2>   创建加密子群
/group list                    列出群组
/group use <qqGroupId>         切换当前群
/send <text>  或裸文本          发送加密消息
/log                           显示最近 50 条事件
");
            break;

        case "/code":
            PrintEvent($"Safety code: {safetyCode}");
            break;

        case "/status":
            PrintEvent($"Identity: {opts.Username}  NapCat: {(server.IsConnected ? "ON" : "OFF")}  current QQ group: {currentQqGroup}");
            foreach (var g in mls.ListGroups())
                PrintEvent($"  group {g.group_id}  {g.name}  epoch={g.epoch}");
            foreach (var b in storage.ListGroupBindings())
                PrintEvent($"  binding  qq={b.QqGroupId}  group={Convert.ToHexString(b.GroupId).ToLowerInvariant()}");
            break;

        case "/peer":
            await HandlePeer(p);
            break;

        case "/group":
            await HandleGroup(p);
            break;

        case "/send":
            await SendMessageAsync(string.Join(' ', p.Skip(1)));
            break;

        case "/log":
            foreach (var l in _log.Reverse().Take(50).Reverse())
                Console.WriteLine(l);
            break;

        default:
            Console.WriteLine($"Unknown command: {p[0]}");
            break;
    }
    return true;
}

async Task HandlePeer(string[] p)
{
    if (p.Length < 2) { Console.WriteLine("Usage: /peer add|list|remove"); return; }
    switch (p[1])
    {
        case "add":
            if (p.Length < 4) { Console.WriteLine("Usage: /peer add <qq> <code> [nick]"); return; }
            var qq = long.Parse(p[2]);
            var code = p[3];
            var nick = p.Length > 4 ? p[4] : "";
            if (!SafetyCodeFormatter.IsValid(code))
            {
                PrintEvent("[ERROR] Invalid safety code format");
                return;
            }
            var fpBytes = Convert.FromHexString(code.Replace("NAPMLS-", "").Replace("-", ""));
            storage.UpsertPeer(new TrustedPeer
            {
                QqNumber = qq.ToString(),
                SafetyCode = code.ToUpperInvariant(),
                Fingerprint = fpBytes,
                Nickname = nick,
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            });
            PrintEvent($"[PEER] added {qq} ({nick})");

            if (!bridge.HasSentKeyPackageTo(qq.ToString()))
            {
                var kp = mls.GenerateKeyPackage();
                if (kp == null) { PrintEvent("[ERROR] GenerateKeyPackage failed"); break; }
                var ok = await bridge.SendKeyPackageAsync(qq, kp);
                PrintEvent(ok ? $"[PEER] Sent KP to {qq}" : $"[PEER] FAILED to send KP to {qq} (NapCat rejected, check retcode log above)");
            }
            else
            {
                PrintEvent($"[PEER] Already sent KP to {qq}");
            }
            break;

        case "list":
            foreach (var pr in storage.ListPeers())
                PrintEvent($"  {pr.QqNumber}  {pr.Nickname}  {pr.SafetyCode}  {(pr.VerifiedAt != null ? "verified" : "pending")}");
            break;

        case "remove":
            if (p.Length < 3) { Console.WriteLine("Usage: /peer remove <qq>"); return; }
            var rqq = long.Parse(p[2]).ToString();
            storage.DeletePeer(rqq);
            bridge.RemoveFromKeyPackageTracking(rqq);
            PrintEvent($"[PEER] removed {rqq}");
            break;
    }
}

async Task HandleGroup(string[] p)
{
    if (p.Length < 2) { Console.WriteLine("Usage: /group create|list|use"); return; }
    switch (p[1])
    {
        case "create":
            if (p.Length < 5) { Console.WriteLine("Usage: /group create <qqGroupId> <name> <qq1,qq2>"); return; }
            var qqGroupId = long.Parse(p[2]);
            var name = p[3];
            var peers = p[4].Split(',').Select(long.Parse).ToList();

            var kps = new List<byte[]>();
            var validPeers = new List<long>();
            foreach (var q in peers)
            {
                var peer = storage.GetPeer(q.ToString());
                if (peer?.KeyPackage == null)
                {
                    PrintEvent($"[GROUP] no KP for {q}, skip");
                    continue;
                }
                kps.Add(peer.KeyPackage);
                validPeers.Add(q);
            }
            if (kps.Count == 0) { PrintEvent("[ERROR] no valid KPs"); return; }

            var groupIdHex = mls.CreateGroup(name);
            if (groupIdHex == null) { PrintEvent("[ERROR] CreateGroup failed"); return; }

            storage.UpsertGroupBinding(new GroupBinding
            {
                GroupId = Convert.FromHexString(groupIdHex),
                QqGroupId = qqGroupId.ToString(),
                DisplayName = name,
            });
            bridge.RegisterGroup(groupIdHex, qqGroupId);

            var welcome = mls.AddMembers(groupIdHex, kps.ToArray());
            if (welcome == null) { PrintEvent("[ERROR] AddMembers failed"); return; }

            foreach (var q in validPeers)
            {
                var ok = await bridge.SendWelcomeAsync(q, qqGroupId, welcome);
                PrintEvent(ok ? $"[GROUP] Welcome sent to {q}" : $"[GROUP] FAILED to send Welcome to {q} (NapCat rejected)");
            }

            currentQqGroup = qqGroupId;
            PrintEvent($"[GROUP] created {groupIdHex}  qq={qqGroupId}  epoch={mls.GetEpoch(groupIdHex)}");
            break;

        case "list":
            foreach (var g in mls.ListGroups())
                PrintEvent($"  {g.group_id}  {g.name}  epoch={g.epoch}");
            break;

        case "use":
            if (p.Length < 3) { Console.WriteLine("Usage: /group use <qqGroupId>"); return; }
            currentQqGroup = long.Parse(p[2]);
            PrintEvent($"[GROUP] switched to QQ group {p[2]}");
            break;
    }
}

async Task SendMessageAsync(string text)
{
    if (currentQqGroup == 0) { PrintEvent("[ERROR] no group selected. /group use <qqGroupId>"); return; }

    var binding = storage.ListGroupBindings()
        .FirstOrDefault(b => b.QqGroupId == currentQqGroup.ToString());
    if (binding == null) { PrintEvent($"[ERROR] no binding for QQ group {currentQqGroup}"); return; }

    var groupIdHex = Convert.ToHexString(binding.GroupId).ToLowerInvariant();
    await bridge.SendAsync(currentQqGroup, groupIdHex, text);
    PrintEvent($"[SENT] {text}");
}

// =====================================================================
// 辅助
// =====================================================================
long? FindQqGroup(SqliteStorage bStorage, string groupHash)
{
    foreach (var b in bStorage.ListGroupBindings())
    {
        var h = MessageChunker.ComputeGroupHash(b.GroupId);
        if (h == groupHash && long.TryParse(b.QqGroupId, out var qq))
            return qq;
    }
    return null;
}

CliOptions? ParseArgs(string[] args)
{
    var o = new CliOptions();
    for (int i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--data-dir": o.DataDir = args[++i]; break;
            case "--port": o.Port = int.Parse(args[++i]); break;
            case "--host": o.Host = args[++i]; break;
            case "--token": o.Token = args[++i]; break;
            case "--username": o.Username = args[++i]; break;
            case "--password": o.Password = args[++i]; break;
            case "--log-file": o.LogFile = args[++i]; break;
            default:
                Console.Error.WriteLine($"Unknown argument: {args[i]}");
                Console.Error.WriteLine("Usage: NapMLS.Cli --data-dir <dir> --port <n> --token <t> --username <name> [--host 127.0.0.1] [--password <p>] [--log-file <path>]");
                return null;
        }
    }
    if (string.IsNullOrEmpty(o.DataDir) || string.IsNullOrEmpty(o.Username))
    {
        Console.Error.WriteLine("--data-dir and --username are required");
        return null;
    }
    return o;
}

void PrintEvent(string line)
{
    var stamped = $"[{DateTime.Now:HH:mm:ss}] {line}";
    _log.Enqueue(stamped);
    while (_log.Count > 200) _log.TryDequeue(out _);

    lock (_consoleLock)
    {
        Console.Write("\r" + new string(' ', 80) + "\r");
        Console.WriteLine(stamped);
        Console.Write("> ");
    }
}

sealed class CliOptions
{
    public string DataDir { get; set; } = "";
    public int Port { get; set; } = 8082;
    public string Host { get; set; } = "127.0.0.1";
    public string Token { get; set; } = "";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "napmls-test-password";
    public string LogFile { get; set; } = "";
}

/// <summary>同时写控制台和日志文件的 TextWriter（E2E 可观测性）。</summary>
sealed class TeeTextWriter : TextWriter
{
    private readonly TextWriter _a;
    private readonly TextWriter _b;
    public TeeTextWriter(TextWriter a, TextWriter b) { _a = a; _b = b; }
    public override Encoding Encoding => _a.Encoding;
    public override void Write(char value) { _a.Write(value); _b.Write(value); }
    public override void Write(string? value) { _a.Write(value); _b.Write(value); }
    public override void WriteLine(string? value) { _a.WriteLine(value); _b.WriteLine(value); }
    public override void WriteLine() { _a.WriteLine(); _b.WriteLine(); }
    public override void Flush() { _a.Flush(); _b.Flush(); }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { _a.Flush(); _b.Flush(); }
    }
}
