using System.Runtime.InteropServices;
using System.Text;

namespace NapMLS.UI.Services;

/// <summary>
/// P/Invoke bindings for napmls_ffi.dll used by the UI.
/// Each call is individually safe; no state carried between calls.
/// </summary>
internal static partial class NapMlsNative
{
    private const string LibName = "napmls_ffi";

    [StructLayout(LayoutKind.Sequential)]
    public struct NapMlsBytes
    {
        public IntPtr ptr;
        public int len;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NapMlsError
    {
        public int code;
        public IntPtr message;
    }

    public const int NAPMLS_OK = 0;
    public const int NAPMLS_ERR_NOT_FOUND = -6;

    public static byte[] ReadBytes(NapMlsBytes bytes)
    {
        if (bytes.ptr == IntPtr.Zero || bytes.len == 0) return [];
        var result = new byte[bytes.len];
        Marshal.Copy(bytes.ptr, result, 0, bytes.len);
        return result;
    }

    public static void FreeBytes(ref NapMlsBytes bytes)
    {
        if (bytes.ptr != IntPtr.Zero && bytes.len > 0)
        {
            napmls_free_bytes(bytes);
            bytes.ptr = IntPtr.Zero;
            bytes.len = 0;
        }
    }

    [LibraryImport(LibName)]
    public static unsafe partial int napmls_init(byte* encryptionKey, int keyLen);

    [LibraryImport(LibName)]
    public static unsafe partial IntPtr napmls_provider_new_from_file(
        byte* dbPath, NapMlsError* outError);

    [LibraryImport(LibName)]
    public static partial void napmls_provider_free(IntPtr provider);

    [LibraryImport(LibName)]
    public static unsafe partial int napmls_create_identity(
        IntPtr provider, byte* name, int nameLen,
        IntPtr* outIdentity, NapMlsError* outError);

    [LibraryImport(LibName)]
    public static unsafe partial int napmls_create_group(
        IntPtr provider, IntPtr identity,
        byte* groupName, int groupNameLen,
        IntPtr* outGroup, NapMlsError* outError);

    [LibraryImport(LibName)]
    public static unsafe partial int napmls_load_group(
        IntPtr provider, byte* groupId, int groupIdLen,
        IntPtr* outGroup, NapMlsError* outError);

    [LibraryImport(LibName)]
    public static unsafe partial int napmls_list_groups(
        IntPtr provider, NapMlsBytes* outGroups, NapMlsError* outError);

    [LibraryImport(LibName)]
    public static unsafe partial int napmls_load_identity(
        IntPtr provider, byte* username, int usernameLen,
        IntPtr* outIdentity, NapMlsError* outError);

    [LibraryImport(LibName)]
    public static unsafe partial int napmls_register_identity(
        IntPtr provider, byte* username, int usernameLen,
        IntPtr identity, NapMlsError* outError);

    [LibraryImport(LibName)]
    public static partial void napmls_identity_free(IntPtr identity);

    [LibraryImport(LibName)]
    public static partial void napmls_free_bytes(NapMlsBytes bytes);

    [LibraryImport(LibName)]
    public static unsafe partial int napmls_identity_fingerprint(
        IntPtr identity, NapMlsBytes* outFingerprint, NapMlsError* outError);
}
