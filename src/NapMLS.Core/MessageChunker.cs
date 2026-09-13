using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace NapMLS.Core;

/// <summary>
/// Wire format: [MLS:TYPE:GROUP_HASH:EPOCH:SENDER:SEQ/TOTAL]base64
///
/// GROUP_HASH = SHA-256(group_id)[0..8] as hex (16 chars)
/// EPOCH      = decimal epoch number
/// SENDER     = QQ number or leaf index (decimal)
/// SEQ/TOTAL  = 1-based sequence / total chunks
/// base64     = standard base64 of ciphertext
///
/// Example:
///   [MLS:MSG:a1b2c3d4e5f6a7b8:42:200001:1/3]dGhpcyBpcyBh...
/// </summary>
public sealed class MessageChunker : IDisposable
{
    private const string Prefix = "[MLS:";
    private const int ReassemblyTimeoutMs = 30_000;

    private readonly ConcurrentDictionary<string, ReassemblyEntry> _pending = new();
    private readonly Timer _cleanupTimer;

    public MessageChunker()
    {
        _cleanupTimer = new Timer(CleanupStaleEntries, null, 10_000, 10_000);
    }

    /// <summary>
    /// Try to parse an [MLS:...] message.
    /// Returns ParsedMlsMessage for single-chunk or final chunk.
    /// Returns null if not an MLS message or chunk is incomplete.
    /// </summary>
    public MlsChunkResult? TryParse(string text, long localEpoch)
    {
        if (!text.StartsWith(Prefix, StringComparison.Ordinal))
            return null;

        var bracketEnd = text.IndexOf(']');
        if (bracketEnd < 0) return null;

        var header = text[Prefix.Length..bracketEnd]; // TYPE:GROUP_HASH:EPOCH:SENDER:SEQ/TOTAL
        var payload = text[(bracketEnd + 1)..];

        if (!TryParseHeader(header, out var msgType, out var groupHash, out var epoch, out var sender, out var seq, out var total))
            return null;

        byte[] ciphertext;
        try
        {
            ciphertext = Convert.FromBase64String(payload);
        }
        catch
        {
            return null;
        }

        // Single chunk
        if (total == 1)
        {
            return new MlsChunkResult
            {
                Type = msgType,
                GroupHash = groupHash,
                Epoch = epoch,
                Sender = sender,
                Seq = 1,
                Total = 1,
                Ciphertext = ciphertext,
                IsComplete = true
            };
        }

        // Multi-chunk: add to reassembly buffer
        var key = MakeKey(groupHash, epoch, msgType, sender);
        var entry = _pending.AddOrUpdate(key,
            _ => new ReassemblyEntry(total, msgType, groupHash, epoch, sender),
            (_, existing) => existing);

        lock (entry.Lock)
        {
            if (entry.Chunks.ContainsKey(seq))
            {
                // Duplicate — ignore
                return null;
            }

            entry.Chunks[seq] = ciphertext;
            entry.LastReceived = DateTime.UtcNow;

            if (entry.Chunks.Count < total)
            {
                // Incomplete
                return null;
            }

            // All chunks received — reassemble
            _pending.TryRemove(key, out _);

            var reassembled = new byte[0];
            try
            {
                reassembled = Reassemble(entry.Chunks, total);
            }
            catch
            {
                return null;
            }

            return new MlsChunkResult
            {
                Type = msgType,
                GroupHash = groupHash,
                Epoch = epoch,
                Sender = sender,
                Seq = 1,
                Total = total,
                Ciphertext = reassembled,
                IsComplete = true
            };
        }
    }

    /// <summary>Check if a text starts with [MLS:...].</summary>
    public static bool IsMlsMessage(string text)
    {
        return text.StartsWith(Prefix, StringComparison.Ordinal);
    }

    /// <summary>Compute GROUP_HASH from group_id bytes.</summary>
    public static string ComputeGroupHash(byte[] groupId)
    {
        var hash = SHA256.HashData(groupId);
        return Convert.ToHexString(hash[..8]).ToLowerInvariant();
    }

    /// <summary>Compute GROUP_HASH from group_id string.</summary>
    public static string ComputeGroupHash(string groupId)
    {
        return ComputeGroupHash(Encoding.UTF8.GetBytes(groupId));
    }

    /// <summary>Build an [MLS:...] message string.</summary>
    public static string FormatMessage(MlsMessageType type, string groupHash, long epoch, long sender, int seq, int total, byte[] ciphertext)
    {
        var b64 = Convert.ToBase64String(ciphertext);
        return $"[MLS:{type}:{groupHash}:{epoch}:{sender}:{seq}/{total}]{b64}";
    }

    /// <summary>Get number of pending reassembly entries (for diagnostics).</summary>
    public int PendingCount => _pending.Count;

    private static bool TryParseHeader(
        string header,
        out MlsMessageType msgType,
        out string groupHash,
        out long epoch,
        out long sender,
        out int seq,
        out int total)
    {
        msgType = MlsMessageType.MSG;
        groupHash = "";
        epoch = 0;
        sender = 0;
        seq = 0;
        total = 0;

        // TYPE:GROUP_HASH:EPOCH:SENDER:SEQ/TOTAL
        var parts = header.Split(':');
        if (parts.Length != 5) return false;

        if (!Enum.TryParse<MlsMessageType>(parts[0], true, out msgType))
            return false;

        groupHash = parts[1].ToLowerInvariant();

        if (!long.TryParse(parts[2], out epoch))
            return false;

        if (!long.TryParse(parts[3], out sender))
            return false;

        var slash = parts[4].IndexOf('/');
        if (slash < 0) return false;

        if (!int.TryParse(parts[4][..slash], out seq)) return false;
        if (!int.TryParse(parts[4][(slash + 1)..], out total)) return false;

        if (seq < 1 || total < 1 || seq > total) return false;

        return true;
    }

    private static string MakeKey(string groupHash, long epoch, MlsMessageType type, long sender)
    {
        return $"{groupHash}:{epoch}:{type}:{sender}";
    }

    private static byte[] Reassemble(Dictionary<int, byte[]> chunks, int total)
    {
        using var ms = new MemoryStream();
        for (int i = 1; i <= total; i++)
        {
            if (!chunks.TryGetValue(i, out var chunk))
                throw new InvalidOperationException($"Missing chunk {i}/{total}");
            ms.Write(chunk, 0, chunk.Length);
        }
        return ms.ToArray();
    }

    private void CleanupStaleEntries(object? state)
    {
        var cutoff = DateTime.UtcNow.AddMilliseconds(-ReassemblyTimeoutMs);
        var staleKeys = _pending
            .Where(kvp => kvp.Value.LastReceived < cutoff)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in staleKeys)
        {
            if (_pending.TryRemove(key, out var entry))
            {
                var received = entry.Chunks.Count;
                var expected = entry.ExpectedTotal;
                // Log stale entry — in production this would go to ILogger
                Console.Error.WriteLine(
                    $"[MessageChunker] Reassembly timeout: {entry.Type} {entry.GroupHash} " +
                    $"epoch={entry.EPOCH} sender={entry.Sender} ({received}/{expected} chunks)");
            }
        }
    }

    public void Dispose()
    {
        _cleanupTimer.Dispose();
    }
}

/// <summary>Result of parsing a single [MLS:...] message or reassembled chunks.</summary>
public sealed class MlsChunkResult
{
    public MlsMessageType Type { get; init; }
    public string GroupHash { get; init; } = "";
    public long Epoch { get; init; }
    public long Sender { get; init; }
    public int Seq { get; init; }
    public int Total { get; init; }
    public byte[] Ciphertext { get; init; } = [];
    public bool IsComplete { get; init; }
}

/// <summary>Internal state for multi-chunk reassembly.</summary>
internal sealed class ReassemblyEntry
{
    public readonly object Lock = new();
    public readonly int ExpectedTotal;
    public readonly MlsMessageType Type;
    public readonly string GroupHash;
    public readonly long EPOCH;
    public readonly long Sender;
    public readonly Dictionary<int, byte[]> Chunks = new();
    public DateTime LastReceived;

    public ReassemblyEntry(int expectedTotal, MlsMessageType type, string groupHash, long epoch, long sender)
    {
        ExpectedTotal = expectedTotal;
        Type = type;
        GroupHash = groupHash;
        EPOCH = epoch;
        Sender = sender;
        LastReceived = DateTime.UtcNow;
    }
}
