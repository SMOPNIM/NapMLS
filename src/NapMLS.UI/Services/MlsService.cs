using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace NapMLS.UI.Services;

/// <summary>
/// Singleton MLS service — owns FFI provider + identity for the app lifetime.
/// Provides: group operations, encrypt/decrypt, identity management.
/// </summary>
public sealed class MlsService : IDisposable
{
    public const int NAPMLS_ERR_KEY_MISMATCH = -7;

    private IntPtr _provider;
    private IntPtr _identity;
    private bool _disposed;
    private string? _username;
    private byte[]? _fingerprint;
    private readonly ConcurrentDictionary<string, IntPtr> _groups = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _groupLocks = new();

    private MlsService(IntPtr provider, IntPtr identity)
    {
        _provider = provider;
        _identity = identity;
    }

    public static MlsService? Open(string dbPath, byte[] encryptionKey, string? username = null)
    {
        int initResult = 0;
        unsafe
        {
            fixed (byte* keyPtr = encryptionKey)
            {
                initResult = NapMlsNative.napmls_init(keyPtr, (nuint)encryptionKey.Length);
            }
        }

        LastOpenError = initResult;
        if (initResult != NapMlsNative.NAPMLS_OK) return null;

        unsafe
        {
            var error = new NapMlsNative.NapMlsError();
            IntPtr provider;
            var pathBytes = Encoding.UTF8.GetBytes(dbPath);
            fixed (byte* pathPtr = pathBytes)
            {
                provider = NapMlsNative.napmls_provider_new_from_file(pathPtr, &error);
            }

            if (provider == IntPtr.Zero)
            {
                NapMlsNative.napmls_free_error(error);
                return null;
            }

            IntPtr identity = IntPtr.Zero;
            if (username != null)
            {
                var nameBytes = Encoding.UTF8.GetBytes(username);
                error = new NapMlsNative.NapMlsError();
                fixed (byte* namePtr = nameBytes)
                {
                    var rc = NapMlsNative.napmls_load_identity(provider, namePtr, (nuint)nameBytes.Length, &identity, &error);
                    if (rc != NapMlsNative.NAPMLS_OK)
                    {
                        NapMlsNative.napmls_free_error(error);
                    }
                }
            }

            var svc = new MlsService(provider, identity) { _username = username };

            if (identity != IntPtr.Zero)
                svc._fingerprint = svc.GetFingerprintRaw();

            svc.LoadPersistedGroups();
            return svc;
        }
    }

    private void LoadPersistedGroups()
    {
        var groupInfos = ListGroupsRaw();
        foreach (var info in groupInfos)
        {
            if (_groups.ContainsKey(info.group_id)) continue;
            var groupIdBytes = Convert.FromHexString(info.group_id);
            unsafe
            {
                fixed (byte* pId = groupIdBytes)
                {
                    IntPtr group;
                    var rc = NapMlsNative.napmls_load_group(_provider, pId, (nuint)groupIdBytes.Length, &group, null);
                    if (rc == NapMlsNative.NAPMLS_OK && group != IntPtr.Zero)
                        _groups[info.group_id] = group;
                }
            }
        }
    }

    public byte[]? CreateIdentity(string username)
    {
        if (_disposed || _provider == IntPtr.Zero) return null;

        if (_identity != IntPtr.Zero)
        {
            NapMlsNative.napmls_identity_free(_identity);
            _identity = IntPtr.Zero;
        }

        var nameBytes = Encoding.UTF8.GetBytes(username);
        var error = new NapMlsNative.NapMlsError();
        IntPtr identity;
        unsafe
        {
            fixed (byte* namePtr = nameBytes)
            {
                var rc = NapMlsNative.napmls_create_identity(_provider, namePtr, (nuint)nameBytes.Length, &identity, &error);
                if (rc != NapMlsNative.NAPMLS_OK)
                {
                    NapMlsNative.napmls_free_error(error);
                    return null;
                }
            }
        }

        _identity = identity;
        _username = username;

        error = new NapMlsNative.NapMlsError();
        unsafe
        {
            fixed (byte* namePtr = nameBytes)
            {
                NapMlsNative.napmls_register_identity(_provider, namePtr, (nuint)nameBytes.Length, _identity, &error);
            }
        }

        _fingerprint = GetFingerprintRaw();
        return _fingerprint;
    }

    public bool LoadIdentity(string username)
    {
        if (_disposed || _provider == IntPtr.Zero) return false;

        if (_identity != IntPtr.Zero)
        {
            NapMlsNative.napmls_identity_free(_identity);
            _identity = IntPtr.Zero;
        }

        var nameBytes = Encoding.UTF8.GetBytes(username);
        var error = new NapMlsNative.NapMlsError();
        IntPtr identity = IntPtr.Zero;
        unsafe
        {
            fixed (byte* namePtr = nameBytes)
            {
                var rc = NapMlsNative.napmls_load_identity(_provider, namePtr, (nuint)nameBytes.Length, &identity, &error);
                if (rc != NapMlsNative.NAPMLS_OK)
                {
                    NapMlsNative.napmls_free_error(error);
                    return false;
                }
            }
        }

        _identity = identity;
        _username = username;
        _fingerprint = GetFingerprintRaw();
        return true;
    }

    public string? CreateGroup(string groupName)
    {
        if (_disposed || _provider == IntPtr.Zero || _identity == IntPtr.Zero) return null;

        var nameBytes = Encoding.UTF8.GetBytes(groupName);
        var error = new NapMlsNative.NapMlsError();
        IntPtr group;
        unsafe
        {
            fixed (byte* namePtr = nameBytes)
            {
                var rc = NapMlsNative.napmls_create_group(
                    _provider, _identity, namePtr, (nuint)nameBytes.Length, &group, &error);
                if (rc != NapMlsNative.NAPMLS_OK)
                {
                    NapMlsNative.napmls_free_error(error);
                    return null;
                }
            }
        }

        var groupIdHex = GetGroupHandleId(group);
        if (groupIdHex != null)
        {
            if (_groups.TryGetValue(groupIdHex, out var old))
                NapMlsNative.napmls_group_free(old);
            _groups[groupIdHex] = group;
        }
        else
            NapMlsNative.napmls_group_free(group);

        return groupIdHex;
    }

    public string? JoinGroup(byte[] welcomeData)
    {
        if (_disposed || _provider == IntPtr.Zero || _identity == IntPtr.Zero) return null;

        IntPtr group;
        unsafe
        {
            fixed (byte* pWelcome = welcomeData)
            {
                var error = new NapMlsNative.NapMlsError();
                var rc = NapMlsNative.napmls_process_welcome(
                    _provider, pWelcome, (nuint)welcomeData.Length, &group, &error);
                if (rc != NapMlsNative.NAPMLS_OK || group == IntPtr.Zero)
                {
                    NapMlsNative.napmls_free_error(error);
                    return null;
                }
            }
        }

        var groupIdHex = GetGroupHandleId(group);
        if (groupIdHex != null)
        {
            if (_groups.TryGetValue(groupIdHex, out var old))
                NapMlsNative.napmls_group_free(old);
            _groups[groupIdHex] = group;
        }
        else
            NapMlsNative.napmls_group_free(group);

        return groupIdHex;
    }

    /// <summary>Remove a group from the local handle cache and free its FFI handle.</summary>
    public void RemoveGroup(string groupIdHex)
    {
        if (_groups.TryRemove(groupIdHex, out var group))
            NapMlsNative.napmls_group_free(group);
        _groupLocks.TryRemove(groupIdHex, out _);
    }

    private SemaphoreSlim GetGroupLock(string groupIdHex) =>
        _groupLocks.GetOrAdd(groupIdHex, _ => new SemaphoreSlim(1, 1));

    /// <summary>Last error from Open(). NAPMLS_ERR_KEY_MISMATCH = wrong password.</summary>
    public static int LastOpenError { get; private set; }

    /// <summary>Generate a KeyPackage for this identity. Returns serialized bytes.</summary>
    public byte[]? GenerateKeyPackage()
    {
        if (_disposed || _provider == IntPtr.Zero || _identity == IntPtr.Zero) return null;

        var error = new NapMlsNative.NapMlsError();
        var bytes = new NapMlsNative.NapMlsBytes();
        unsafe
        {
            var rc = NapMlsNative.napmls_generate_key_package(_provider, _identity, &bytes, &error);
            if (rc != NapMlsNative.NAPMLS_OK)
            {
                NapMlsNative.napmls_free_error(error);
                return null;
            }
        }

        try
        {
            var result = new byte[(int)bytes.len];
            Marshal.Copy(bytes.ptr, result, 0, (int)bytes.len);
            return result;
        }
        finally
        {
            NapMlsNative.napmls_free_bytes(bytes);
        }
    }

    /// <summary>Add members to a group by their KeyPackage bytes. Returns Welcome bytes on success.</summary>
    public byte[]? AddMembers(string groupIdHex, byte[][] keyPackages)
    {
        if (_disposed || _provider == IntPtr.Zero || _identity == IntPtr.Zero) return null;
        if (!_groups.TryGetValue(groupIdHex, out var group))
        {
            Console.WriteLine($"[MlsService] AddMembers: group {groupIdHex} NOT FOUND in _groups");
            return null;
        }

        Console.WriteLine($"[MlsService] AddMembers: group={group}, kps={keyPackages.Length}");

        // Wire format: [count:4][len1:4][kp1..][len2:4][kp2..]...
        var totalLen = 4;
        foreach (var kp in keyPackages)
            totalLen += 4 + kp.Length;

        var allData = new byte[totalLen];
        var offset = 0;
        BitConverter.GetBytes((uint)keyPackages.Length).CopyTo(allData, offset); offset += 4;
        foreach (var kp in keyPackages)
        {
            BitConverter.GetBytes((uint)kp.Length).CopyTo(allData, offset); offset += 4;
            kp.CopyTo(allData, offset); offset += kp.Length;
        }

        var error = new NapMlsNative.NapMlsError();
        var outWelcome = new NapMlsNative.NapMlsBytes();
        unsafe
        {
            fixed (byte* pData = allData)
            {
                var rc = NapMlsNative.napmls_add_members(
                    _provider, group, _identity,
                    pData, (nuint)allData.Length, &outWelcome, &error);
                if (rc != NapMlsNative.NAPMLS_OK)
                {
                    Console.WriteLine($"[MlsService] AddMembers FAILED: rc={rc}");
                    NapMlsNative.napmls_free_error(error);
                    return null;
                }
                Console.WriteLine($"[MlsService] AddMembers OK: welcome_len={outWelcome.len}");
            }
        }

        // Read epoch after AddMembers to verify it advanced
        unsafe
        {
            ulong epoch = 0;
            var eRc = NapMlsNative.napmls_group_epoch(group, &epoch, null);
            Console.WriteLine($"[MlsService] AddMembers: epoch after add={epoch} (rc={eRc})");
        }

        try
        {
            var result = new byte[(int)outWelcome.len];
            Marshal.Copy(outWelcome.ptr, result, 0, (int)outWelcome.len);
            return result;
        }
        finally
        {
            NapMlsNative.napmls_free_bytes(outWelcome);
        }
    }

    public byte[]? Encrypt(string groupIdHex, string plaintext)
    {
        if (_disposed || _provider == IntPtr.Zero || _identity == IntPtr.Zero) return null;
        if (!_groups.TryGetValue(groupIdHex, out var group)) return null;
        if (group == IntPtr.Zero) return null;

        var sem = GetGroupLock(groupIdHex);
        sem.Wait();
        try
        {
            var msgBytes = Encoding.UTF8.GetBytes(plaintext);
            var error = new NapMlsNative.NapMlsError();
            var outBytes = new NapMlsNative.NapMlsBytes();

            unsafe
            {
                fixed (byte* mPtr = msgBytes)
                {
                    var rc = NapMlsNative.napmls_encrypt(
                        _provider, group, _identity,
                        mPtr, (nuint)msgBytes.Length, &outBytes, &error);

                    if (rc != NapMlsNative.NAPMLS_OK)
                    {
                        NapMlsNative.napmls_free_error(error);
                        Console.WriteLine($"[MlsService] Encrypt failed: rc={rc}");
                        return null;
                    }
                }
            }

            var result = new byte[(int)outBytes.len];
            Marshal.Copy(outBytes.ptr, result, 0, (int)outBytes.len);
            NapMlsNative.napmls_free_bytes(outBytes);
            return result;
        }
        finally
        {
            sem.Release();
        }
    }

    public string? Decrypt(string groupIdHex, byte[] ciphertext)
    {
        if (_disposed || _provider == IntPtr.Zero || _identity == IntPtr.Zero) return null;
        if (!_groups.TryGetValue(groupIdHex, out var group)) return null;
        if (group == IntPtr.Zero) return null;

        var sem = GetGroupLock(groupIdHex);
        sem.Wait();
        try
        {
            var error = new NapMlsNative.NapMlsError();
            var outBytes = new NapMlsNative.NapMlsBytes();

            unsafe
            {
                fixed (byte* cPtr = ciphertext)
                {
                    var rc = NapMlsNative.napmls_decrypt(
                        _provider, group,
                        cPtr, (nuint)ciphertext.Length, &outBytes, &error);

                    if (rc != NapMlsNative.NAPMLS_OK)
                    {
                        NapMlsNative.napmls_free_error(error);
                        return null;
                    }
                }
            }

            var data = new byte[(int)outBytes.len];
            Marshal.Copy(outBytes.ptr, data, 0, (int)outBytes.len);
            NapMlsNative.napmls_free_bytes(outBytes);
            return Encoding.UTF8.GetString(data);
        }
        finally
        {
            sem.Release();
        }
    }

    public int GetEpoch(string groupIdHex)
    {
        if (_disposed || _provider == IntPtr.Zero || _identity == IntPtr.Zero) return 0;
        if (!_groups.TryGetValue(groupIdHex, out var group)) return 0;
        if (group == IntPtr.Zero) return 0;

        var sem = GetGroupLock(groupIdHex);
        sem.Wait();
        try
        {
            unsafe
            {
                ulong epoch = 0;
                var rc = NapMlsNative.napmls_group_epoch(group, &epoch, null);
                return rc == NapMlsNative.NAPMLS_OK ? (int)epoch : 0;
            }
        }
        finally
        {
            sem.Release();
        }
    }

    public int GetMemberCount(string groupIdHex)
    {
        if (_disposed || _provider == IntPtr.Zero) return 0;
        if (!_groups.TryGetValue(groupIdHex, out var group)) return 0;
        if (group == IntPtr.Zero) return 0;

        var sem = GetGroupLock(groupIdHex);
        sem.Wait();
        try
        {
            unsafe
            {
                uint count = 0;
                var rc = NapMlsNative.napmls_group_member_count(group, &count, null);
                return rc == NapMlsNative.NAPMLS_OK ? (int)count : 0;
            }
        }
        finally
        {
            sem.Release();
        }
    }

    public List<MlsGroupInfo> ListGroups()
    {
        if (_disposed || _provider == IntPtr.Zero) return [];

        var error = new NapMlsNative.NapMlsError();
        var bytes = new NapMlsNative.NapMlsBytes();

        unsafe
        {
            var rc = NapMlsNative.napmls_list_groups(_provider, &bytes, &error);
            if (rc != NapMlsNative.NAPMLS_OK) return [];

            try
            {
                var data = new byte[(int)bytes.len];
                Marshal.Copy(bytes.ptr, data, 0, (int)bytes.len);
                var json = Encoding.UTF8.GetString(data);
                return JsonSerializer.Deserialize<List<MlsGroupInfo>>(json) ?? [];
            }
            catch { return []; }
            finally { NapMlsNative.napmls_free_bytes(bytes); }
        }
    }

    private List<MlsGroupInfo> ListGroupsRaw() => ListGroups();

    public byte[]? GetFingerprint() => _fingerprint;
    public string? GetUsername() => _username;
    public bool HasIdentity => _identity != IntPtr.Zero;
    public bool HasGroup(string groupIdHex) => _groups.ContainsKey(groupIdHex);

    private string? GetGroupHandleId(IntPtr group)
    {
        var error = new NapMlsNative.NapMlsError();
        var bytes = new NapMlsNative.NapMlsBytes();
        unsafe
        {
            var rc = NapMlsNative.napmls_group_id(group, &bytes, &error);
            if (rc != NapMlsNative.NAPMLS_OK || bytes.ptr == IntPtr.Zero)
            {
                NapMlsNative.napmls_free_error(error);
                return null;
            }
        }

        try
        {
            var idBytes = new byte[(int)bytes.len];
            Marshal.Copy(bytes.ptr, idBytes, 0, (int)bytes.len);
            return Convert.ToHexString(idBytes).ToLowerInvariant();
        }
        finally
        {
            NapMlsNative.napmls_free_bytes(bytes);
        }
    }

    private byte[]? GetFingerprintRaw()
    {
        if (_identity == IntPtr.Zero) return null;

        var error = new NapMlsNative.NapMlsError();
        var bytes = new NapMlsNative.NapMlsBytes();
        unsafe
        {
            var rc = NapMlsNative.napmls_identity_fingerprint(_identity, &bytes, &error);
            if (rc != NapMlsNative.NAPMLS_OK)
            {
                NapMlsNative.napmls_free_error(error);
                return null;
            }
        }

        try
        {
            var result = new byte[(int)bytes.len];
            Marshal.Copy(bytes.ptr, result, 0, (int)bytes.len);
            return result;
        }
        finally
        {
            NapMlsNative.napmls_free_bytes(bytes);
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            foreach (var (_, group) in _groups)
                NapMlsNative.napmls_group_free(group);
            _groups.Clear();
            _groupLocks.Clear();

            if (_identity != IntPtr.Zero) NapMlsNative.napmls_identity_free(_identity);
            if (_provider != IntPtr.Zero) NapMlsNative.napmls_provider_free(_provider);
            _identity = IntPtr.Zero;
            _provider = IntPtr.Zero;
            _disposed = true;
        }
    }
}

public class MlsGroupInfo
{
    public string group_id { get; set; } = "";
    public string name { get; set; } = "";
    public int epoch { get; set; }
}
