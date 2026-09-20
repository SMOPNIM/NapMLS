using System;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace NapMLS.IntegrationTests;

/// <summary>
/// End-to-end integration test for KP exchange → group creation → Welcome → JoinGroup flow.
/// Simulates the full invite lifecycle without NapCat transport.
/// </summary>
public class KpExchangeWelcomeTests : IAsyncLifetime
{
    private static readonly byte[] _sharedKey = new byte[32];

    public Task InitializeAsync()
    {
        unsafe
        {
            fixed (byte* pKey = _sharedKey)
            {
                NativeMethods.napmls_init(pKey, 32);
            }
        }
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// Full symmetric KP exchange + invite flow:
    /// 1. A generates KP_A, "sends" to B
    /// 2. B receives KP_A, generates KP_B, "sends" back to A
    /// 3. A now has KP_B → creates group → AddMembers(KP_B) → gets Welcome
    /// 4. B receives Welcome → JoinGroup
    /// 5. Bidirectional encrypt/decrypt works
    /// </summary>
    [Fact]
    public async Task FullFlow_KpExchangeThenInvite()
    {
        await using var a = MlsClient.Create("Alice");
        await using var b = MlsClient.Create("Bob");

        // ── Step 1-2: Symmetric KP exchange ──
        var kpA = a.GenerateKeyPackage();
        Assert.NotEmpty(kpA);

        var kpB = b.GenerateKeyPackage();
        Assert.NotEmpty(kpB);

        // ── Step 3: A creates group and adds B using KP_B ──
        var groupA = a.CreateGroup("TestGroup");
        Assert.NotNull(groupA);

        var welcomeBytes = a.AddMembers(groupA, [kpB]);
        Assert.NotNull(welcomeBytes);
        Assert.NotEmpty(welcomeBytes);

        // ── Step 4: B processes Welcome ──
        var groupB = b.ProcessWelcome(welcomeBytes);
        Assert.NotNull(groupB);

        // Verify member count is 2 on both sides
        var countA = a.GetMemberCount(groupA);
        var countB = b.GetMemberCount(groupB);
        Assert.Equal(2u, countA);
        Assert.Equal(2u, countB);

        // ── Step 5: Bidirectional encrypt/decrypt ──
        var msg1 = "Hello Bob from Alice!";
        var cipher1 = a.Encrypt(groupA, msg1);
        var plain1 = b.Decrypt(groupB, cipher1);
        Assert.Equal(msg1, plain1);

        var msg2 = "Hi Alice, Bob here!";
        var cipher2 = b.Encrypt(groupB, msg2);
        var plain2 = a.Decrypt(groupA, cipher2);
        Assert.Equal(msg2, plain2);
    }

    /// <summary>
    /// Verify epoch alignment after Welcome: both peers at same epoch.
    /// </summary>
    [Fact]
    public async Task EpochAlignment_AfterWelcome()
    {
        await using var a = MlsClient.Create("Alice");
        await using var b = MlsClient.Create("Bob");

        var kpB = b.GenerateKeyPackage();
        var groupA = a.CreateGroup("EpochTest");
        var welcome = a.AddMembers(groupA, [kpB]);
        var groupB = b.ProcessWelcome(welcome);

        var epochA = a.GetEpoch(groupA);
        var epochB = b.GetEpoch(groupB);

        Assert.Equal(epochA, epochB);
        Assert.True(epochA > 0, $"Epoch should advance after AddMembers, got {epochA}");

        // Encrypt doesn't advance epoch
        var cipher = a.Encrypt(groupA, "test");
        var plain = b.Decrypt(groupB, cipher);
        Assert.Equal("test", plain);

        var epochAfter = a.GetEpoch(groupA);
        Assert.Equal(epochA, epochAfter);
    }

    /// <summary>
    /// B decrypts A's message → verifies group context is consistent.
    /// </summary>
    [Fact]
    public async Task CrossPeerDecrypt_AliceToBob()
    {
        await using var a = MlsClient.Create("Alice");
        await using var b = MlsClient.Create("Bob");

        var kpB = b.GenerateKeyPackage();
        var groupA = a.CreateGroup("CrossTest");
        var welcome = a.AddMembers(groupA, [kpB]);
        var groupB = b.ProcessWelcome(welcome);

        // A sends, B decrypts — verifies group secret tree is in sync
        for (int i = 0; i < 10; i++)
        {
            var msg = $"Message {i}";
            var cipher = a.Encrypt(groupA, msg);
            var plain = b.Decrypt(groupB, cipher);
            Assert.Equal(msg, plain);
        }
    }

    /// <summary>
    /// B sends to A → verifies reverse direction works.
    /// </summary>
    [Fact]
    public async Task CrossPeerDecrypt_BobToAlice()
    {
        await using var a = MlsClient.Create("Alice");
        await using var b = MlsClient.Create("Bob");

        var kpB = b.GenerateKeyPackage();
        var groupA = a.CreateGroup("ReverseTest");
        var welcome = a.AddMembers(groupA, [kpB]);
        var groupB = b.ProcessWelcome(welcome);

        // B sends, A decrypts
        for (int i = 0; i < 10; i++)
        {
            var msg = $"Bob message {i}";
            var cipher = b.Encrypt(groupB, msg);
            var plain = a.Decrypt(groupA, cipher);
            Assert.Equal(msg, plain);
        }
    }
}
