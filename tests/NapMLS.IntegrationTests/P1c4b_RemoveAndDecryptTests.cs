using Xunit;
using static NapMLS.IntegrationTests.NativeMethods;

namespace NapMLS.IntegrationTests;

/// <summary>
/// P1c-4b: Bidirectional communication, member removal, old message decryption.
/// </summary>
public class P1c4b_RemoveAndDecryptTests : IAsyncLifetime
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
    public async Task RemoveMember_CannotDecryptAfter()
    {
        await using var clientA = await CreateClientAsync("Alice");
        await using var clientB = await CreateClientAsync("Bob");

        // Setup: A creates group, B joins
        var kpB = clientB.GenerateKeyPackage();
        var groupA = clientA.CreateGroup();
        var welcome = clientA.AddMembers(groupA, [kpB]);
        var groupB = clientB.ProcessWelcome(welcome);

        // B can decrypt before removal
        var msgBefore = "Before removal";
        var cipherBefore = clientA.Encrypt(groupA, msgBefore);
        var plainBefore = clientB.Decrypt(groupB, cipherBefore);
        Assert.Equal(msgBefore, plainBefore);

        // A removes B (leaf index 1, since A is 0)
        clientA.RemoveMember(groupA, 1);

        // B can still decrypt old messages (already received)
        Assert.NotNull(plainBefore);

        // B CANNOT decrypt new messages after removal
        var msgAfter = "After removal";
        var cipherAfter = clientA.Encrypt(groupA, msgAfter);
        var plainAfter = clientB.Decrypt(groupB, cipherAfter);
        Assert.Null(plainAfter); // Should fail
    }

    [Fact]
    public async Task RemoveMember_GroupSizeDecreases()
    {
        await using var clientA = await CreateClientAsync("Alice");
        await using var clientB = await CreateClientAsync("Bob");

        var kpB = clientB.GenerateKeyPackage();
        var groupA = clientA.CreateGroup();
        var welcome = clientA.AddMembers(groupA, [kpB]);
        var groupB = clientB.ProcessWelcome(welcome);

        Assert.Equal(2u, clientA.GetMemberCount(groupA));

        clientA.RemoveMember(groupA, 1);

        Assert.Equal(1u, clientA.GetMemberCount(groupA));
    }

    [Fact]
    public async Task RemoveMember_RemovedClientCannotDecrypt()
    {
        await using var clientA = await CreateClientAsync("Alice");
        await using var clientB = await CreateClientAsync("Bob");

        var kpB = clientB.GenerateKeyPackage();
        var groupA = clientA.CreateGroup();
        var welcome = clientA.AddMembers(groupA, [kpB]);
        var groupB = clientB.ProcessWelcome(welcome);

        clientA.RemoveMember(groupA, 1);

        // After removal, A sends a message — B cannot decrypt it
        var msg = "Secret after removal";
        var cipher = clientA.Encrypt(groupA, msg);
        var plain = clientB.Decrypt(groupB, cipher);
        Assert.Null(plain);
    }

    [Fact]
    public async Task Bidirectional_MultipleRounds()
    {
        await using var clientA = await CreateClientAsync("Alice");
        await using var clientB = await CreateClientAsync("Bob");

        var kpB = clientB.GenerateKeyPackage();
        var groupA = clientA.CreateGroup();
        var welcome = clientA.AddMembers(groupA, [kpB]);
        var groupB = clientB.ProcessWelcome(welcome);

        // 10 rounds of back-and-forth
        for (int i = 0; i < 10; i++)
        {
            var aToB = $"A→B #{i}";
            var c1 = clientA.Encrypt(groupA, aToB);
            var p1 = clientB.Decrypt(groupB, c1);
            Assert.Equal(aToB, p1);

            var bToA = $"B→A #{i}";
            var c2 = clientB.Encrypt(groupB, bToA);
            var p2 = clientA.Decrypt(groupA, c2);
            Assert.Equal(bToA, p2);
        }
    }

    private static async Task<MlsClient> CreateClientAsync(string name)
    {
        return await Task.FromResult(MlsClient.Create(name));
    }
}
