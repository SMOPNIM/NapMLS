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
    private IntPtr _provider;
    private IntPtr _identity;
    private bool _disposed;
    private string? _username;
    private byte[]? _fingerprint;
    private readonly Dictionary<string, IntPtr> _groups = new();

    private MlsService(IntPtr provider, IntPtr identity)
    {
        _provider = provider;
        _identity = identity;
    }

    public static MlsService? Open(string dbPath, byte[] encryptionKey, string? username = null)
    {
        unsafe
        {
            fixed (byte* keyPtr = encryptionKey)
            {
                var rc = NapMlsNative.napmls_init(keyPtr, encryptionKey.Length);
                if (rc != NapMlsNative.NAPMLS_OK) return null;
            }

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
                    var rc = NapMlsNative.napmls_load_identity(provider, namePtr, nameBytes.Length, &identity, &error);
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
                    var rc = NapMlsNative.napmls_load_group(_provider, pId, groupIdBytes.Length, &group, null);
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
                var rc = NapMlsNative.napmls_create_identity(_provider, namePtr, nameBytes.Length, &identity, &error);
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
                NapMlsNative.napmls_register_identity(_provider, namePtr, nameBytes.Length, _identity, &error);
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
                var rc = NapMlsNative.napmls_load_identity(_provider, namePtr, nameBytes.Length, &identity, &error);
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
                    _provider, _identity, namePtr, nameBytes.Length, &group, &error);
                if (rc != NapMlsNative.NAPMLS_OK)
                {
                    NapMlsNative.napmls_free_error(error);
                    return null;
                }
            }
        }

        var groupIdHex = GetGroupHandleId(group);
        if (groupIdHex != null)
            _groups[groupIdHex] = group;
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
                    _provider, pWelcome, welcomeData.Length, &group, &error);
                if (rc != NapMlsNative.NAPMLS_OK || group == IntPtr.Zero)
                {
                    NapMlsNative.napmls_free_error(error);
                    return null;
                }
            }
        }

        var groupIdHex = GetGroupHandleId(group);
        if (groupIdHex != null)
            _groups[groupIdHex] = group;
        else
            NapMlsNative.napmls_group_free(group);

        return groupIdHex;
    }

    public byte[]? Encrypt(string groupIdHex, string plaintext)
    {
        if (_disposed || _provider == IntPtr.Zero || _identity == IntPtr.Zero) return null;
        if (!_groups.TryGetValue(groupIdHex, out var group)) return null;

        var msgBytes = Encoding.UTF8.GetBytes(plaintext);
        var error = new NapMlsNative.NapMlsError();
        var outBytes = new NapMlsNative.NapMlsBytes();

        unsafe
        {
            fixed (byte* mPtr = msgBytes)
            {
                var rc = NapMlsNative.napmls_encrypt(
                    _provider, group, _identity,
                    mPtr, msgBytes.Length, &outBytes, &error);

                if (rc != NapMlsNative.NAPMLS_OK)
                {
                    NapMlsNative.napmls_free_error(error);
                    return null;
                }
            }
        }

        try
        {
            var result = new byte[outBytes.len];
            Marshal.Copy(outBytes.ptr, result, 0, outBytes.len);
            return result;
        }
        finally
        {
            NapMlsNative.napmls_free_bytes(outBytes);
        }
    }

    public string? Decrypt(string groupIdHex, byte[] ciphertext)
    {
        if (_disposed || _provider == IntPtr.Zero || _identity == IntPtr.Zero) return null;
        if (!_groups.TryGetValue(groupIdHex, out var group)) return null;

        var error = new NapMlsNative.NapMlsError();
        var outBytes = new NapMlsNative.NapMlsBytes();

        unsafe
        {
            fixed (byte* cPtr = ciphertext)
            {
                var rc = NapMlsNative.napmls_decrypt(
                    _provider, group,
                    cPtr, ciphertext.Length, &outBytes, &error);

                if (rc != NapMlsNative.NAPMLS_OK)
                {
                    NapMlsNative.napmls_free_error(error);
                    return null;
                }
            }
        }

        try
        {
            var data = new byte[outBytes.len];
            Marshal.Copy(outBytes.ptr, data, 0, outBytes.len);
            return Encoding.UTF8.GetString(data);
        }
        finally
        {
            NapMlsNative.napmls_free_bytes(outBytes);
        }
    }

    public int GetEpoch(string groupIdHex)
    {
        if (_disposed || _provider == IntPtr.Zero || _identity == IntPtr.Zero) return 0;
        if (!_groups.TryGetValue(groupIdHex, out var group)) return 0;

        unsafe
        {
            ulong epoch = 0;
            var rc = NapMlsNative.napmls_group_epoch(group, &epoch, null);
            return rc == NapMlsNative.NAPMLS_OK ? (int)epoch : 0;
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
                var data = new byte[bytes.len];
                Marshal.Copy(bytes.ptr, data, 0, bytes.len);
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
            var idBytes = new byte[bytes.len];
            Marshal.Copy(bytes.ptr, idBytes, 0, bytes.len);
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
            var result = new byte[bytes.len];
            Marshal.Copy(bytes.ptr, result, 0, bytes.len);
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
