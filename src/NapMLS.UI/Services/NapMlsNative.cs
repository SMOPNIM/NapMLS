using System.Runtime.InteropServices;

namespace NapMLS.UI.Services;

/// <summary>
/// P/Invoke bindings for napmls_ffi.dll used by the UI.
/// Signatures MUST match native/napmls-ffi/src/ffi.rs exactly.
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
    public const int NAPMLS_ERR_KEY_MISMATCH = -7;

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

    // --- Init ---
    [LibraryImport(LibName)]
    public static unsafe partial int napmls_init(byte* encryptionKey, int keyLen);

    // --- Provider ---
    [LibraryImport(LibName)]
    public static unsafe partial IntPtr napmls_provider_new_from_file(
        byte* dbPath, NapMlsError* outError);

    [LibraryImport(LibName)]
    public static partial void napmls_provider_free(IntPtr provider);

    // --- Identity ---
    [LibraryImport(LibName)]
    public static unsafe partial int napmls_create_identity(
        IntPtr provider, byte* name, int nameLen,
        IntPtr* outIdentity, NapMlsError* outError);

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
    public static unsafe partial int napmls_identity_fingerprint(
        IntPtr identity, NapMlsBytes* outFingerprint, NapMlsError* outError);

    // --- Groups ---
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
    public static partial void napmls_group_free(IntPtr group);

    [LibraryImport(LibName)]
    public static unsafe partial int napmls_group_epoch(
        IntPtr group, ulong* outEpoch, NapMlsError* outError);

    [LibraryImport(LibName)]
    public static unsafe partial int napmls_group_id(
        IntPtr group, NapMlsBytes* outGroupId, NapMlsError* outError);

    [LibraryImport(LibName)]
    public static unsafe partial int napmls_group_members(
        IntPtr group, NapMlsBytes* outMembers, NapMlsError* outError);

    // --- Encrypt / Decrypt (take group handle, not group_id bytes) ---
    [LibraryImport(LibName)]
    public static unsafe partial int napmls_encrypt(
        IntPtr provider, IntPtr group, IntPtr identity,
        byte* plaintext, int plaintextLen,
        NapMlsBytes* outCiphertext, NapMlsError* outError);

    [LibraryImport(LibName)]
    public static unsafe partial int napmls_decrypt(
        IntPtr provider, IntPtr group,
        byte* ciphertext, int ciphertextLen,
        NapMlsBytes* outPlaintext, NapMlsError* outError);

    // --- Key Packages ---
    [LibraryImport(LibName)]
    public static unsafe partial int napmls_generate_key_package(
        IntPtr provider, IntPtr identity,
        NapMlsBytes* outKeyPackage, NapMlsError* outError);

    // --- Membership (take group handle, not groupId bytes) ---
    [LibraryImport(LibName)]
    public static unsafe partial int napmls_add_members(
        IntPtr provider, IntPtr group, IntPtr identity,
        byte* keyPackageData, int keyPackageLen,
        NapMlsBytes* outWelcome, NapMlsError* outError);

    [LibraryImport(LibName)]
    public static unsafe partial int napmls_process_welcome(
        IntPtr provider,
        byte* welcomeData, int welcomeLen,
        IntPtr* outGroup, NapMlsError* outError);

    [LibraryImport(LibName)]
    public static unsafe partial int napmls_remove_members(
        IntPtr provider, IntPtr group, IntPtr identity,
        uint removedLeafIndex,
        NapMlsError* outError);

    [LibraryImport(LibName)]
    public static unsafe partial int napmls_group_member_count(
        IntPtr group, uint* outCount, NapMlsError* outError);

    // --- Utilities ---
    [LibraryImport(LibName)]
    public static partial void napmls_free_bytes(NapMlsBytes bytes);

    [LibraryImport(LibName)]
    public static partial void napmls_free_error(NapMlsError error);
}
