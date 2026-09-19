using System.Collections.Concurrent;
using NapMLS.Core;
using NapMLS.NapCat;

namespace NapMLS.UI.Services;

/// <summary>
/// Bridges NapCatServer (QQ transport) ↔ MlsService (MLS crypto) ↔ MessageBus (UI events).
///
/// Inbound group:  NapCat WS → [MLS:MSG] parse → MlsService.Decrypt → MessageBus.Publish
/// Inbound private: NapCat WS → [MLS:WELCOME] parse → MlsService.JoinGroup → HashMap update
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

    /// <summary>Fires when a group is joined via Welcome (for UI to refresh group list).</summary>
    public event Action<string>? OnGroupJoined;

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

    public void RegisterGroup(string groupIdHex, long qqGroupId)
    {
        var groupIdBytes = Convert.FromHexString(groupIdHex);
        var hash = MessageChunker.ComputeGroupHash(groupIdBytes);
        _hashMap[hash] = (groupIdHex, qqGroupId);
    }

    // ── Inbound routing ──

    private void OnNapCatEvent(string json)
    {
        try
        {
            var evt = OneBotParser.ParseEvent(json);
            if (evt == null || evt.PostType != "message")
                return;

            var segments = OneBotParser.ParseMessage(evt.Message);
            var text = OneBotParser.ExtractText(segments);

            if (!OneBotParser.IsMlsMessage(text))
                return;

            if (evt.MessageType == "group")
            {
                HandleInboundGroupMessage(text, evt);
            }
            else if (evt.MessageType == "private")
            {
                HandleInboundPrivateMessage(text, evt);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Bridge] Inbound error: {ex.Message}");
        }
    }

    private void HandleInboundGroupMessage(string text, OneBotEvent evt)
    {
        var result = _chunker.TryParse(text, 0);
        if (result == null)
        {
            Console.WriteLine("[Bridge] Incomplete MLS message, waiting for chunks");
            return;
        }

        var groupHash = result.GroupHash;

        if (!_hashMap.TryGetValue(groupHash, out var groupInfo))
        {
            Console.WriteLine($"[Bridge] Unknown GROUP_HASH={groupHash}, dropping");
            return;
        }

        // Decrypt
        var plaintext = _mls.Decrypt(groupInfo.GroupIdHex, result.Ciphertext);
        if (plaintext == null)
        {
            Console.WriteLine($"[Bridge] Decrypt failed for group {groupHash}");
            return;
        }

        var senderInfo = OneBotParser.ParseSender(evt.Sender);
        var senderName = senderInfo?.Nickname ?? evt.UserId.ToString();

        Console.WriteLine($"[Bridge] Decrypted from {senderName}: {plaintext[..Math.Min(50, plaintext.Length)]}");

        _bus.Publish(new MessageReceivedEvent
        {
            GroupHash = groupHash,
            Sender = evt.UserId,
            Epoch = result.Epoch,
            Plaintext = System.Text.Encoding.UTF8.GetBytes(plaintext),
        });
    }

    private void HandleInboundPrivateMessage(string text, OneBotEvent evt)
    {
        // [MLS:WELCOME:<qqGroupId>]base64 — auto-join group with QQ binding
        if (text.StartsWith("[MLS:WELCOME:", StringComparison.Ordinal))
        {
            Console.WriteLine($"[Bridge] Welcome received from {evt.UserId}, joining group...");

            try
            {
                var bracketEnd = text.IndexOf(']');
                if (bracketEnd < 0) return;

                var header = text[13..bracketEnd]; // after "[MLS:WELCOME:" up to "]"
                long qqGroupId = 0;
                if (long.TryParse(header, out var parsed))
                    qqGroupId = parsed;

                var payload = text[(bracketEnd + 1)..];
                var welcomeBytes = Convert.FromBase64String(payload);

                var groupIdHex = _mls.JoinGroup(welcomeBytes);
                if (groupIdHex != null)
                {
                    Console.WriteLine($"[Bridge] Joined group {groupIdHex}");

                    if (qqGroupId > 0)
                    {
                        _storage.UpsertGroupBinding(new GroupBinding
                        {
                            GroupId = Convert.FromHexString(groupIdHex),
                            QqGroupId = qqGroupId.ToString(),
                            DisplayName = $"QQ {qqGroupId} 加密子群",
                        });
                        Console.WriteLine($"[Bridge] Bound group {groupIdHex} to QQ group {qqGroupId}");
                    }

                    RebuildHashMap();
                    OnGroupJoined?.Invoke(groupIdHex);
                }
                else
                {
                    Console.WriteLine("[Bridge] JoinGroup failed");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Bridge] Welcome processing error: {ex.Message}");
            }
            return;
        }

        // [MLS:KP:<base64(KP)>] — KeyPackage exchange: save to trusted_peers
        if (text.StartsWith("[MLS:KP:", StringComparison.Ordinal))
        {
            Console.WriteLine($"[Bridge] KeyPackage received from {evt.UserId}");

            try
            {
                var bracketEnd = text.IndexOf(']');
                if (bracketEnd < 0) return;
                var payload = text[(bracketEnd + 1)..];
                var kpBytes = Convert.FromBase64String(payload);

                var senderQq = evt.UserId.ToString();
                _storage.SetKeyPackage(senderQq, kpBytes);
                Console.WriteLine($"[Bridge] Saved KeyPackage for {senderQq} ({kpBytes.Length} bytes)");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Bridge] KeyPackage parse error: {ex.Message}");
            }
            return;
        }

        Console.WriteLine($"[Bridge] Unknown MLS private message: {text[..Math.Min(80, text.Length)]}");
    }

    // ── Outbound ──

    public async Task<bool> SendAsync(long qqGroupId, string groupIdHex, string plaintext, CancellationToken ct = default)
    {
        if (qqGroupId == 0)
        {
            Console.WriteLine("[Bridge] Cannot send: QQ group ID not mapped");
            return false;
        }

        var ciphertext = _mls.Encrypt(groupIdHex, plaintext);
        if (ciphertext == null)
        {
            Console.WriteLine("[Bridge] Encrypt failed");
            return false;
        }

        var groupIdBytes = Convert.FromHexString(groupIdHex);
        var groupHash = MessageChunker.ComputeGroupHash(groupIdBytes);
        var epoch = _mls.GetEpoch(groupIdHex);
        var sender = long.TryParse(_mls.GetUsername(), out var qq) ? qq : 0;

        var formatted = MessageChunker.FormatMessage(
            MlsMessageType.MSG, groupHash, epoch, sender, seq: 1, total: 1, ciphertext);

        Console.WriteLine($"[Bridge] Sending to QQ group {qqGroupId}: hash={groupHash} epoch={epoch}");

        return await _server.SendGroupMessageAsync(qqGroupId, formatted, ct);
    }

    /// <summary>Get list of QQ groups the bot is in.</summary>
    public async Task<List<(long GroupId, string GroupName)>> GetGroupListAsync(CancellationToken ct = default)
    {
        return await _server.GetGroupListAsync(ct);
    }

    public async Task<bool> SendPrivateAsync(long userId, string text, CancellationToken ct = default)
    {
        return await _server.SendPrivateMessageAsync(userId, text, ct);
    }

    /// <summary>Send a Welcome message to a user via private chat, prefixed with QQ group ID.</summary>
    public async Task<bool> SendWelcomeAsync(long userId, long qqGroupId, byte[] welcomeBytes, CancellationToken ct = default)
    {
        var payload = $"[MLS:WELCOME:{qqGroupId}]{Convert.ToBase64String(welcomeBytes)}";
        return await _server.SendPrivateMessageAsync(userId, payload, ct);
    }

    /// <summary>Send a KeyPackage to a user via private chat.</summary>
    public async Task<bool> SendKeyPackageAsync(long userId, byte[] keyPackageBytes, CancellationToken ct = default)
    {
        var payload = $"[MLS:KP:]{Convert.ToBase64String(keyPackageBytes)}";
        return await _server.SendPrivateMessageAsync(userId, payload, ct);
    }

    public void Dispose()
    {
        _server.OnEventReceived -= OnNapCatEvent;
        _chunker.Dispose();
    }
}
