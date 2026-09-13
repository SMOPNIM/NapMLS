using System.Text;

namespace NapMLS.FFITest;

/// <summary>
/// Safe wrapper around a Rust identity handle (signature key pair + credential).
/// </summary>
internal sealed class MlsIdentity : IDisposable
{
    public IntPtr Handle { get; private set; }
    private bool _disposed;

    public MlsIdentity(MlsProvider provider, string name)
    {
        var nameBytes = Encoding.UTF8.GetBytes(name);
        IntPtr handle;
        unsafe
        {
            fixed (byte* pName = nameBytes)
            {
                NativeMethods.NapMlsError err = default;
                var rc = NativeMethods.napmls_create_identity(
                    provider.Handle, pName, nameBytes.Length, &handle, &err);
                if (rc != 0)
                    throw new InvalidOperationException($"Failed to create identity '{name}': {NativeMethods.GetErrorMessage(err)}");
            }
        }
        Handle = handle;
    }

    /// <summary>
    /// Compute the identity fingerprint (safety code).
    /// Format: NAPMLS-XXXXXXXX-XXXXXXXX-0000-0000
    /// </summary>
    public string GetFingerprint()
    {
        unsafe
        {
            NativeMethods.NapMlsError err = default;
            NativeMethods.NapMlsBytes fp = default;
            var rc = NativeMethods.napmls_identity_fingerprint(Handle, &fp, &err);
            if (rc != 0)
                throw new InvalidOperationException($"Failed to get fingerprint: {NativeMethods.GetErrorMessage(err)}");

            try
            {
                var data = NativeMethods.ReadBytes(fp);
                if (data.Length != 8) throw new InvalidOperationException($"Invalid fingerprint length: {data.Length}");
                return $"NAPMLS-{data[0]:X2}{data[1]:X2}{data[2]:X2}{data[3]:X2}-{data[4]:X2}{data[5]:X2}{data[6]:X2}{data[7]:X2}-0000-0000";
            }
            finally
            {
                NativeMethods.FreeBytes(ref fp);
            }
        }
    }

    public void Dispose()
    {
        if (!_disposed && Handle != IntPtr.Zero)
        {
            NativeMethods.napmls_identity_free(Handle);
            Handle = IntPtr.Zero;
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }

    ~MlsIdentity() => Dispose();
}
