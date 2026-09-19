using NapMLS.Core;
using Xunit;

namespace NapMLS.IntegrationTests;

/// <summary>
/// End-to-end tests: KP exchange → group creation → Welcome → bidirectional encrypt/decrypt.
/// Exercises MlsClient + MessageChunker wire format + MessageBus.
/// </summary>
public class P1e_E2ETests : IAsyncLifetime
{
    public Task InitializeAsync()
    {
        unsafe
        {
            fixed (byte* pKey = EncryptionKey)
            {
                NativeMethods.napmls_init(pKey, 32);
            }
        }
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static readonly byte[] EncryptionKey = new byte[32];

    [Fact]
    public async Task FullPipeline_KpExchange_Welcome_BidirectionalDecrypt()
    {
        // -- Arrange: two clients --
        await using var alice = MlsClient.Create("alice");
        await using var bob = MlsClient.Create("bob");

        // -- KP exchange --
        var kpBob = bob.GenerateKeyPackage();
        var kpAlice = alice.GenerateKeyPackage();

        Assert.NotEmpty(kpAlice);
        Assert.NotEmpty(kpBob);

        // -- A creates group and adds B --
        var groupA = alice.CreateGroup("E2E Test Group");
        var welcome = alice.AddMembers(groupA, [kpBob]);
        Assert.NotEmpty(welcome);

        // -- B processes Welcome --
        var groupB = bob.ProcessWelcome(welcome);
        Assert.NotNull(groupB);

        var countA = alice.GetMemberCount(groupA);
        var countB = bob.GetMemberCount(groupB);
        Assert.Equal(2u, countA);
        Assert.Equal(2u, countB);

        // -- Wire format round-trip: A → B --
        var plainA = "Hello from Alice!";
        var cipherA = alice.Encrypt(groupA, plainA);
        Assert.NotEmpty(cipherA);

        // Format as wire message — groupA IS the groupHash from CreateGroup
        var groupHashA = groupA;
        var epochA = (long)alice.GetEpoch(groupA);
        var senderA = 100001L;
        var wireMsgA = MessageChunker.FormatMessage(
            MlsMessageType.MSG, groupHashA, epochA, senderA, seq: 1, total: 1, cipherA);
        Assert.StartsWith("[MLS:MSG:", wireMsgA);

        // B parses wire message
        var parsedA = new MessageChunker().TryParse(wireMsgA, 0);
        Assert.NotNull(parsedA);
        Assert.Equal(groupHashA, parsedA.GroupHash);
        Assert.Equal(cipherA, parsedA.Ciphertext);

        // B decrypts
        var decryptedA = bob.Decrypt(groupB, parsedA.Ciphertext);
        Assert.Equal(plainA, decryptedA);

        // -- Wire format round-trip: B → A --
        var plainB = "Hi Alice, Bob here!";
        var cipherB = bob.Encrypt(groupB, plainB);
        Assert.NotEmpty(cipherB);

        var groupHashB = groupB;
        var epochB = (long)bob.GetEpoch(groupB);
        var senderB = 200001L;
        var wireMsgB = MessageChunker.FormatMessage(
            MlsMessageType.MSG, groupHashB, epochB, senderB, seq: 1, total: 1, cipherB);

        var parsedB = new MessageChunker().TryParse(wireMsgB, 0);
        Assert.NotNull(parsedB);
        Assert.Equal(cipherB, parsedB.Ciphertext);

        var decryptedB = alice.Decrypt(groupA, parsedB.Ciphertext);
        Assert.Equal(plainB, decryptedB);
    }

    [Fact]
    public async Task WelcomeMessage_WireFormat_ParsesCorrectly()
    {
        await using var alice = MlsClient.Create("alice");
        await using var bob = MlsClient.Create("bob");

        var kpBob = bob.GenerateKeyPackage();
        var groupA = alice.CreateGroup("Welcome Test");
        var welcome = alice.AddMembers(groupA, [kpBob]);

        // Format as [MLS:WELCOME:<qqGroupId>]base64
        var qqGroupId = 838221408L;
        var wireWelcome = $"[MLS:WELCOME:{qqGroupId}]{Convert.ToBase64String(welcome)}";
        Assert.StartsWith("[MLS:WELCOME:838221408]", wireWelcome);

        // Parse back
        var bracketEnd = wireWelcome.IndexOf(']');
        var header = wireWelcome[13..bracketEnd];
        Assert.Equal(qqGroupId.ToString(), header);

        var payload = wireWelcome[(bracketEnd + 1)..];
        var roundTripped = Convert.FromBase64String(payload);
        Assert.Equal(welcome, roundTripped);
    }

    [Fact]
    public async Task KeyPackage_WireFormat_ParsesCorrectly()
    {
        await using var bob = MlsClient.Create("bob");

        var kp = bob.GenerateKeyPackage();

        // Format as [MLS:KP:]base64
        var wireKp = $"[MLS:KP:]{Convert.ToBase64String(kp)}";
        Assert.StartsWith("[MLS:KP:]", wireKp);

        // Parse back
        var bracketEnd = wireKp.IndexOf(']');
        var payload = wireKp[(bracketEnd + 1)..];
        var roundTripped = Convert.FromBase64String(payload);
        Assert.Equal(kp, roundTripped);
    }

    [Fact]
    public async Task MessageBus_DeliversToFilteredSubscriber()
    {
        var bus = new MessageBus();
        var groupHash = "abcdef1234567890";
        var received = new List<MessageReceivedEvent>();

        using var sub = bus.Subscribe<MessageReceivedEvent>(evt =>
        {
            if (evt.GroupHash == groupHash)
                received.Add(evt);
        });

        // Publish matching
        bus.Publish(new MessageReceivedEvent
        {
            GroupHash = groupHash,
            Sender = 100001,
            Epoch = 1,
            Plaintext = System.Text.Encoding.UTF8.GetBytes("hello"),
        });

        // Publish non-matching
        bus.Publish(new MessageReceivedEvent
        {
            GroupHash = "0000000000000000",
            Sender = 200001,
            Epoch = 1,
            Plaintext = System.Text.Encoding.UTF8.GetBytes("other"),
        });

        Assert.Single(received);
        Assert.Equal("hello", System.Text.Encoding.UTF8.GetString(received[0].Plaintext));
    }

    [Fact]
    public async Task ThreeMembers_GroupCreate_AddAll_BidirectionalDecrypt()
    {
        await using var alice = MlsClient.Create("alice");
        await using var bob = MlsClient.Create("bob");
        await using var carol = MlsClient.Create("carol");

        var kpBob = bob.GenerateKeyPackage();
        var kpCarol = carol.GenerateKeyPackage();

        // A creates group, adds B and C
        var groupA = alice.CreateGroup("Three Member Group");
        var welcome = alice.AddMembers(groupA, [kpBob, kpCarol]);
        Assert.NotEmpty(welcome);

        // B and C process welcome
        var groupB = bob.ProcessWelcome(welcome);
        var groupC = carol.ProcessWelcome(welcome);
        Assert.NotNull(groupB);
        Assert.NotNull(groupC);

        // All see 3 members
        Assert.Equal(3u, alice.GetMemberCount(groupA));
        Assert.Equal(3u, bob.GetMemberCount(groupB));
        Assert.Equal(3u, carol.GetMemberCount(groupC));

        // A → B
        var msgAB = "Alice to Bob";
        var cipherAB = alice.Encrypt(groupA, msgAB);
        var plainAB = bob.Decrypt(groupB, cipherAB);
        Assert.Equal(msgAB, plainAB);

        // B → C
        var msgBC = "Bob to Carol";
        var cipherBC = bob.Encrypt(groupB, msgBC);
        var plainBC = carol.Decrypt(groupC, cipherBC);
        Assert.Equal(msgBC, plainBC);

        // C → A
        var msgCA = "Carol to Alice";
        var cipherCA = carol.Encrypt(groupC, msgCA);
        var plainCA = alice.Decrypt(groupA, cipherCA);
        Assert.Equal(msgCA, plainCA);
    }
}
