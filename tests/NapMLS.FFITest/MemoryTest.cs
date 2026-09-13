using System.Diagnostics;
using System.Runtime.InteropServices;

namespace NapMLS.FFITest;

internal static class MemoryTest
{
    public static unsafe void Run()
    {
        Console.WriteLine("=== NapMLS Memory Leak Test ===\n");

        // Initialize
        var key = new byte[32];
        Random.Shared.NextBytes(key);
        fixed (byte* pKey = key)
        {
            NativeMethods.napmls_init(pKey, 32);
        }

        // ===== Test 1: Basic alloc/free balance =====
        Console.WriteLine("--- Test 1: Alloc/Free Balance (100 cycles) ---");
        NativeMethods.napmls_reset_counters();

        for (int i = 0; i < 100; i++)
        {
            var provider = NativeMethods.napmls_provider_new(null);
            IntPtr identity, group;
            var name = System.Text.Encoding.UTF8.GetBytes("TestUser");
            fixed (byte* pName = name)
            {
                NativeMethods.napmls_create_identity(provider, pName, name.Length, &identity, null);
            }
            NativeMethods.napmls_create_group(provider, identity, &group, null);

            NativeMethods.NapMlsBytes kp = default;
            NativeMethods.napmls_generate_key_package(provider, identity, &kp, null);
            NativeMethods.napmls_free_bytes(kp);

            var msgBytes = System.Text.Encoding.UTF8.GetBytes("Hello World!");
            NativeMethods.NapMlsBytes cipher = default;
            fixed (byte* pMsg = msgBytes)
            {
                NativeMethods.napmls_encrypt(provider, group, identity, pMsg, msgBytes.Length, &cipher, null);
            }
            NativeMethods.NapMlsBytes plain = default;
            NativeMethods.napmls_decrypt(provider, group, (byte*)cipher.ptr, cipher.len, &plain, null);
            NativeMethods.napmls_free_bytes(cipher);
            NativeMethods.napmls_free_bytes(plain);

            NativeMethods.napmls_group_free(group);
            NativeMethods.napmls_identity_free(identity);
            NativeMethods.napmls_provider_free(provider);
        }

        nuint allocCount, freeCount, bytesAlloc, bytesFreed;
        NativeMethods.napmls_get_alloc_stats(&allocCount, &freeCount, &bytesAlloc, &bytesFreed);
        Console.WriteLine($"  Alloc count:  {allocCount}");
        Console.WriteLine($"  Free count:   {freeCount}");
        Console.WriteLine($"  Bytes alloc:  {bytesAlloc:N0}");
        Console.WriteLine($"  Bytes freed:  {bytesFreed:N0}");
        Console.WriteLine($"  Balance:      alloc={allocCount - freeCount}, bytes={bytesAlloc - bytesFreed}");
        Console.WriteLine($"  Status:       {(allocCount == freeCount ? "✅ BALANCED" : "❌ LEAK DETECTED")}");

        // ===== Test 2: GC + Process Memory (1000 iterations) =====
        Console.WriteLine("\n--- Test 2: GC + Process Memory (1000 iterations) ---");
        NativeMethods.napmls_reset_counters();

        var sw = Stopwatch.StartNew();
        long[] memSnapshots = new long[11];
        Process proc = Process.GetCurrentProcess();

        for (int i = 0; i <= 1000; i++)
        {
            if (i % 100 == 0)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                memSnapshots[i / 100] = proc.PrivateMemorySize64;
                Console.WriteLine($"  [{i,5}] GC={GC.GetTotalMemory(true),12:N0}  PrivateMem={memSnapshots[i / 100],12:N0}");
            }

            var provider = NativeMethods.napmls_provider_new(null);
            IntPtr identity, group;
            var name = System.Text.Encoding.UTF8.GetBytes("StressUser");
            fixed (byte* pName = name)
            {
                NativeMethods.napmls_create_identity(provider, pName, name.Length, &identity, null);
            }
            NativeMethods.napmls_create_group(provider, identity, &group, null);

            var msgBytes = System.Text.Encoding.UTF8.GetBytes("stress test message");
            NativeMethods.NapMlsBytes cipher = default;
            fixed (byte* pMsg = msgBytes)
            {
                NativeMethods.napmls_encrypt(provider, group, identity, pMsg, msgBytes.Length, &cipher, null);
            }
            NativeMethods.NapMlsBytes plain = default;
            NativeMethods.napmls_decrypt(provider, group, (byte*)cipher.ptr, cipher.len, &plain, null);
            NativeMethods.napmls_free_bytes(cipher);
            NativeMethods.napmls_free_bytes(plain);

            NativeMethods.napmls_group_free(group);
            NativeMethods.napmls_identity_free(identity);
            NativeMethods.napmls_provider_free(provider);
        }

        sw.Stop();

        NativeMethods.napmls_get_alloc_stats(&allocCount, &freeCount, &bytesAlloc, &bytesFreed);
        Console.WriteLine($"\n  Time: {sw.ElapsedMilliseconds}ms");
        Console.WriteLine($"  Alloc count:  {allocCount}");
        Console.WriteLine($"  Free count:   {freeCount}");
        Console.WriteLine($"  Bytes alloc:  {bytesAlloc:N0}");
        Console.WriteLine($"  Bytes freed:  {bytesFreed:N0}");
        Console.WriteLine($"  Balance:      alloc={allocCount - freeCount}, bytes={bytesAlloc - bytesFreed}");

        long memGrowth = memSnapshots[10] - memSnapshots[0];
        Console.WriteLine($"  Memory growth: {memGrowth:N0} bytes ({memGrowth / 1024.0:F1} KB)");
        Console.WriteLine($"  Status:        {(allocCount == freeCount && Math.Abs(memGrowth) < 1024 * 100 ? "✅ NO LEAK" : "⚠️ CHECK NEEDED")}");

        // ===== Test 3: Multi-provider with same key =====
        Console.WriteLine("\n--- Test 3: Multi-Provider with Same Key ---");
        NativeMethods.napmls_reset_counters();

        var p1 = NativeMethods.napmls_provider_new(null);
        var p2 = NativeMethods.napmls_provider_new(null);

        IntPtr id1, id2;
        var n1 = System.Text.Encoding.UTF8.GetBytes("User1");
        var n2 = System.Text.Encoding.UTF8.GetBytes("User2");
        fixed (byte* pn1 = n1, pn2 = n2)
        {
            NativeMethods.napmls_create_identity(p1, pn1, n1.Length, &id1, null);
            NativeMethods.napmls_create_identity(p2, pn2, n2.Length, &id2, null);
        }

        IntPtr g1, g2;
        NativeMethods.napmls_create_group(p1, id1, &g1, null);
        NativeMethods.napmls_create_group(p2, id2, &g2, null);

        NativeMethods.NapMlsBytes kp2 = default;
        NativeMethods.napmls_generate_key_package(p2, id2, &kp2, null);
        var kpData = NativeMethods.ReadBytes(kp2);
        NativeMethods.napmls_free_bytes(kp2);

        NativeMethods.NapMlsBytes welcome = default;
        fixed (byte* pKp = kpData)
        {
            NativeMethods.napmls_add_members(p1, g1, id1, pKp, kpData.Length, &welcome, null);
        }
        Console.WriteLine($"  Cross-provider add: welcome={welcome.len} bytes");
        NativeMethods.napmls_free_bytes(welcome);

        var msg1 = System.Text.Encoding.UTF8.GetBytes("from p1");
        var msg2 = System.Text.Encoding.UTF8.GetBytes("from p2");
        NativeMethods.NapMlsBytes c1 = default, c2 = default, pl1 = default, pl2 = default;

        fixed (byte* m1 = msg1) { NativeMethods.napmls_encrypt(p1, g1, id1, m1, msg1.Length, &c1, null); }
        fixed (byte* m2 = msg2) { NativeMethods.napmls_encrypt(p2, g2, id2, m2, msg2.Length, &c2, null); }

        NativeMethods.napmls_decrypt(p1, g1, (byte*)c1.ptr, c1.len, &pl1, null);
        NativeMethods.napmls_decrypt(p2, g2, (byte*)c2.ptr, c2.len, &pl2, null);

        var r1 = System.Text.Encoding.UTF8.GetString(NativeMethods.ReadBytes(pl1));
        var r2 = System.Text.Encoding.UTF8.GetString(NativeMethods.ReadBytes(pl2));
        Console.WriteLine($"  Provider 1 decrypt: \"{r1}\"");
        Console.WriteLine($"  Provider 2 decrypt: \"{r2}\"");

        NativeMethods.napmls_free_bytes(c1); NativeMethods.napmls_free_bytes(c2);
        NativeMethods.napmls_free_bytes(pl1); NativeMethods.napmls_free_bytes(pl2);
        NativeMethods.napmls_group_free(g1); NativeMethods.napmls_group_free(g2);
        NativeMethods.napmls_identity_free(id1); NativeMethods.napmls_identity_free(id2);
        NativeMethods.napmls_provider_free(p1); NativeMethods.napmls_provider_free(p2);

        NativeMethods.napmls_get_alloc_stats(&allocCount, &freeCount, &bytesAlloc, &bytesFreed);
        Console.WriteLine($"  Balance: alloc={allocCount - freeCount}, bytes={bytesAlloc - bytesFreed}");
        Console.WriteLine($"  Multi-provider: {(allocCount == freeCount ? "✅ OK" : "❌ LEAK")}");

        Console.WriteLine("\n✅ Memory leak tests complete!");
    }
}
