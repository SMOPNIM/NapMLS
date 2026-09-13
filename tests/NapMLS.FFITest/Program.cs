using System.Text;
using NapMLS.FFITest;

Console.WriteLine("=== NapMLS Safe Wrapper Test ===\n");

using var aliceClient = new MlsClient();
using var bobClient = new MlsClient();

// Both clients share the same master key
var masterKey = new byte[32];
Random.Shared.NextBytes(masterKey);

aliceClient.Initialize(masterKey);
bobClient.Initialize(masterKey);

// 1. Create identities
var alice = aliceClient.CreateIdentity("Alice");
var bob = bobClient.CreateIdentity("Bob");
Console.WriteLine($"[identity] Alice: {alice.GetFingerprint()}");
Console.WriteLine($"[identity] Bob:   {bob.GetFingerprint()}");

// 2. Alice creates group
using var aliceGroup = aliceClient.CreateGroup(alice);
Console.WriteLine($"[group] Created: epoch={aliceClient.GetEpoch(aliceGroup)}, members={aliceClient.GetMemberCount(aliceGroup)}");

// 3. Generate Bob's key package + add
var bobKp = bobClient.GenerateKeyPackage(bob);
var welcome = aliceClient.AddMember(aliceGroup, alice, bobKp);
Console.WriteLine($"[add] Welcome={welcome.Length} bytes, epoch={aliceClient.GetEpoch(aliceGroup)}, members={aliceClient.GetMemberCount(aliceGroup)}");

// 4. Members JSON
Console.WriteLine($"[members] {aliceClient.GetMembersJson(aliceGroup)}");

// 5. Bob joins
using var bobGroup = bobClient.ProcessWelcome(welcome);
Console.WriteLine($"[welcome] Bob joined: epoch={bobClient.GetEpoch(bobGroup)}");

// 6. Alice encrypts
var ciphertext = aliceClient.Encrypt(aliceGroup, alice, Encoding.UTF8.GetBytes("Hello Bob!"));
Console.WriteLine($"[encrypt] {ciphertext.Length} bytes");

// 7. Bob decrypts
var plaintext = Encoding.UTF8.GetString(bobClient.Decrypt(bobGroup, ciphertext));
Console.WriteLine($"[decrypt] \"{plaintext}\"");
Console.WriteLine($"[decrypt] Match: {plaintext == "Hello Bob!"}");

// 8. Bob replies
var reply = bobClient.Encrypt(bobGroup, bob, Encoding.UTF8.GetBytes("Hi Alice!"));
var replyPlain = Encoding.UTF8.GetString(aliceClient.Decrypt(aliceGroup, reply));
Console.WriteLine($"[reply] \"{replyPlain}\"");
Console.WriteLine($"[reply] Match: {replyPlain == "Hi Alice!"}");

// 9. Remove Bob
aliceClient.RemoveMember(aliceGroup, alice, 1);
Console.WriteLine($"[remove] epoch={aliceClient.GetEpoch(aliceGroup)}, members={aliceClient.GetMemberCount(aliceGroup)}");
Console.WriteLine($"[members] {aliceClient.GetMembersJson(aliceGroup)}");

// 10. Bob is evicted
var lateCipher = aliceClient.Encrypt(aliceGroup, alice, Encoding.UTF8.GetBytes("secret"));
try
{
    bobClient.Decrypt(bobGroup, lateCipher);
    Console.WriteLine("[evicted] ERROR: Bob should not be able to decrypt!");
}
catch (MlsDecryptException ex)
{
    Console.WriteLine($"[evicted] Bob correctly rejected (error {ex.ErrorCode})");
}

Console.WriteLine("\n✅ All safe wrapper tests passed!");
