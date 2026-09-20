using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Xunit;
using static NapMLS.IntegrationTests.NativeMethods;

namespace NapMLS.IntegrationTests;

public class FfiAbiTests : IAsyncLifetime
{
    private static readonly byte[] _sharedKey = new byte[32];

    public Task InitializeAsync()
    {
        unsafe
        {
            fixed (byte* pKey = _sharedKey)
            {
                napmls_init(pKey, 32);
            }
        }
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public void StructLayout_NapMlsBytes_Is16Bytes()
    {
        Assert.Equal(16, Marshal.SizeOf<NapMlsBytes>());
    }

    [Fact]
    public void StructLayout_NapMlsError_Is16Bytes()
    {
        Assert.Equal(16, Marshal.SizeOf<NapMlsError>());
    }

    [Fact]
    public void StructLayout_NapMlsBytes_FieldOffsets()
    {
        Assert.Equal(0, (int)Marshal.OffsetOf<NapMlsBytes>("ptr"));
        Assert.Equal(8, (int)Marshal.OffsetOf<NapMlsBytes>("len"));
    }

    [Fact]
    public void StructLayout_NapMlsError_FieldOffsets()
    {
        Assert.Equal(0, (int)Marshal.OffsetOf<NapMlsError>("code"));
        Assert.Equal(8, (int)Marshal.OffsetOf<NapMlsError>("message"));
    }

    [Fact]
    public async Task StressTest_100Iterations_EncryptDecryptCycle()
    {
        var sw = Stopwatch.StartNew();

        for (int i = 0; i < 100; i++)
        {
            await using var alice = await CreateClientAsync($"Alice{i}");
            await using var bob = await CreateClientAsync($"Bob{i}");

            var kp = bob.GenerateKeyPackage();
            var groupA = alice.CreateGroup($"G{i}");
            var welcome = alice.AddMembers(groupA, [kp]);
            var groupB = bob.ProcessWelcome(welcome);

            var cipher = alice.Encrypt(groupA, $"msg{i}");
            var plain = bob.Decrypt(groupB, cipher);
            Assert.Equal($"msg{i}", plain);
        }

        sw.Stop();
        Debug.WriteLine($"100-iteration stress test: {sw.ElapsedMilliseconds}ms");
    }

    private static async Task<MlsClient> CreateClientAsync(string name)
    {
        return await Task.FromResult(MlsClient.Create(name));
    }
}
