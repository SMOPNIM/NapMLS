using System.Collections.Concurrent;
using System.Text.Json;
using NapMLS.Core;
using NapMLS.NapCat;

namespace NapMLS.UI.Services;

/// <summary>
/// Bridges NapCatServer (QQ transport) ↔ MlsService (MLS crypto) ↔ MessageBus (UI events).
///
/// Inbound:  NapCat WS event → [MLS:MSG] parse → MlsService.Decrypt → MessageBus.Publish
/// Outbound: ChatViewModel → MlsService.Encrypt → [MLS:MSG] format → NapCatServer.SendGroupMsg
/// </summary>
public sealed class MlsTransportBridge : IDisposable
{
    private readonly NapCatServer _server;
    private readonly MlsService _mls;
    private readonly MessageBus _bus;
    private readonly SqliteStorage _storage;
    private readonly MessageChunker _chunker = new();

    // Reverse lookup: GROUP_HASH (16 hex chars) → (MLS groupIdHex, QQ groupId)
    private readonly ConcurrentDictionary<string, (string GroupIdHex, long QqGroupId)> _hashMap = new();

    public bool IsConnected => _server.IsConnected;
    public event Action<bool>? OnConnectionChanged;

    public MlsTransportBridge(NapCatServer server, MlsService mls, MessageBus bus, SqliteStorage storage)
    {
        _server = server;
        _mls = mls;
        _bus = bus;
        _storage = storage;

        _server.OnEventReceived += OnNapCatEvent;
        _server.OnConnectionChanged += connected => OnConnectionChanged?.Invoke(connected);

        RebuildHashMap();
    }

    /// <summary>Rebuild GROUP_HASH → group mapping from current MlsService state.</summary>
    public void RebuildHashMap()
    {
        _hashMap.Clear();
        foreach (var info in _mls.ListGroups())
        {
            var groupIdBytes = Convert.FromHexString(info.group_id);
            var hash = MessageChunker.ComputeGroupHash(groupIdBytes);

            var binding = _storage.GetGroupBinding(groupIdBytes);
            long qqGroupId = 0;
            if (binding != null && long.TryParse(binding.QqGroupId, out var parsed))
                qqGroupId = parsed;

            _hashMap[hash] = (info.group_id, qqGroupId);
        }
    }

    /// <summary>Register a newly created group in the hash map.</summary>
    public void RegisterGroup(string groupIdHex, long qqGroupId)
    {
        var groupIdBytes = Convert.FromHexString(groupIdHex);
        var hash = MessageChunker.ComputeGroupHash(groupIdBytes);
        _hashMap[hash] = (groupIdHex, qqGroupId);
    }

    // ── Inbound: NapCat → MLS decrypt → MessageBus ──

    private void OnNapCatEvent(string json)
    {
        try
        {
            var evt = OneBotParser.ParseEvent(json);
            if (evt == null || evt.PostType != "message" || evt.MessageType != "group")
                return;

            var segments = OneBotParser.ParseMessage(evt.Message);
            var text = OneBotParser.ExtractText(segments);

            if (!OneBotParser.IsMlsMessage(text))
                return;

            HandleInboundMlsMessage(text, evt);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Bridge] Inbound error: {ex.Message}");
        }
    }

    private void HandleInboundMlsMessage(string text, OneBotEvent evt)
    {
        // Try parse single chunk (most common case for small messages)
        var result = _chunker.TryParse(text, 0);
        if (result == null)
        {
            Console.WriteLine($"[Bridge] Incomplete MLS message, waiting for more chunks");
            return;
        }

        var groupHash = result.GroupHash;

        if (!_hashMap.TryGetValue(groupHash, out var groupInfo))
        {
            Console.WriteLine($"[Bridge] Unknown GROUP_HASH={groupHash}, dropping");
            return;
        }

        // Self-decrypt check: skip if sender is ourselves
        var sender = evt.UserId;
        if (sender.ToString() == _mls.GetUsername())
        {
            // Own message: display plaintext locally (MLS returns null on self-decrypt)
            // The ChatViewModel handles this in SendMessage — skip inbound echo
            Console.WriteLine($"[Bridge] Own message echo, skipping decrypt");
            return;
        }

        // Decrypt
        var plaintext = _mls.Decrypt(groupInfo.GroupIdHex, result.Ciphertext);
        if (plaintext == null)
        {
            Console.WriteLine($"[Bridge] Decrypt failed for group {groupHash}");
            return;
        }

        // Resolve sender name from QQ
        var senderInfo = OneBotParser.ParseSender(evt.Sender);
        var senderName = senderInfo?.Nickname ?? sender.ToString();

        Console.WriteLine($"[Bridge] Decrypted message from {senderName}: {plaintext[..Math.Min(50, plaintext.Length)]}");

        _bus.Publish(new MessageReceivedEvent
        {
            GroupHash = groupHash,
            Sender = sender,
            Epoch = result.Epoch,
            Plaintext = System.Text.Encoding.UTF8.GetBytes(plaintext),
        });
    }

    // ── Outbound: ChatViewModel → MLS encrypt → NapCat ──

    /// <summary>Send an encrypted message to a QQ group.</summary>
    public async Task<bool> SendAsync(long qqGroupId, string groupIdHex, string plaintext, CancellationToken ct = default)
    {
        if (qqGroupId == 0)
        {
            Console.WriteLine("[Bridge] Cannot send: QQ group ID not mapped");
            return false;
        }

        // Encrypt
        var ciphertext = _mls.Encrypt(groupIdHex, plaintext);
        if (ciphertext == null)
        {
            Console.WriteLine("[Bridge] Encrypt failed");
            return false;
        }

        // Build GROUP_HASH
        var groupIdBytes = Convert.FromHexString(groupIdHex);
        var groupHash = MessageChunker.ComputeGroupHash(groupIdBytes);
        var epoch = _mls.GetEpoch(groupIdHex);

        // Format [MLS:MSG:GROUP_HASH:EPOCH:SENDER:1/1]base64
        var sender = long.TryParse(_mls.GetUsername(), out var qq) ? qq : 0;
        var formatted = MessageChunker.FormatMessage(
            MlsMessageType.MSG, groupHash, epoch, sender,
            seq: 1, total: 1, ciphertext);

        Console.WriteLine($"[Bridge] Sending to QQ group {qqGroupId}: hash={groupHash} epoch={epoch}");

        return await _server.SendGroupMessageAsync(qqGroupId, formatted, ct);
    }

    /// <summary>Send a private message (for KeyPackage / Welcome exchange).</summary>
    public async Task<bool> SendPrivateAsync(long userId, string text, CancellationToken ct = default)
    {
        return await _server.SendPrivateMessageAsync(userId, text, ct);
    }

    public void Dispose()
    {
        _server.OnEventReceived -= OnNapCatEvent;
        _chunker.Dispose();
    }
}
