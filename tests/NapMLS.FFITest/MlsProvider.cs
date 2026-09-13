using System.Runtime.InteropServices;

namespace NapMLS.FFITest;

/// <summary>
/// Safe wrapper around a Rust provider handle.
/// Each provider owns its own encrypted SQLite storage.
/// One process = one master key, multiple providers share it.
/// </summary>
internal sealed class MlsProvider : IDisposable
{
    public IntPtr Handle { get; private set; }
    private bool _disposed;

    public MlsProvider()
    {
        NativeMethods.NapMlsError err = default;
        Handle = NativeMethods.napmls_provider_new(&err);
        if (Handle == IntPtr.Zero)
            throw new InvalidOperationException($"Failed to create provider: {NativeMethods.GetErrorMessage(err)}");
    }

    public void Dispose()
    {
        if (!_disposed && Handle != IntPtr.Zero)
        {
            NativeMethods.napmls_provider_free(Handle);
            Handle = IntPtr.Zero;
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }

    ~MlsProvider() => Dispose();
}
