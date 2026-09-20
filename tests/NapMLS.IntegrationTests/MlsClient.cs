using System.Runtime.InteropServices;
using System.Text;

namespace NapMLS.IntegrationTests;

/// <summary>
/// High-level MLS client wrapping the FFI.
/// Each MlsClient owns its own provider + identity.
/// Thread-safe via SemaphoreSlim(1,1).
/// </summary>
public sealed class MlsClient : IAsyncDisposable
{
    private readonly IntPtr _provider;
    private readonly IntPtr _identity;
    private readonly string _name;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Dictionary<string, IntPtr> _groups = new(); // groupHash → group handle
    private bool _disposed;

    public string Name => _name;
    public IntPtr Provider => _provider;
    public IntPtr Identity => _identity;

    private MlsClient(string name, IntPtr provider, IntPtr identity)
    {
        _name = name;
        _provider = provider;
        _identity = identity;
    }

    /// <summary>Create a new MlsClient with its own provider and identity.</summary>
    public static unsafe MlsClient Create(string name)
    {
        var provider = NativeMethods.napmls_provider_new(null);
        if (provider == IntPtr.Zero)
            throw new InvalidOperationException($"Failed to create provider for {name}");

        var nameBytes = Encoding.UTF8.GetBytes(name);
        IntPtr identity;
        fixed (byte* pName = nameBytes)
        {
            var rc = NativeMethods.napmls_create_identity(
                provider, pName, (nuint)nameBytes.Length, &identity, null);
            if (rc != NativeMethods.NAPMLS_OK || identity == IntPtr.Zero)
                throw new InvalidOperationException($"Failed to create identity for {name}");
        }

        return new MlsClient(name, provider, identity);
    }

    /// <summary>Create a file-backed MlsClient. Creates provider from dbPath.</summary>
    public static unsafe MlsClient CreateFromFile(string name, string dbPath)
    {
        var dbPathBytes = Encoding.UTF8.GetBytes(dbPath);
        IntPtr provider;
        fixed (byte* pDbPath = dbPathBytes)
        {
            provider = NativeMethods.napmls_provider_new_from_file(pDbPath, null);
        }
        if (provider == IntPtr.Zero)
            throw new InvalidOperationException($"Failed to create provider from {dbPath}");

        var nameBytes = Encoding.UTF8.GetBytes(name);
        IntPtr identity;
        fixed (byte* pName = nameBytes)
        {
            var rc = NativeMethods.napmls_create_identity(
                provider, pName, (nuint)nameBytes.Length, &identity, null);
            if (rc != NativeMethods.NAPMLS_OK || identity == IntPtr.Zero)
                throw new InvalidOperationException($"Failed to create identity for {name}");
        }

        return new MlsClient(name, provider, identity);
    }

    /// <summary>Load an existing identity by username from a file-backed provider.
    /// Returns null if not found.</summary>
    public static unsafe MlsClient? LoadFromFile(string name, string dbPath)
    {
        var dbPathBytes = Encoding.UTF8.GetBytes(dbPath);
        IntPtr provider;
        fixed (byte* pDbPath = dbPathBytes)
        {
            provider = NativeMethods.napmls_provider_new_from_file(pDbPath, null);
        }
        if (provider == IntPtr.Zero)
            throw new InvalidOperationException($"Failed to create provider from {dbPath}");

        var nameBytes = Encoding.UTF8.GetBytes(name);
        IntPtr identity;
        fixed (byte* pName = nameBytes)
        {
            var rc = NativeMethods.napmls_load_identity(
                provider, pName, (nuint)nameBytes.Length, &identity, null);
            if (rc != NativeMethods.NAPMLS_OK || identity == IntPtr.Zero)
            {
                NativeMethods.napmls_provider_free(provider);
                return null;
            }
        }

        return new MlsClient(name, provider, identity);
    }

    /// <summary>Register this identity in the identity registry (enables future LoadFromFile).</summary>
    public unsafe void RegisterIdentity()
    {
        var nameBytes = Encoding.UTF8.GetBytes(_name);
        fixed (byte* pName = nameBytes)
        {
            var rc = NativeMethods.napmls_register_identity(
                _provider, pName, (nuint)nameBytes.Length, _identity, null);
            if (rc != NativeMethods.NAPMLS_OK)
                throw new InvalidOperationException($"Failed to register identity: {rc}");
        }
    }

    /// <summary>Get this client's identity fingerprint.</summary>
    public unsafe string GetFingerprint()
    {
        NativeMethods.NapMlsBytes fp = default;
        var rc = NativeMethods.napmls_identity_fingerprint(_identity, &fp, null);
        if (rc != NativeMethods.NAPMLS_OK)
            throw new InvalidOperationException($"Failed to get fingerprint: {rc}");
        var result = Encoding.UTF8.GetString(NativeMethods.ReadBytes(fp));
        NativeMethods.napmls_free_bytes(fp);
        return result;
    }

    /// <summary>Generate a KeyPackage for this client.</summary>
    public unsafe byte[] GenerateKeyPackage()
    {
        NativeMethods.NapMlsBytes kp = default;
        var rc = NativeMethods.napmls_generate_key_package(_provider, _identity, &kp, null);
        if (rc != NativeMethods.NAPMLS_OK)
            throw new InvalidOperationException($"Failed to generate key package: {rc}");
        var data = NativeMethods.ReadBytes(kp);
        NativeMethods.napmls_free_bytes(kp);
        return data;
    }

    /// <summary>Create a new MLS group. Returns groupHash.</summary>
    public unsafe string CreateGroup(string name = "default")
    {
        IntPtr group;
        var nameBytes = Encoding.UTF8.GetBytes(name);
        fixed (byte* pName = nameBytes)
        {
            var rc = NativeMethods.napmls_create_group(
                _provider, _identity, pName, (nuint)nameBytes.Length, &group, null);
            if (rc != NativeMethods.NAPMLS_OK || group == IntPtr.Zero)
                throw new InvalidOperationException($"Failed to create group: {rc}");
        }

        var groupHash = GetGroupHash(group);
        _groups[groupHash] = group;
        return groupHash;
    }

    /// <summary>Load an existing group by its raw group_id bytes.</summary>
    public unsafe string? LoadGroup(byte[] groupId)
    {
        IntPtr group;
        fixed (byte* pId = groupId)
        {
            var rc = NativeMethods.napmls_load_group(
                _provider, pId, (nuint)groupId.Length, &group, null);
            if (rc != NativeMethods.NAPMLS_OK || group == IntPtr.Zero)
                return null;
        }

        var groupHash = GetGroupHash(group);
        _groups[groupHash] = group;
        return groupHash;
    }

    /// <summary>List all groups in the registry. Returns JSON array.</summary>
    public unsafe string ListGroups()
    {
        NativeMethods.NapMlsBytes groups = default;
        var rc = NativeMethods.napmls_list_groups(_provider, &groups, null);
        if (rc != NativeMethods.NAPMLS_OK)
            throw new InvalidOperationException($"Failed to list groups: {rc}");
        var json = Encoding.UTF8.GetString(NativeMethods.ReadBytes(groups));
        NativeMethods.napmls_free_bytes(groups);
        return json;
    }

    /// <summary>Add members to a group by their key packages. Returns welcome bytes.
    /// Wire format: [count:4][len1:4][kp1..][len2:4][kp2..]...</summary>
    public unsafe byte[] AddMembers(string groupHash, byte[][] keyPackages)
    {
        if (!_groups.TryGetValue(groupHash, out var group))
            throw new KeyNotFoundException($"Group {groupHash} not found");

        // Serialize: count(4 LE) + [len(4 LE) + kp_bytes] for each
        using var ms = new MemoryStream();
        var countBytes = BitConverter.GetBytes((uint)keyPackages.Length);
        ms.Write(countBytes, 0, 4);
        foreach (var kp in keyPackages)
        {
            var lenBytes = BitConverter.GetBytes((uint)kp.Length);
            ms.Write(lenBytes, 0, 4);
            ms.Write(kp, 0, kp.Length);
        }
        var allData = ms.ToArray();

        NativeMethods.NapMlsBytes welcome = default;
        fixed (byte* pKp = allData)
        {
            var rc = NativeMethods.napmls_add_members(
                _provider, group, _identity, pKp, (nuint)allData.Length, &welcome, null);
            if (rc != NativeMethods.NAPMLS_OK)
                throw new InvalidOperationException($"Failed to add members: {rc}");
        }

        var welcomeData = NativeMethods.ReadBytes(welcome);
        NativeMethods.napmls_free_bytes(welcome);
        return welcomeData;
    }

    /// <summary>Process a Welcome message. Returns groupHash.</summary>
    public unsafe string ProcessWelcome(byte[] welcomeData)
    {
        IntPtr group;
        fixed (byte* pWelcome = welcomeData)
        {
            var rc = NativeMethods.napmls_process_welcome(
                _provider, pWelcome, (nuint)welcomeData.Length, &group, null);
            if (rc != NativeMethods.NAPMLS_OK || group == IntPtr.Zero)
                throw new InvalidOperationException($"Failed to process welcome: {rc}");
        }

        var groupHash = GetGroupHash(group);
        _groups[groupHash] = group;
        return groupHash;
    }

    /// <summary>Encrypt a message for a group.</summary>
    public unsafe byte[] Encrypt(string groupHash, string plaintext)
    {
        if (!_groups.TryGetValue(groupHash, out var group))
            throw new KeyNotFoundException($"Group {groupHash} not found");

        var msgBytes = Encoding.UTF8.GetBytes(plaintext);
        NativeMethods.NapMlsBytes cipher = default;
        fixed (byte* pMsg = msgBytes)
        {
            var rc = NativeMethods.napmls_encrypt(
                _provider, group, _identity, pMsg, (nuint)msgBytes.Length, &cipher, null);
            if (rc != NativeMethods.NAPMLS_OK)
                throw new InvalidOperationException($"Failed to encrypt: {rc}");
        }

        var result = NativeMethods.ReadBytes(cipher);
        NativeMethods.napmls_free_bytes(cipher);
        return result;
    }

    /// <summary>Decrypt a message from a group.</summary>
    public unsafe string? Decrypt(string groupHash, byte[] ciphertext)
    {
        if (!_groups.TryGetValue(groupHash, out var group))
            throw new KeyNotFoundException($"Group {groupHash} not found");

        NativeMethods.NapMlsBytes plain = default;
        fixed (byte* pCipher = ciphertext)
        {
            var rc = NativeMethods.napmls_decrypt(
                _provider, group, pCipher, (nuint)ciphertext.Length, &plain, null);
            if (rc != NativeMethods.NAPMLS_OK)
                return null; // Decryption failed (e.g. removed member)
        }

        var result = Encoding.UTF8.GetString(NativeMethods.ReadBytes(plain));
        NativeMethods.napmls_free_bytes(plain);
        return result;
    }

    /// <summary>Remove a member by leaf index.</summary>
    public unsafe void RemoveMember(string groupHash, uint leafIndex)
    {
        if (!_groups.TryGetValue(groupHash, out var group))
            throw new KeyNotFoundException($"Group {groupHash} not found");

        var rc = NativeMethods.napmls_remove_members(
            _provider, group, _identity, leafIndex, null);
        if (rc != NativeMethods.NAPMLS_OK)
            throw new InvalidOperationException($"Failed to remove member: {rc}");
    }

    /// <summary>Get member count for a group.</summary>
    public unsafe uint GetMemberCount(string groupHash)
    {
        if (!_groups.TryGetValue(groupHash, out var group))
            throw new KeyNotFoundException($"Group {groupHash} not found");

        uint count;
        var rc = NativeMethods.napmls_group_member_count(group, &count, null);
        if (rc != NativeMethods.NAPMLS_OK)
            throw new InvalidOperationException($"Failed to get member count: {rc}");
        return count;
    }

    /// <summary>Get group epoch.</summary>
    public unsafe ulong GetEpoch(string groupHash)
    {
        if (!_groups.TryGetValue(groupHash, out var group))
            throw new KeyNotFoundException($"Group {groupHash} not found");

        ulong epoch;
        var rc = NativeMethods.napmls_group_epoch(group, &epoch, null);
        if (rc != NativeMethods.NAPMLS_OK)
            throw new InvalidOperationException($"Failed to get epoch: {rc}");
        return epoch;
    }

    /// <summary>Find member leaf index by name.</summary>
    public unsafe uint? FindMember(string groupHash, string memberName)
    {
        if (!_groups.TryGetValue(groupHash, out var group))
            throw new KeyNotFoundException($"Group {groupHash} not found");

        var nameBytes = Encoding.UTF8.GetBytes(memberName);
        uint leafIndex;
        fixed (byte* pName = nameBytes)
        {
            var rc = NativeMethods.napmls_find_member_by_name(
                group, pName, (nuint)nameBytes.Length, &leafIndex, null);
            if (rc != NativeMethods.NAPMLS_OK)
                return null;
        }
        return leafIndex;
    }

    /// <summary>Get group handle pointer (for cross-client operations).</summary>
    internal IntPtr GetGroupHandle(string groupHash)
    {
        if (!_groups.TryGetValue(groupHash, out var group))
            throw new KeyNotFoundException($"Group {groupHash} not found");
        return group;
    }

    /// <summary>Remove a group from tracking.</summary>
    public void RemoveGroup(string groupHash)
    {
        if (_groups.TryGetValue(groupHash, out var group))
        {
            NativeMethods.napmls_group_free(group);
            _groups.Remove(groupHash);
        }
    }

    /// <summary>Get the most recently added group handle.</summary>
    public string GetGroupFromLatest()
    {
        if (_groups.Count == 0)
            throw new InvalidOperationException("No groups tracked");
        return _groups.Keys.Last();
    }

    private static string GetGroupHash(byte[] groupId)
    {
        return Core.MessageChunker.ComputeGroupHash(groupId);
    }

    private static unsafe string GetGroupHash(IntPtr group)
    {
        NativeMethods.NapMlsBytes outGroupId = default;
        var rc = NativeMethods.napmls_group_id(group, &outGroupId, null);
        if (rc != NativeMethods.NAPMLS_OK || outGroupId.ptr == IntPtr.Zero)
            throw new InvalidOperationException($"Failed to get group_id: {rc}");

        try
        {
            var groupId = NativeMethods.ReadBytes(outGroupId);
            return Core.MessageChunker.ComputeGroupHash(groupId);
        }
        finally
        {
            NativeMethods.napmls_free_bytes(outGroupId);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await _lock.WaitAsync();
        try
        {
            foreach (var (hash, group) in _groups)
                NativeMethods.napmls_group_free(group);
            _groups.Clear();

            NativeMethods.napmls_identity_free(_identity);
            NativeMethods.napmls_provider_free(_provider);
        }
        finally
        {
            _lock.Release();
        }

        _lock.Dispose();
    }
}
