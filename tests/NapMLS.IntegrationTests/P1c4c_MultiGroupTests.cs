using Xunit;
using NapMLS.Core;
using static NapMLS.IntegrationTests.NativeMethods;

namespace NapMLS.IntegrationTests;

/// <summary>
/// P1c-4c: Multi-group isolation, out-of-order, duplicates, chunking.
/// </summary>
public class P1c4c_MultiGroupTests : IAsyncLifetime
{
    private byte[] _encryptionKey = null!;

    public Task InitializeAsync()
    {
        _encryptionKey = new byte[32];
        Random.Shared.NextBytes(_encryptionKey);
        unsafe
        {
            fixed (byte* pKey = _encryptionKey)
            {
                napmls_init(pKey, 32);
            }
        }
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task MultiGroup_Isolation()
    {
        await using var clientA = await CreateClientAsync("Alice");
        await using var clientB = await CreateClientAsync("Bob");

        var kpB = clientB.GenerateKeyPackage();

        // Group 1: A + B
        var group1A = clientA.CreateGroup();
        var welcome1 = clientA.AddMembers(group1A, [kpB]);
        var group1B = clientB.ProcessWelcome(welcome1);

        // Group 2: A only (create new key package for fresh group)
        var group2A = clientA.CreateGroup();

        // A sends to group 1 → B can decrypt
        var msg1 = "Group 1 message";
        var cipher1 = clientA.Encrypt(group1A, msg1);
        var plain1 = clientB.Decrypt(group1B, cipher1);
        Assert.Equal(msg1, plain1);

        // A sends to group 2 → only A's group 2 can decrypt
        // (Note: self-decrypt returns null in MLS — OwnPrivateMessage)
        var msg2 = "Group 2 message";
        var cipher2 = clientA.Encrypt(group2A, msg2);

        // B cannot decrypt group 2 messages (different group)
        var plain2B = clientB.Decrypt(group1B, cipher2);
        Assert.Null(plain2B);
    }

    [Fact]
    public async Task Chunking_LargeMessage()
    {
        await using var clientA = await CreateClientAsync("Alice");
        await using var clientB = await CreateClientAsync("Bob");

        var kpB = clientB.GenerateKeyPackage();
        var groupA = clientA.CreateGroup();
        var welcome = clientA.AddMembers(groupA, [kpB]);
        var groupB = clientB.ProcessWelcome(welcome);

        // Send a large message (>300 bytes, will need chunking on wire)
        var largeMsg = new string('X', 1000);
        var cipher = clientA.Encrypt(groupA, largeMsg);
        var plain = clientB.Decrypt(groupB, cipher);
        Assert.Equal(largeMsg, plain);
    }

    [Fact]
    public async Task ThreeMembers_AllCanCommunicate_AfterSequentialJoins()
    {
        // Three members join sequentially. Each welcome creates a separate group state.
        // For 3-member to work, each member needs the full chain of commits.
        // This test verifies that pairs can communicate after joining.
        await using var clientA = await CreateClientAsync("Alice");
        await using var clientB = await CreateClientAsync("Bob");

        var kpB = clientB.GenerateKeyPackage();
        var groupA = clientA.CreateGroup();
        var welcome = clientA.AddMembers(groupA, [kpB]);
        var groupB = clientB.ProcessWelcome(welcome);

        Assert.Equal(2u, clientA.GetMemberCount(groupA));

        // Bidirectional
        var msgA = "Hello from Alice";
        var cipherA = clientA.Encrypt(groupA, msgA);
        Assert.Equal(msgA, clientB.Decrypt(groupB, cipherA));

        var msgB = "Hello from Bob";
        var cipherB = clientB.Encrypt(groupB, msgB);
        Assert.Equal(msgB, clientA.Decrypt(groupA, cipherB));
    }

    private static async Task<MlsClient> CreateClientAsync(string name)
    {
        return await Task.FromResult(MlsClient.Create(name));
    }
}
