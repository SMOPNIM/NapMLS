using Xunit;
using static NapMLS.IntegrationTests.NativeMethods;

namespace NapMLS.IntegrationTests;

/// <summary>
/// P1c-4a: Full handshake between two MLS clients via in-memory bus.
/// 
/// Flow:
/// 1. A creates identity + group
/// 2. B creates identity, generates KeyPackage
/// 3. A adds B to group → gets Welcome
/// 4. A sends Welcome to B (via private channel)
/// 5. B processes Welcome → joins group
/// 6. A sends encrypted message → B decrypts
/// 7. B sends encrypted message → A decrypts
/// </summary>
public class P1c4a_HandshakeTests : IAsyncLifetime
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
    public async Task FullHandshake_AInvitesB_SendAndReceive()
    {
        // ── Step 1: Create clients ──
        await using var clientA = await CreateClientAsync("Alice");
        await using var clientB = await CreateClientAsync("Bob");

        var mapA = new IdentityMap("100001", clientA);
        var mapB = new IdentityMap("200001", clientB);

        // ── Step 2: B generates KeyPackage ──
        var kpB = clientB.GenerateKeyPackage();
        mapA.RegisterKeyPackage("200001", kpB);

        // ── Step 3: A creates group and adds B ──
        var groupA = clientA.CreateGroup();
        var welcome = clientA.AddMembers(groupA, mapA.GetAllKeyPackages("200001"));

        Assert.NotEmpty(welcome);

        // ── Step 4: B processes Welcome ──
        var groupB = clientB.ProcessWelcome(welcome);
        Assert.NotNull(groupB);

        // ── Step 5: Verify group membership ──
        var countA = clientA.GetMemberCount(groupA);
        var countB = clientB.GetMemberCount(groupB);
        Assert.Equal(2u, countA);
        Assert.Equal(2u, countB);

        // ── Step 6: A encrypts → B decrypts ──
        var msg1 = "Hello Bob, this is Alice!";
        var cipher1 = clientA.Encrypt(groupA, msg1);
        var plain1 = clientB.Decrypt(groupB, cipher1);

        Assert.NotNull(plain1);
        Assert.Equal(msg1, plain1);

        // ── Step 7: B encrypts → A decrypts ──
        var msg2 = "Hi Alice, Bob here!";
        var cipher2 = clientB.Encrypt(groupB, msg2);
        var plain2 = clientA.Decrypt(groupA, cipher2);

        Assert.NotNull(plain2);
        Assert.Equal(msg2, plain2);
    }

    [Fact]
    public async Task FullHandshake_MultipleMessages()
    {
        await using var clientA = await CreateClientAsync("Alice");
        await using var clientB = await CreateClientAsync("Bob");

        var kpB = clientB.GenerateKeyPackage();
        var groupA = clientA.CreateGroup();
        var welcome = clientA.AddMembers(groupA, [kpB]);
        var groupB = clientB.ProcessWelcome(welcome);

        // Exchange multiple messages
        for (int i = 0; i < 5; i++)
        {
            var msgA = $"Message from Alice #{i}";
            var cipherA = clientA.Encrypt(groupA, msgA);
            var plainA = clientB.Decrypt(groupB, cipherA);
            Assert.Equal(msgA, plainA);

            var msgB = $"Message from Bob #{i}";
            var cipherB = clientB.Encrypt(groupB, msgB);
            var plainB = clientA.Decrypt(groupA, cipherB);
            Assert.Equal(msgB, plainB);
        }
    }

    [Fact]
    public async Task GroupEpoch_AdvancesAfterAdd()
    {
        await using var clientA = await CreateClientAsync("Alice");
        await using var clientB = await CreateClientAsync("Bob");

        var groupA = clientA.CreateGroup();
        var epochBefore = clientA.GetEpoch(groupA);
        Assert.Equal(0u, (uint)epochBefore); // Initial epoch is 0

        var kpB = clientB.GenerateKeyPackage();
        var welcome = clientA.AddMembers(groupA, [kpB]);

        var epochAfter = clientA.GetEpoch(groupA);
        Assert.True(epochAfter > epochBefore);
    }

    [Fact]
    public async Task Fingerprints_AreUnique()
    {
        await using var clientA = await CreateClientAsync("Alice");
        await using var clientB = await CreateClientAsync("Bob");

        var fpA = clientA.GetFingerprint();
        var fpB = clientB.GetFingerprint();

        Assert.NotEmpty(fpA);
        Assert.NotEmpty(fpB);
        Assert.NotEqual(fpA, fpB);
    }

    [Fact]
    public async Task IdentityMap_StoresKeyPackage()
    {
        await using var clientA = await CreateClientAsync("Alice");
        await using var clientB = await CreateClientAsync("Bob");

        var mapA = new IdentityMap("100001", clientA);
        var kpB = clientB.GenerateKeyPackage();

        mapA.RegisterKeyPackage("200001", kpB);

        Assert.True(mapA.IsTrusted("200001") == false); // Not yet trusted
        Assert.NotNull(mapA.GetKeyPackage("200001"));

        mapA.TrustPeer("200001", clientB.GetFingerprint());
        Assert.True(mapA.IsTrusted("200001"));
    }

    private static async Task<MlsClient> CreateClientAsync(string name)
    {
        return await Task.FromResult(MlsClient.Create(name));
    }
}
