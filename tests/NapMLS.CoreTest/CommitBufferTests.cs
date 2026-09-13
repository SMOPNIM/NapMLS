using NapMLS.Core;
using Xunit;

namespace NapMLS.CoreTest;

public class CommitBufferTests
{
    private const string GroupHash = "a1b2c3d4e5f6a7b8";

    [Fact]
    public void Commit_EpochEqualsLocalPlus1_Applies()
    {
        var buffer = new CommitBuffer();

        // Set local epoch to 41 via Welcome
        buffer.ApplyWelcome(GroupHash, 41);

        // Commit at epoch 42 (exactly +1)
        var action = buffer.ProcessCommit(GroupHash, 42, 200001, new byte[] { 1 });

        Assert.IsType<CommitAction.ApplyAction>(action);
        Assert.Equal(GroupSyncState.Ready, buffer.GetState(GroupHash));
    }

    [Fact]
    public void Commit_EpochGreaterThanLocalPlus1_BuffersAndResyncs()
    {
        var buffer = new CommitBuffer();
        buffer.ApplyWelcome(GroupHash, 41);

        // Commit at epoch 45 (gap of 4)
        var action = buffer.ProcessCommit(GroupHash, 45, 200001, new byte[] { 1 });

        Assert.IsType<CommitAction.ResyncAction>(action);
        Assert.Equal(GroupSyncState.Lagging, buffer.GetState(GroupHash));
        Assert.Equal(1, buffer.GetPendingCount(GroupHash));
    }

    [Fact]
    public void Commit_EpochLessThanLocal_Discards()
    {
        var buffer = new CommitBuffer();
        buffer.ApplyWelcome(GroupHash, 41);

        // Commit at epoch 40 (expired)
        var action = buffer.ProcessCommit(GroupHash, 40, 200001, new byte[] { 1 });

        Assert.IsType<CommitAction.DiscardAction>(action);
    }

    [Fact]
    public void Commit_EpochEqualsLocal_Discards()
    {
        var buffer = new CommitBuffer();
        buffer.ApplyWelcome(GroupHash, 41);

        // Commit at epoch 41 (same as local)
        var action = buffer.ProcessCommit(GroupHash, 41, 200001, new byte[] { 1 });

        Assert.IsType<CommitAction.DiscardAction>(action);
    }

    [Fact]
    public void Commit_LargeGap_SetsDesynced()
    {
        var buffer = new CommitBuffer();
        buffer.ApplyWelcome(GroupHash, 1);

        // Commit at epoch 20 (gap of 19 > MaxEpochGap=10)
        var action = buffer.ProcessCommit(GroupHash, 20, 200001, new byte[] { 1 });

        Assert.IsType<CommitAction.ResyncAction>(action);
        Assert.Equal(GroupSyncState.Desynced, buffer.GetState(GroupHash));
    }

    [Fact]
    public void Welcome_AppliesBufferedCommits()
    {
        var buffer = new CommitBuffer();

        // Buffer a commit at epoch 45 (gap from 41)
        buffer.ProcessCommit(GroupHash, 45, 200001, new byte[] { 1 });
        buffer.ProcessCommit(GroupHash, 46, 200002, new byte[] { 2 });
        Assert.Equal(2, buffer.GetPendingCount(GroupHash));

        // Apply Welcome at epoch 44
        var result = buffer.ApplyWelcome(GroupHash, 44);

        Assert.Equal(44, result.Epoch);
        // Epoch 45 should be now applicable (44+1)
        Assert.Single(result.BufferedCommits);
        Assert.Equal(45, result.BufferedCommits[0].Epoch);
        // Epoch 46 should still be buffered
        Assert.Equal(1, buffer.GetPendingCount(GroupHash));
    }

    [Fact]
    public void Welcome_NoPending_ReturnsEmpty()
    {
        var buffer = new CommitBuffer();

        var result = buffer.ApplyWelcome(GroupHash, 41);

        Assert.Equal(41, result.Epoch);
        Assert.Empty(result.BufferedCommits);
    }

    [Fact]
    public void Commit_DuplicateEpochSender_BuffersOnlyOnce()
    {
        var buffer = new CommitBuffer();
        buffer.ApplyWelcome(GroupHash, 41);

        // Same commit twice
        buffer.ProcessCommit(GroupHash, 45, 200001, new byte[] { 1 });
        buffer.ProcessCommit(GroupHash, 45, 200001, new byte[] { 2 });

        Assert.Equal(1, buffer.GetPendingCount(GroupHash));
    }

    [Fact]
    public void Commit_DifferentSenders_SameEpoch_BothBuffered()
    {
        var buffer = new CommitBuffer();
        buffer.ApplyWelcome(GroupHash, 41);

        buffer.ProcessCommit(GroupHash, 45, 200001, new byte[] { 1 });
        buffer.ProcessCommit(GroupHash, 45, 200002, new byte[] { 2 });

        Assert.Equal(2, buffer.GetPendingCount(GroupHash));
    }

    [Fact]
    public void ResyncCooldown_PreventsMultipleResyncs()
    {
        var buffer = new CommitBuffer();
        buffer.ApplyWelcome(GroupHash, 41);

        // First commit triggers resync
        var action1 = buffer.ProcessCommit(GroupHash, 45, 200001, new byte[] { 1 });
        Assert.IsType<CommitAction.ResyncAction>(action1);

        // Second commit in same gap — should buffer but NOT resync
        var action2 = buffer.ProcessCommit(GroupHash, 46, 200002, new byte[] { 2 });
        Assert.IsType<CommitAction.BufferAction>(action2);
    }

    [Fact]
    public void ShouldRespondToResync_RateLimited()
    {
        var buffer = new CommitBuffer();

        Assert.True(buffer.ShouldRespondToResync(GroupHash, 42));
        Assert.False(buffer.ShouldRespondToResync(GroupHash, 42)); // Rate limited
        Assert.True(buffer.ShouldRespondToResync(GroupHash, 43)); // Different epoch
    }

    [Fact]
    public void RemoveGroup_ClearsState()
    {
        var buffer = new CommitBuffer();
        buffer.ApplyWelcome(GroupHash, 41);
        buffer.ProcessCommit(GroupHash, 45, 200001, new byte[] { 1 });

        buffer.RemoveGroup(GroupHash);

        Assert.Null(buffer.GetState(GroupHash));
        Assert.Equal(0, buffer.GetPendingCount(GroupHash));
    }

    [Fact]
    public void UnknownGroup_Commit_SmallGap_BuffersAndAwaitingWelcome()
    {
        var buffer = new CommitBuffer();

        // Commit at epoch 5 from unknown group (local=-1, gap=6 <= MaxEpochGap=10)
        var action = buffer.ProcessCommit(GroupHash, 5, 200001, new byte[] { 1 });

        Assert.IsType<CommitAction.ResyncAction>(action);
        Assert.Equal(GroupSyncState.Lagging, buffer.GetState(GroupHash));
    }

    [Fact]
    public void SequentialCommits_AdvanceEpoch()
    {
        var buffer = new CommitBuffer();
        buffer.ApplyWelcome(GroupHash, 41);

        // Apply commits 42, 43, 44 in order
        var a1 = buffer.ProcessCommit(GroupHash, 42, 200001, new byte[] { 1 });
        var a2 = buffer.ProcessCommit(GroupHash, 43, 200001, new byte[] { 2 });
        var a3 = buffer.ProcessCommit(GroupHash, 44, 200001, new byte[] { 3 });

        Assert.IsType<CommitAction.ApplyAction>(a1);
        Assert.IsType<CommitAction.ApplyAction>(a2);
        Assert.IsType<CommitAction.ApplyAction>(a3);
        Assert.Equal(GroupSyncState.Ready, buffer.GetState(GroupHash));
    }
}
