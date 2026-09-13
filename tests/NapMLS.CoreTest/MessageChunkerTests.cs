using NapMLS.Core;
using Xunit;

namespace NapMLS.CoreTest;

public class MessageChunkerTests
{
    [Fact]
    public void IsMlsMessage_ValidPrefix_ReturnsTrue()
    {
        Assert.True(MessageChunker.IsMlsMessage("[MLS:MSG:abc:1:1:1/1]data"));
        Assert.False(MessageChunker.IsMlsMessage("Hello world"));
        Assert.False(MessageChunker.IsMlsMessage("[MLS"));
    }

    [Fact]
    public void ComputeGroupHash_Deterministic()
    {
        var hash1 = MessageChunker.ComputeGroupHash("test-group-123");
        var hash2 = MessageChunker.ComputeGroupHash("test-group-123");
        Assert.Equal(hash1, hash2);
        Assert.Equal(16, hash1.Length); // 8 bytes = 16 hex chars
    }

    [Fact]
    public void ComputeGroupHash_DifferentInputs_DifferentHashes()
    {
        var hash1 = MessageChunker.ComputeGroupHash("group-a");
        var hash2 = MessageChunker.ComputeGroupHash("group-b");
        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    public void SingleChunk_ParsesCorrectly()
    {
        using var chunker = new MessageChunker();
        var ciphertext = new byte[] { 1, 2, 3, 4, 5 };
        var groupHash = MessageChunker.ComputeGroupHash("test-group");
        var msg = MessageChunker.FormatMessage(MlsMessageType.MSG, groupHash, 42, 200001, 1, 1, ciphertext);

        var result = chunker.TryParse(msg, 41);

        Assert.NotNull(result);
        Assert.True(result.IsComplete);
        Assert.Equal(MlsMessageType.MSG, result.Type);
        Assert.Equal(groupHash, result.GroupHash);
        Assert.Equal(42, result.Epoch);
        Assert.Equal(200001, result.Sender);
        Assert.Equal(1, result.Total);
        Assert.Equal(ciphertext, result.Ciphertext);
    }

    [Fact]
    public void ThreeChunks_InOrder_ReassemblesCorrectly()
    {
        using var chunker = new MessageChunker();
        var groupHash = MessageChunker.ComputeGroupHash("test-group");
        var chunk1 = new byte[] { 1, 2, 3 };
        var chunk2 = new byte[] { 4, 5, 6 };
        var chunk3 = new byte[] { 7, 8, 9 };

        var msg1 = MessageChunker.FormatMessage(MlsMessageType.MSG, groupHash, 42, 200001, 1, 3, chunk1);
        var msg2 = MessageChunker.FormatMessage(MlsMessageType.MSG, groupHash, 42, 200001, 2, 3, chunk2);
        var msg3 = MessageChunker.FormatMessage(MlsMessageType.MSG, groupHash, 42, 200001, 3, 3, chunk3);

        var r1 = chunker.TryParse(msg1, 41);
        var r2 = chunker.TryParse(msg2, 41);
        var r3 = chunker.TryParse(msg3, 41);

        Assert.Null(r1); // incomplete
        Assert.Null(r2); // incomplete
        Assert.NotNull(r3); // complete
        Assert.True(r3.IsComplete);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 }, r3.Ciphertext);
    }

    [Fact]
    public void ThreeChunks_OutOfOrder_ReassemblesCorrectly()
    {
        using var chunker = new MessageChunker();
        var groupHash = MessageChunker.ComputeGroupHash("test-group");
        var chunk1 = new byte[] { 1, 2, 3 };
        var chunk2 = new byte[] { 4, 5, 6 };
        var chunk3 = new byte[] { 7, 8, 9 };

        var msg1 = MessageChunker.FormatMessage(MlsMessageType.MSG, groupHash, 42, 200001, 1, 3, chunk1);
        var msg2 = MessageChunker.FormatMessage(MlsMessageType.MSG, groupHash, 42, 200001, 2, 3, chunk2);
        var msg3 = MessageChunker.FormatMessage(MlsMessageType.MSG, groupHash, 42, 200001, 3, 3, chunk3);

        // Send in reverse order
        var r3 = chunker.TryParse(msg3, 41);
        var r1 = chunker.TryParse(msg1, 41);
        var r2 = chunker.TryParse(msg2, 41);

        Assert.Null(r3); // incomplete
        Assert.Null(r1); // incomplete
        Assert.NotNull(r2); // complete
        Assert.True(r2.IsComplete);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 }, r2.Ciphertext);
    }

    [Fact]
    public void ThreeChunks_WithDuplicates_ReassemblesCorrectly()
    {
        using var chunker = new MessageChunker();
        var groupHash = MessageChunker.ComputeGroupHash("test-group");
        var chunk1 = new byte[] { 1, 2, 3 };
        var chunk2 = new byte[] { 4, 5, 6 };
        var chunk3 = new byte[] { 7, 8, 9 };

        var msg1 = MessageChunker.FormatMessage(MlsMessageType.MSG, groupHash, 42, 200001, 1, 3, chunk1);
        var msg2 = MessageChunker.FormatMessage(MlsMessageType.MSG, groupHash, 42, 200001, 2, 3, chunk2);
        var msg3 = MessageChunker.FormatMessage(MlsMessageType.MSG, groupHash, 42, 200001, 3, 3, chunk3);

        // Send chunk 1 twice
        var r1a = chunker.TryParse(msg1, 41);
        var r1b = chunker.TryParse(msg1, 41); // duplicate
        var r2 = chunker.TryParse(msg2, 41);
        var r3 = chunker.TryParse(msg3, 41);

        Assert.Null(r1a); // incomplete
        Assert.Null(r1b); // duplicate, ignored
        Assert.Null(r2);  // incomplete
        Assert.NotNull(r3); // complete
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 }, r3.Ciphertext);
    }

    [Fact]
    public void DifferentSenders_IndependentReassembly()
    {
        using var chunker = new MessageChunker();
        var groupHash = MessageChunker.ComputeGroupHash("test-group");

        // Sender 1: 2 chunks
        var s1c1 = MessageChunker.FormatMessage(MlsMessageType.MSG, groupHash, 42, 200001, 1, 2, new byte[] { 1, 2 });
        var s1c2 = MessageChunker.FormatMessage(MlsMessageType.MSG, groupHash, 42, 200001, 2, 2, new byte[] { 3, 4 });

        // Sender 2: 2 chunks
        var s2c1 = MessageChunker.FormatMessage(MlsMessageType.MSG, groupHash, 42, 200002, 1, 2, new byte[] { 10, 20 });
        var s2c2 = MessageChunker.FormatMessage(MlsMessageType.MSG, groupHash, 42, 200002, 2, 2, new byte[] { 30, 40 });

        // Interleave
        Assert.Null(chunker.TryParse(s1c1, 41));
        Assert.Null(chunker.TryParse(s2c1, 41));
        Assert.NotNull(chunker.TryParse(s1c2, 41)); // Sender 1 complete
        Assert.NotNull(chunker.TryParse(s2c2, 41)); // Sender 2 complete

        Assert.Equal(0, chunker.PendingCount);
    }

    [Fact]
    public void InvalidBase64_ReturnsNull()
    {
        using var chunker = new MessageChunker();
        var groupHash = MessageChunker.ComputeGroupHash("test-group");
        var msg = $"[MLS:MSG:{groupHash}:42:200001:1/1]!!!invalid-base64!!!";

        var result = chunker.TryParse(msg, 41);
        Assert.Null(result);
    }

    [Fact]
    public void InvalidHeader_ReturnsNull()
    {
        using var chunker = new MessageChunker();

        // Missing fields
        Assert.Null(chunker.TryParse("[MLS:MSG:abc:42]data", 41));
        // Bad seq/total
        Assert.Null(chunker.TryParse("[MLS:MSG:abc:42:200001:1]data", 41));
        // Bad epoch
        Assert.Null(chunker.TryParse("[MLS:MSG:abc:notanumber:200001:1/2]data", 41));
    }

    [Fact]
    public void FormatMessage_RoundTrips()
    {
        var groupHash = MessageChunker.ComputeGroupHash("test");
        var ciphertext = new byte[] { 10, 20, 30, 40, 50 };
        var msg = MessageChunker.FormatMessage(MlsMessageType.COMMIT, groupHash, 100, 12345, 1, 1, ciphertext);

        Assert.StartsWith("[MLS:COMMIT:", msg);
        Assert.Contains("100", msg);
        Assert.Contains("12345", msg);

        using var chunker = new MessageChunker();
        var result = chunker.TryParse(msg, 99);
        Assert.NotNull(result);
        Assert.Equal(MlsMessageType.COMMIT, result.Type);
        Assert.Equal(ciphertext, result.Ciphertext);
    }
}
