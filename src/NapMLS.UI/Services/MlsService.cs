using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace NapMLS.UI.Services;

/// <summary>
/// P1d-5: Singleton MLS service — owns FFI provider + identity for the app lifetime.
/// Provides: group operations, encrypt/decrypt, identity management.
/// </summary>
public sealed class MlsService : IDisposable
{
    private IntPtr _provider;
    private IntPtr _identity;
    private bool _disposed;
    private string? _username;
    private byte[]? _fingerprint;

    private MlsService(IntPtr provider, IntPtr identity)
    {
        _provider = provider;
        _identity = identity;
    }

    /// <summary>
    /// Initialize MLS with encryption key and open provider from file.
    /// Creates or loads identity if username is provided.
    /// </summary>
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
                NapMlsNative.napmls_free_bytes(new NapMlsNative.NapMlsBytes { ptr = error.message, len = 0 });
                return null;
            }

            // Load or create identity
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
                        // Identity doesn't exist yet — caller should create it separately
                        NapMlsNative.napmls_free_bytes(new NapMlsNative.NapMlsBytes { ptr = error.message, len = 0 });
                    }
                }
            }

            var svc = new MlsService(provider, identity)
            {
                _username = username,
            };

            if (identity != IntPtr.Zero)
                svc._fingerprint = svc.GetFingerprintRaw();

            return svc;
        }
    }

    /// <summary>
    /// Create a new identity. Returns fingerprint bytes on success.
    /// </summary>
    public byte[]? CreateIdentity(string username)
    {
        if (_disposed || _provider == IntPtr.Zero) return null;

        // Free old identity if any
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
                    NapMlsNative.napmls_free_bytes(new NapMlsNative.NapMlsBytes { ptr = error.message, len = 0 });
                    return null;
                }
            }
        }

        _identity = identity;
        _username = username;

        // Register for persistence
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

    /// <summary>
    /// Load an existing identity by username.
    /// </summary>
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
                    NapMlsNative.napmls_free_bytes(new NapMlsNative.NapMlsBytes { ptr = error.message, len = 0 });
                    return false;
                }
            }
        }

        _identity = identity;
        _username = username;
        _fingerprint = GetFingerprintRaw();
        return true;
    }

    /// <summary>
    /// Create a new MLS group. Returns group_id hex string on success.
    /// </summary>
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
                var rc = NapMlsNative.napmls_create_group(_provider, _identity, namePtr, nameBytes.Length, &group, &error);
                if (rc != NapMlsNative.NAPMLS_OK)
                {
                    NapMlsNative.napmls_free_bytes(new NapMlsNative.NapMlsBytes { ptr = error.message, len = 0 });
                    return null;
                }
            }
        }

        // Get group_id from the group handle — need to read it from group members or use a helper
        // For now, use list_groups to find the newly created group
        var groups = ListGroups();
        var newest = groups.LastOrDefault(g => g.name == groupName);

        NapMlsNative.napmls_group_free(group);
        return newest?.group_id;
    }

    /// <summary>
    /// List all groups.
    /// </summary>
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

    /// <summary>
    /// Encrypt a message for a group. Returns ciphertext bytes.
    /// </summary>
    public byte[]? Encrypt(string groupIdHex, string plaintext)
    {
        if (_disposed || _provider == IntPtr.Zero || _identity == IntPtr.Zero) return null;

        var groupBytes = Convert.FromHexString(groupIdHex);
        var msgBytes = Encoding.UTF8.GetBytes(plaintext);
        var error = new NapMlsNative.NapMlsError();
        var outBytes = new NapMlsNative.NapMlsBytes();

        unsafe
        {
            fixed (byte* gPtr = groupBytes, mPtr = msgBytes)
            {
                var rc = NapMlsNative.napmls_encrypt(
                    _provider, _identity, gPtr, groupBytes.Length,
                    mPtr, msgBytes.Length, &outBytes, &error);

                if (rc != NapMlsNative.NAPMLS_OK)
                {
                    NapMlsNative.napmls_free_bytes(new NapMlsNative.NapMlsBytes { ptr = error.message, len = 0 });
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

    /// <summary>
    /// Decrypt a message from a group. Returns plaintext string or null.
    /// </summary>
    public string? Decrypt(string groupIdHex, byte[] ciphertext)
    {
        if (_disposed || _provider == IntPtr.Zero || _identity == IntPtr.Zero) return null;

        var groupBytes = Convert.FromHexString(groupIdHex);
        var error = new NapMlsNative.NapMlsError();
        var outBytes = new NapMlsNative.NapMlsBytes();

        unsafe
        {
            fixed (byte* gPtr = groupBytes, cPtr = ciphertext)
            {
                var rc = NapMlsNative.napmls_decrypt(
                    _provider, _identity, gPtr, groupBytes.Length,
                    cPtr, ciphertext.Length, &outBytes, &error);

                if (rc != NapMlsNative.NAPMLS_OK)
                {
                    NapMlsNative.napmls_free_bytes(new NapMlsNative.NapMlsBytes { ptr = error.message, len = 0 });
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

    /// <summary>
    /// Get epoch for a group.
    /// </summary>
    public int GetEpoch(string groupIdHex)
    {
        if (_disposed || _provider == IntPtr.Zero || _identity == IntPtr.Zero) return 0;

        var groupBytes = Convert.FromHexString(groupIdHex);
        unsafe
        {
            fixed (byte* gPtr = groupBytes)
            {
                return NapMlsNative.napmls_group_epoch(_provider, _identity, gPtr, groupBytes.Length);
            }
        }
    }

    public byte[]? GetFingerprint() => _fingerprint;
    public string? GetUsername() => _username;
    public bool HasIdentity => _identity != IntPtr.Zero;

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
                NapMlsNative.napmls_free_bytes(new NapMlsNative.NapMlsBytes { ptr = error.message, len = 0 });
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
