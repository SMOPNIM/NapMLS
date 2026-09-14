using System.Runtime.InteropServices;
using System.Text.Json;

namespace NapMLS.UI.Services;

/// <summary>
/// P1d-3e: Singleton wrapper around the MLS FFI provider.
/// Lifecycle: open from file after identity exists, provide group operations.
/// </summary>
public sealed class MlsService : IDisposable
{
    private IntPtr _provider;
    private bool _disposed;

    private MlsService(IntPtr provider)
    {
        _provider = provider;
    }

    /// <summary>
    /// Open or create provider from the app data directory.
    /// Returns null if init fails or provider can't be opened.
    /// </summary>
    public static MlsService? Open(string dbPath, byte[] encryptionKey)
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
            var pathBytes = System.Text.Encoding.UTF8.GetBytes(dbPath);
            fixed (byte* pathPtr = pathBytes)
            {
                provider = NapMlsNative.napmls_provider_new_from_file(pathPtr, &error);
            }

            if (provider == IntPtr.Zero)
            {
                NapMlsNative.napmls_free_bytes(new NapMlsNative.NapMlsBytes { ptr = error.message, len = 0 });
                return null;
            }

            return new MlsService(provider);
        }
    }

    /// <summary>
    /// List all MLS groups the current identity is part of.
    /// Returns JSON array: [{group_id, name, epoch}]
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
                var json = System.Text.Encoding.UTF8.GetString(data);
                return JsonSerializer.Deserialize<List<MlsGroupInfo>>(json) ?? [];
            }
            catch
            {
                return [];
            }
            finally
            {
                NapMlsNative.napmls_free_bytes(bytes);
            }
        }
    }

    public void Dispose()
    {
        if (!_disposed && _provider != IntPtr.Zero)
        {
            NapMlsNative.napmls_provider_free(_provider);
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
