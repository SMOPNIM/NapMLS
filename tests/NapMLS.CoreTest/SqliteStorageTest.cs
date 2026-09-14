using Microsoft.Data.Sqlite;
using NapMLS.Core;
using Xunit;

namespace NapMLS.CoreTest;

public class SqliteStorageTest : IDisposable
{
    private readonly SqliteStorage _storage;

    public SqliteStorageTest()
    {
        _storage = new SqliteStorage(":memory:");
    }

    public void Dispose()
    {
        _storage.Dispose();
    }

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private static byte[] RandomBytes(int len)
    {
        var buf = new byte[len];
        Random.Shared.NextBytes(buf);
        return buf;
    }

    // ===== Trusted Peers =====

    [Fact]
    public void UpsertPeer_InsertAndRetrieve()
    {
        var peer = new TrustedPeer
        {
            QqNumber = "12345678",
            Fingerprint = RandomBytes(8),
            SafetyCode = "NAPMLS-A1B2-C3D4-E5F6-7890",
            Nickname = "张三",
            CreatedAt = Now(),
        };

        _storage.UpsertPeer(peer);
        var loaded = _storage.GetPeer("12345678");

        Assert.NotNull(loaded);
        Assert.Equal("12345678", loaded!.QqNumber);
        Assert.Equal("张三", loaded.Nickname);
        Assert.Equal("NAPMLS-A1B2-C3D4-E5F6-7890", loaded.SafetyCode);
        Assert.False(loaded.IsVerified);
    }

    [Fact]
    public void UpsertPeer_ReplaceOnDuplicate()
    {
        var peer1 = new TrustedPeer
        {
            QqNumber = "11111111",
            Fingerprint = RandomBytes(8),
            SafetyCode = "NAPMLS-0000-0000-0000-0000",
            CreatedAt = Now(),
        };
        var peer2 = new TrustedPeer
        {
            QqNumber = "11111111",
            Fingerprint = RandomBytes(8),
            SafetyCode = "NAPMLS-FFFF-FFFF-FFFF-FFFF",
            Nickname = "新昵称",
            CreatedAt = Now(),
        };

        _storage.UpsertPeer(peer1);
        _storage.UpsertPeer(peer2);

        var loaded = _storage.GetPeer("11111111");
        Assert.NotNull(loaded);
        Assert.Equal("NAPMLS-FFFF-FFFF-FFFF-FFFF", loaded!.SafetyCode);
        Assert.Equal("新昵称", loaded.Nickname);
    }

    [Fact]
    public void ListPeers_ReturnsAll()
    {
        for (int i = 0; i < 3; i++)
        {
            _storage.UpsertPeer(new TrustedPeer
            {
                QqNumber = $"1000000{i}",
                Fingerprint = RandomBytes(8),
                SafetyCode = $"NAPMLS-000{i}-0000-0000-0000",
                CreatedAt = Now() + i,
            });
        }

        var peers = _storage.ListPeers();
        Assert.Equal(3, peers.Count);
        // Ordered by created_at DESC
        Assert.Equal("10000002", peers[0].QqNumber);
    }

    [Fact]
    public void SetVerified_MarksVerified()
    {
        _storage.UpsertPeer(new TrustedPeer
        {
            QqNumber = "22222222",
            Fingerprint = RandomBytes(8),
            SafetyCode = "NAPMLS-1111-1111-1111-1111",
            CreatedAt = Now(),
        });

        Assert.False(_storage.GetPeer("22222222")!.IsVerified);

        _storage.SetVerified("22222222", Now());
        Assert.True(_storage.GetPeer("22222222")!.IsVerified);
    }

    [Fact]
    public void ClearVerification_Unverifies()
    {
        _storage.UpsertPeer(new TrustedPeer
        {
            QqNumber = "33333333",
            Fingerprint = RandomBytes(8),
            SafetyCode = "NAPMLS-2222-2222-2222-2222",
            CreatedAt = Now(),
        });
        _storage.SetVerified("33333333", Now());
        Assert.True(_storage.GetPeer("33333333")!.IsVerified);

        _storage.ClearVerification("33333333");
        Assert.False(_storage.GetPeer("33333333")!.IsVerified);
    }

    [Fact]
    public void SetKeyPackage_StoresBlob()
    {
        _storage.UpsertPeer(new TrustedPeer
        {
            QqNumber = "44444444",
            Fingerprint = RandomBytes(8),
            SafetyCode = "NAPMLS-3333-3333-3333-3333",
            CreatedAt = Now(),
        });

        var kp = RandomBytes(256);
        _storage.SetKeyPackage("44444444", kp);

        var loaded = _storage.GetPeer("44444444");
        Assert.NotNull(loaded!.KeyPackage);
        Assert.Equal(kp, loaded.KeyPackage);
    }

    [Fact]
    public void DeletePeer_Removes()
    {
        _storage.UpsertPeer(new TrustedPeer
        {
            QqNumber = "55555555",
            Fingerprint = RandomBytes(8),
            SafetyCode = "NAPMLS-4444-4444-4444-4444",
            CreatedAt = Now(),
        });

        _storage.DeletePeer("55555555");
        Assert.Null(_storage.GetPeer("55555555"));
        Assert.Empty(_storage.ListPeers());
    }

    [Fact]
    public void GetPeer_NotFound_ReturnsNull()
    {
        Assert.Null(_storage.GetPeer("nonexistent"));
    }

    // ===== Group Bindings =====

    [Fact]
    public void UpsertBinding_InsertAndRetrieve()
    {
        var binding = new GroupBinding
        {
            GroupId = RandomBytes(16),
            QqGroupId = "99999",
            DisplayName = "测试群",
            SortOrder = 0,
        };

        _storage.UpsertGroupBinding(binding);
        var loaded = _storage.GetGroupBinding(binding.GroupId);

        Assert.NotNull(loaded);
        Assert.Equal("99999", loaded!.QqGroupId);
        Assert.Equal("测试群", loaded.DisplayName);
    }

    [Fact]
    public void UpsertBinding_ReplaceOnDuplicate()
    {
        var gid = RandomBytes(16);

        _storage.UpsertGroupBinding(new GroupBinding
        {
            GroupId = gid,
            QqGroupId = "11111",
            DisplayName = "旧名称",
        });

        _storage.UpsertGroupBinding(new GroupBinding
        {
            GroupId = gid,
            QqGroupId = "22222",
            DisplayName = "新名称",
        });

        var loaded = _storage.GetGroupBinding(gid);
        Assert.NotNull(loaded);
        Assert.Equal("22222", loaded!.QqGroupId);
        Assert.Equal("新名称", loaded.DisplayName);
    }

    [Fact]
    public void ListBindings_OrderBySortOrder()
    {
        _storage.UpsertGroupBinding(new GroupBinding
        {
            GroupId = RandomBytes(16), QqGroupId = "1", DisplayName = "B群", SortOrder = 2,
        });
        _storage.UpsertGroupBinding(new GroupBinding
        {
            GroupId = RandomBytes(16), QqGroupId = "2", DisplayName = "A群", SortOrder = 1,
        });

        var bindings = _storage.ListGroupBindings();
        Assert.Equal(2, bindings.Count);
        Assert.Equal("A群", bindings[0].DisplayName);
        Assert.Equal("B群", bindings[1].DisplayName);
    }

    [Fact]
    public void DeleteBinding_Removes()
    {
        var gid = RandomBytes(16);
        _storage.UpsertGroupBinding(new GroupBinding
        {
            GroupId = gid, QqGroupId = "1", DisplayName = "测试",
        });

        _storage.DeleteGroupBinding(gid);
        Assert.Null(_storage.GetGroupBinding(gid));
    }

    // ===== Persistence (file-backed) =====

    [Fact]
    public void Persistence_FileBacked()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"napmls_test_{Guid.NewGuid():N}.db");
        try
        {
            // Write
            var fp = RandomBytes(8);
            using (var db = new SqliteStorage(dbPath))
            {
                db.UpsertPeer(new TrustedPeer
                {
                    QqNumber = "persist_test",
                    Fingerprint = fp,
                    SafetyCode = "NAPMLS-PERSIST-TEST-TEST-TEST",
                    CreatedAt = Now(),
                });
            }

            // Force checkpoint and close WAL
            SqliteConnection.ClearAllPools();

            // Read from new instance
            using (var db = new SqliteStorage(dbPath))
            {
                var loaded = db.GetPeer("persist_test");
                Assert.NotNull(loaded);
                Assert.Equal("NAPMLS-PERSIST-TEST-TEST-TEST", loaded!.SafetyCode);
                Assert.Equal(fp, loaded.Fingerprint);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { File.Delete(dbPath); } catch { }
            try { File.Delete(dbPath + "-wal"); } catch { }
            try { File.Delete(dbPath + "-shm"); } catch { }
        }
    }
}
