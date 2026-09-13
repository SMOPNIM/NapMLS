namespace NapMLS.Core;

/// <summary>
/// Sync state for a group, tracked by groupHash.
/// </summary>
public enum GroupSyncState
{
    /// <summary>Received a Commit but no Welcome yet.</summary>
    AwaitingWelcome,

    /// <summary>Epoch matches local — normal operation.</summary>
    Ready,

    /// <summary>Behind by 1-N epochs, waiting for resync.</summary>
    Lagging,

    /// <summary>Behind by >10 epochs — needs full re-Welcome.</summary>
    Desynced
}

/// <summary>
/// Pending commit entry buffered while waiting for epoch alignment.
/// </summary>
public sealed class PendingCommit
{
    public string GroupHash { get; init; } = "";
    public long Epoch { get; init; }
    public long Sender { get; init; }
    public byte[] Ciphertext { get; init; } = [];
    public DateTime ReceivedAt { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Buffers commits and manages group sync state.
///
/// State machine:
///  收到 COMMIT:
///   ├── 本地群组不存在
///   │   └── 缓冲到 PendingCommits → AwaitingWelcome
///   ├── commit.epoch == local.epoch + 1 → 直接应用
///   ├── commit.epoch > local.epoch + 1 → 缓冲 + RESYNC
///   └── commit.epoch <= local.epoch → 丢弃（过期）
/// </summary>
public sealed class CommitBuffer
{
    private const int MaxEpochGap = 10;

    private readonly Dictionary<string, GroupState> _groups = new();
    private readonly Dictionary<string, List<PendingCommit>> _pendingCommits = new();
    private readonly Dictionary<string, DateTime> _resyncCooldown = new();

    /// <summary>Get sync state for a group (for diagnostics).</summary>
    public GroupSyncState? GetState(string groupHash)
    {
        return _groups.TryGetValue(groupHash, out var state) ? state.SyncState : null;
    }

    /// <summary>Get pending commit count for a group.</summary>
    public int GetPendingCount(string groupHash)
    {
        return _pendingCommits.TryGetValue(groupHash, out var list) ? list.Count : 0;
    }

    /// <summary>
    /// Process an incoming commit.
    /// Returns the action to take.
    /// </summary>
    public CommitAction ProcessCommit(string groupHash, long commitEpoch, long sender, byte[] ciphertext)
    {
        // Get or create group state
        if (!_groups.TryGetValue(groupHash, out var state))
        {
            state = new GroupState { SyncState = GroupSyncState.AwaitingWelcome, LocalEpoch = -1 };
            _groups[groupHash] = state;
        }

        // Expired commit
        if (commitEpoch <= state.LocalEpoch)
        {
            return CommitAction.Discard($"Expired: commit epoch {commitEpoch} <= local {state.LocalEpoch}");
        }

        // Exactly one ahead — apply directly
        if (commitEpoch == state.LocalEpoch + 1)
        {
            state.LocalEpoch = commitEpoch;
            state.SyncState = GroupSyncState.Ready;
            return CommitAction.Apply(ciphertext, commitEpoch);
        }

        // Ahead by more than expected
        var gap = commitEpoch - state.LocalEpoch;

        if (gap > MaxEpochGap)
        {
            state.SyncState = GroupSyncState.Desynced;
            BufferCommit(groupHash, commitEpoch, sender, ciphertext);
            return CommitAction.Resync(state.LocalEpoch, $"Gap too large: {gap} epochs");
        }

        state.SyncState = GroupSyncState.Lagging;
        BufferCommit(groupHash, commitEpoch, sender, ciphertext);

        // Check resync cooldown
        var resyncKey = $"{groupHash}:{state.LocalEpoch}";
        if (_resyncCooldown.TryGetValue(resyncKey, out var lastResync) &&
            (DateTime.UtcNow - lastResync).TotalSeconds < 60)
        {
            return CommitAction.Buffer($"Buffered (resync cooldown active, gap={gap})");
        }

        _resyncCooldown[resyncKey] = DateTime.UtcNow;
        return CommitAction.Resync(state.LocalEpoch, $"Gap={gap} epochs");
    }

    /// <summary>
    /// Apply a Welcome message. Returns buffered commits that can now be applied.
    /// </summary>
    public WelcomeResult ApplyWelcome(string groupHash, long welcomeEpoch)
    {
        if (!_groups.TryGetValue(groupHash, out var state))
        {
            state = new GroupState { LocalEpoch = welcomeEpoch, SyncState = GroupSyncState.Ready };
            _groups[groupHash] = state;
        }
        else
        {
            state.LocalEpoch = welcomeEpoch;
            state.SyncState = GroupSyncState.Ready;
        }

        var buffered = new List<PendingCommit>();
        if (_pendingCommits.TryGetValue(groupHash, out var pending))
        {
            // Sort by epoch and return those that are now applicable
            buffered = pending
                .OrderBy(c => c.Epoch)
                .Where(c => c.Epoch == state.LocalEpoch + 1)
                .ToList();

            // Remove applied from buffer
            foreach (var commit in buffered)
            {
                pending.Remove(commit);
            }

            // Update local epoch for each applied commit
            foreach (var commit in buffered)
            {
                state.LocalEpoch = commit.Epoch;
            }

            if (pending.Count == 0)
                _pendingCommits.Remove(groupHash);
        }

        _resyncCooldown.Remove($"{groupHash}:{state.LocalEpoch}");

        return new WelcomeResult
        {
            Epoch = welcomeEpoch,
            BufferedCommits = buffered
        };
    }

    /// <summary>Check if a resync should be responded to (rate limited).</summary>
    public bool ShouldRespondToResync(string groupHash, long epoch)
    {
        var key = $"{groupHash}:{epoch}:resync";
        if (_resyncCooldown.TryGetValue(key, out var last) &&
            (DateTime.UtcNow - last).TotalSeconds < 60)
        {
            return false;
        }
        _resyncCooldown[key] = DateTime.UtcNow;
        return true;
    }

    /// <summary>Remove a group and its pending state.</summary>
    public void RemoveGroup(string groupHash)
    {
        _groups.Remove(groupHash);
        _pendingCommits.Remove(groupHash);
        // Clean up cooldown keys for this group
        var prefix = $"{groupHash}:";
        var keysToRemove = _resyncCooldown.Keys.Where(k => k.StartsWith(prefix)).ToList();
        foreach (var key in keysToRemove)
            _resyncCooldown.Remove(key);
    }

    private void BufferCommit(string groupHash, long epoch, long sender, byte[] ciphertext)
    {
        if (!_pendingCommits.TryGetValue(groupHash, out var list))
        {
            list = [];
            _pendingCommits[groupHash] = list;
        }

        // Deduplicate: same epoch + same sender = skip
        if (list.Any(c => c.Epoch == epoch && c.Sender == sender))
            return;

        list.Add(new PendingCommit
        {
            GroupHash = groupHash,
            Epoch = epoch,
            Sender = sender,
            Ciphertext = ciphertext,
            ReceivedAt = DateTime.UtcNow
        });
    }
}

internal sealed class GroupState
{
    public GroupSyncState SyncState { get; set; } = GroupSyncState.AwaitingWelcome;
    public long LocalEpoch { get; set; } = -1;
}

/// <summary>Action to take after processing a commit.</summary>
public abstract class CommitAction
{
    public static CommitAction Apply(byte[] ciphertext, long epoch)
        => new ApplyAction(ciphertext, epoch);

    public static CommitAction Buffer(string reason)
        => new BufferAction(reason);

    public static CommitAction Resync(long localEpoch, string reason)
        => new ResyncAction(localEpoch, reason);

    public static CommitAction Discard(string reason)
        => new DiscardAction(reason);

    public sealed class ApplyAction(byte[] ciphertext, long epoch) : CommitAction
    {
        public byte[] Ciphertext => ciphertext;
        public long Epoch => epoch;
        public override string ToString() => $"Apply(epoch={epoch})";
    }

    public sealed class BufferAction(string reason) : CommitAction
    {
        public string Reason => reason;
        public override string ToString() => $"Buffer({reason})";
    }

    public sealed class ResyncAction(long localEpoch, string reason) : CommitAction
    {
        public long LocalEpoch => localEpoch;
        public string Reason => reason;
        public override string ToString() => $"Resync(localEpoch={localEpoch}, {reason})";
    }

    public sealed class DiscardAction(string reason) : CommitAction
    {
        public string Reason => reason;
        public override string ToString() => $"Discard({reason})";
    }
}

/// <summary>Result of applying a Welcome message.</summary>
public sealed class WelcomeResult
{
    public long Epoch { get; init; }
    public List<PendingCommit> BufferedCommits { get; init; } = [];
}
