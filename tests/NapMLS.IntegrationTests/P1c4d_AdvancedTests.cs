using Xunit;
using NapMLS.Core;
using static NapMLS.IntegrationTests.NativeMethods;

namespace NapMLS.IntegrationTests;

/// <summary>
/// P1c-4d: Advanced integration tests.
/// - Multi-member add (3 members, 1 commit)
/// - Commit propagation (B receives A's commit when C joins)
/// - Offline resync (lag 1, 3 epochs)
/// - 10-round bidirectional consistency
/// </summary>
public class P1c4d_AdvancedTests : IAsyncLifetime
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
    public async Task MultiMemberAdd_3Members_1Commit()
    {
        await using var clientA = await CreateClientAsync("Alice");
        await using var clientB = await CreateClientAsync("Bob");
        await using var clientC = await CreateClientAsync("Charlie");

        var kpB = clientB.GenerateKeyPackage();
        var kpC = clientC.GenerateKeyPackage();

        var groupA = clientA.CreateGroup();
        var epochBefore = clientA.GetEpoch(groupA);

        // Add B and C in a single commit
        var welcome = clientA.AddMembers(groupA, [kpB, kpC]);

        var epochAfter = clientA.GetEpoch(groupA);
        Assert.Equal(epochBefore + 1, epochAfter); // Only 1 epoch advance

        // Both B and C process their respective Welcomes
        var groupB = clientB.ProcessWelcome(welcome);
        var groupC = clientC.ProcessWelcome(welcome);

        // All see 3 members
        Assert.Equal(3u, clientA.GetMemberCount(groupA));
        Assert.Equal(3u, clientB.GetMemberCount(groupB));
        Assert.Equal(3u, clientC.GetMemberCount(groupC));
    }

    [Fact]
    public async Task MultiMemberAdd_AllCanCommunicate()
    {
        await using var clientA = await CreateClientAsync("Alice");
        await using var clientB = await CreateClientAsync("Bob");
        await using var clientC = await CreateClientAsync("Charlie");

        var kpB = clientB.GenerateKeyPackage();
        var kpC = clientC.GenerateKeyPackage();

        var groupA = clientA.CreateGroup();
        var welcome = clientA.AddMembers(groupA, [kpB, kpC]);
        var groupB = clientB.ProcessWelcome(welcome);
        var groupC = clientC.ProcessWelcome(welcome);

        // A → all
        var msgA = "Hello from Alice";
        var cipherA = clientA.Encrypt(groupA, msgA);
        Assert.Equal(msgA, clientB.Decrypt(groupB, cipherA));
        Assert.Equal(msgA, clientC.Decrypt(groupC, cipherA));

        // B → all
        var msgB = "Hello from Bob";
        var cipherB = clientB.Encrypt(groupB, msgB);
        Assert.Equal(msgB, clientA.Decrypt(groupA, cipherB));
        Assert.Equal(msgB, clientC.Decrypt(groupC, cipherB));

        // C → all
        var msgC = "Hello from Charlie";
        var cipherC = clientC.Encrypt(groupC, msgC);
        Assert.Equal(msgC, clientA.Decrypt(groupA, cipherC));
        Assert.Equal(msgC, clientB.Decrypt(groupB, cipherC));
    }

    [Fact]
    public async Task RemoveAndAdd_MemberReplacement()
    {
        await using var clientA = await CreateClientAsync("Alice");
        await using var clientB = await CreateClientAsync("Bob");
        await using var clientC = await CreateClientAsync("Charlie");

        var kpB = clientB.GenerateKeyPackage();
        var groupA = clientA.CreateGroup();
        var welcome = clientA.AddMembers(groupA, [kpB]);
        var groupB = clientB.ProcessWelcome(welcome);

        // Verify A+B communication works
        var msg1 = "Before Charlie";
        Assert.Equal(msg1, clientB.Decrypt(groupB, clientA.Encrypt(groupA, msg1)));

        // A removes B
        clientA.RemoveMember(groupA, 1);
        Assert.Equal(1u, clientA.GetMemberCount(groupA));

        // A adds C
        var kpC = clientC.GenerateKeyPackage();
        var welcome2 = clientA.AddMembers(groupA, [kpC]);
        var groupC = clientC.ProcessWelcome(welcome2);

        Assert.Equal(2u, clientA.GetMemberCount(groupA));

        // A and C can communicate
        var msg2 = "After Charlie joined";
        Assert.Equal(msg2, clientC.Decrypt(groupC, clientA.Encrypt(groupA, msg2)));

        // B cannot decrypt A's new messages
        var msg3 = "B should not see this";
        Assert.Null(clientB.Decrypt(groupB, clientA.Encrypt(groupA, msg3)));
    }

    [Fact]
    public async Task Bidirectional_10Rounds_EpochConsistent()
    {
        await using var clientA = await CreateClientAsync("Alice");
        await using var clientB = await CreateClientAsync("Bob");

        var kpB = clientB.GenerateKeyPackage();
        var groupA = clientA.CreateGroup();
        var welcome = clientA.AddMembers(groupA, [kpB]);
        var groupB = clientB.ProcessWelcome(welcome);

        for (int i = 0; i < 10; i++)
        {
            var aToB = $"A→B #{i}";
            var c1 = clientA.Encrypt(groupA, aToB);
            Assert.Equal(aToB, clientB.Decrypt(groupB, c1));

            var bToA = $"B→A #{i}";
            var c2 = clientB.Encrypt(groupB, bToA);
            Assert.Equal(bToA, clientA.Decrypt(groupA, c2));
        }

        // Epochs should be consistent (application messages don't advance epoch)
        Assert.Equal(clientA.GetEpoch(groupA), clientB.GetEpoch(groupB));
    }

    [Fact]
    public async Task OwnPrivateMessage_SelfDecryptReturnsNull()
    {
        await using var clientA = await CreateClientAsync("Alice");

        var groupA = clientA.CreateGroup();

        // Self-decrypt should return null (OwnPrivateMessage)
        var msg = "Self message";
        var cipher = clientA.Encrypt(groupA, msg);
        var plain = clientA.Decrypt(groupA, cipher);
        Assert.Null(plain); // MLS behavior: can't decrypt own message
    }

    [Fact]
    public async Task ProcessWelcome_FailureReturnsError()
    {
        await using var clientA = await CreateClientAsync("Alice");
        await using var clientB = await CreateClientAsync("Bob");
        await using var clientC = await CreateClientAsync("Charlie");

        // A creates group with B
        var kpB = clientB.GenerateKeyPackage();
        var groupA = clientA.CreateGroup();
        var welcome = clientA.AddMembers(groupA, [kpB]);
        var groupB = clientB.ProcessWelcome(welcome);

        // C tries to process B's welcome — should fail (wrong key package)
        var ex = Assert.Throws<InvalidOperationException>(
            () => clientC.ProcessWelcome(welcome));
        Assert.Contains("Failed to process welcome", ex.Message);
    }

    private static async Task<MlsClient> CreateClientAsync(string name)
    {
        return await Task.FromResult(MlsClient.Create(name));
    }
}
