using Microsoft.Data.Sqlite;

namespace NapMLS.Core;

/// <summary>
/// P1d-3b: Application-layer SQLite storage for trusted peers and group bindings.
/// This is separate from the Rust-side MLS storage (napmls_data.db).
/// Stores UI metadata and trust relationships — no secrets.
/// </summary>
public sealed class SqliteStorage : IDisposable
{
    private readonly SqliteConnection _conn;

    public SqliteStorage(string dbPath)
    {
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        _conn = new SqliteConnection($"Data Source={dbPath}");
        _conn.Open();
        RunMigrations();
    }

    public SqliteStorage(SqliteConnection conn)
    {
        _conn = conn;
        _conn.Open();
        RunMigrations();
    }

    private void RunMigrations()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS trusted_peers (
                qq_number     TEXT PRIMARY KEY,
                fingerprint   BLOB NOT NULL,
                safety_code   TEXT NOT NULL,
                nickname      TEXT,
                key_package   BLOB,
                verified_at   INTEGER,
                created_at    INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS group_bindings (
                group_id      BLOB PRIMARY KEY,
                qq_group_id   TEXT NOT NULL,
                display_name  TEXT NOT NULL,
                last_update_at INTEGER,
                sort_order    INTEGER DEFAULT 0
            );
        ";
        cmd.ExecuteNonQuery();
    }

    // ===== Trusted Peers =====

    public void UpsertPeer(TrustedPeer peer)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
            INSERT OR REPLACE INTO trusted_peers
                (qq_number, fingerprint, safety_code, nickname, key_package, verified_at, created_at)
            VALUES
                (@qq, @fp, @sc, @nick, @kp, @verified, @created)
        ";
        cmd.Parameters.AddWithValue("@qq", peer.QqNumber);
        cmd.Parameters.AddWithValue("@fp", peer.Fingerprint);
        cmd.Parameters.AddWithValue("@sc", peer.SafetyCode);
        cmd.Parameters.AddWithValue("@nick", (object?)peer.Nickname ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@kp", (object?)peer.KeyPackage ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@verified", peer.VerifiedAt.HasValue
            ? peer.VerifiedAt.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@created", peer.CreatedAt);
        cmd.ExecuteNonQuery();
    }

    public TrustedPeer? GetPeer(string qqNumber)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM trusted_peers WHERE qq_number = @qq";
        cmd.Parameters.AddWithValue("@qq", qqNumber);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadPeer(reader) : null;
    }

    public List<TrustedPeer> ListPeers()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM trusted_peers ORDER BY created_at DESC";
        using var reader = cmd.ExecuteReader();
        var result = new List<TrustedPeer>();
        while (reader.Read())
            result.Add(ReadPeer(reader));
        return result;
    }

    public void SetKeyPackage(string qqNumber, byte[] keyPackage)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "UPDATE trusted_peers SET key_package = @kp WHERE qq_number = @qq";
        cmd.Parameters.AddWithValue("@kp", keyPackage);
        cmd.Parameters.AddWithValue("@qq", qqNumber);
        cmd.ExecuteNonQuery();
    }

    public void SetVerified(string qqNumber, long verifiedAt)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "UPDATE trusted_peers SET verified_at = @v WHERE qq_number = @qq";
        cmd.Parameters.AddWithValue("@v", verifiedAt);
        cmd.Parameters.AddWithValue("@qq", qqNumber);
        cmd.ExecuteNonQuery();
    }

    public void ClearVerification(string qqNumber)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "UPDATE trusted_peers SET verified_at = NULL, key_package = NULL WHERE qq_number = @qq";
        cmd.Parameters.AddWithValue("@qq", qqNumber);
        cmd.ExecuteNonQuery();
    }

    public void DeletePeer(string qqNumber)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM trusted_peers WHERE qq_number = @qq";
        cmd.Parameters.AddWithValue("@qq", qqNumber);
        cmd.ExecuteNonQuery();
    }

    // ===== Group Bindings =====

    public void UpsertGroupBinding(GroupBinding binding)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
            INSERT OR REPLACE INTO group_bindings
                (group_id, qq_group_id, display_name, last_update_at, sort_order)
            VALUES
                (@gid, @qq, @name, @updated, @sort)
        ";
        cmd.Parameters.AddWithValue("@gid", binding.GroupId);
        cmd.Parameters.AddWithValue("@qq", binding.QqGroupId);
        cmd.Parameters.AddWithValue("@name", binding.DisplayName);
        cmd.Parameters.AddWithValue("@updated", binding.LastUpdateAt.HasValue
            ? binding.LastUpdateAt.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@sort", binding.SortOrder);
        cmd.ExecuteNonQuery();
    }

    public GroupBinding? GetGroupBinding(byte[] groupId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM group_bindings WHERE group_id = @gid";
        cmd.Parameters.AddWithValue("@gid", groupId);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadBinding(reader) : null;
    }

    public List<GroupBinding> ListGroupBindings()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM group_bindings ORDER BY sort_order, display_name";
        using var reader = cmd.ExecuteReader();
        var result = new List<GroupBinding>();
        while (reader.Read())
            result.Add(ReadBinding(reader));
        return result;
    }

    public void UpdateGroupBindingSortOrder(byte[] groupId, int sortOrder)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "UPDATE group_bindings SET sort_order = @sort WHERE group_id = @gid";
        cmd.Parameters.AddWithValue("@sort", sortOrder);
        cmd.Parameters.AddWithValue("@gid", groupId);
        cmd.ExecuteNonQuery();
    }

    public void DeleteGroupBinding(byte[] groupId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM group_bindings WHERE group_id = @gid";
        cmd.Parameters.AddWithValue("@gid", groupId);
        cmd.ExecuteNonQuery();
    }

    // ===== Helpers =====

    private static TrustedPeer ReadPeer(SqliteDataReader reader)
    {
        return new TrustedPeer
        {
            QqNumber = reader.GetString(reader.GetOrdinal("qq_number")),
            Fingerprint = GetBytes(reader, "fingerprint"),
            SafetyCode = reader.GetString(reader.GetOrdinal("safety_code")),
            Nickname = reader.IsDBNull(reader.GetOrdinal("nickname"))
                ? null : reader.GetString(reader.GetOrdinal("nickname")),
            KeyPackage = reader.IsDBNull(reader.GetOrdinal("key_package"))
                ? null : GetBytes(reader, "key_package"),
            VerifiedAt = reader.IsDBNull(reader.GetOrdinal("verified_at"))
                ? null : reader.GetInt64(reader.GetOrdinal("verified_at")),
            CreatedAt = reader.GetInt64(reader.GetOrdinal("created_at")),
        };
    }

    private static GroupBinding ReadBinding(SqliteDataReader reader)
    {
        return new GroupBinding
        {
            GroupId = GetBytes(reader, "group_id"),
            QqGroupId = reader.GetString(reader.GetOrdinal("qq_group_id")),
            DisplayName = reader.GetString(reader.GetOrdinal("display_name")),
            LastUpdateAt = reader.IsDBNull(reader.GetOrdinal("last_update_at"))
                ? null : reader.GetInt64(reader.GetOrdinal("last_update_at")),
            SortOrder = reader.GetInt32(reader.GetOrdinal("sort_order")),
        };
    }

    private static byte[] GetBytes(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        using var stream = reader.GetStream(ordinal);
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    public void Dispose()
    {
        _conn.Dispose();
    }
}

public sealed class TrustedPeer
{
    public required string QqNumber { get; init; }
    public required byte[] Fingerprint { get; init; }
    public required string SafetyCode { get; init; }
    public string? Nickname { get; init; }
    public byte[]? KeyPackage { get; init; }
    public long? VerifiedAt { get; init; }
    public required long CreatedAt { get; init; }

    public bool IsVerified => VerifiedAt.HasValue;
}

public sealed class GroupBinding
{
    public required byte[] GroupId { get; init; }
    public required string QqGroupId { get; init; }
    public required string DisplayName { get; init; }
    public long? LastUpdateAt { get; init; }
    public int SortOrder { get; init; }
}
