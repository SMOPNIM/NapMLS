using System.Runtime.InteropServices;

namespace NapMLS.UI.Services;

/// <summary>
/// P/Invoke bindings for napmls_ffi.dll used by the UI.
/// Full set: init, provider, identity, groups, encrypt/decrypt.
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
        IntPtr provider, IntPtr identity, byte* groupId, int groupIdLen);

    // --- Encrypt / Decrypt ---
    [LibraryImport(LibName)]
    public static unsafe partial int napmls_encrypt(
        IntPtr provider, IntPtr identity,
        byte* groupId, int groupIdLen,
        byte* plaintext, int plaintextLen,
        NapMlsBytes* outCiphertext, NapMlsError* outError);

    [LibraryImport(LibName)]
    public static unsafe partial int napmls_decrypt(
        IntPtr provider, IntPtr identity,
        byte* groupId, int groupIdLen,
        byte* ciphertext, int ciphertextLen,
        NapMlsBytes* outPlaintext, NapMlsError* outError);

    // --- Key Packages ---
    [LibraryImport(LibName)]
    public static unsafe partial int napmls_generate_key_package(
        IntPtr provider, IntPtr identity,
        NapMlsBytes* outKeyPackage, NapMlsError* outError);

    // --- Membership ---
    [LibraryImport(LibName)]
    public static unsafe partial int napmls_add_members(
        IntPtr provider, IntPtr identity,
        byte* groupId, int groupIdLen,
        byte* keyPackages, int keyPackagesLen,
        NapMlsBytes* outWelcome, NapMlsError* outError);

    [LibraryImport(LibName)]
    public static unsafe partial int napmls_process_welcome(
        IntPtr provider, IntPtr identity,
        byte* welcomeData, int welcomeLen,
        byte* outGroupId, int outGroupIdLen,
        NapMlsError* outError);

    [LibraryImport(LibName)]
    public static unsafe partial int napmls_remove_members(
        IntPtr provider, IntPtr identity,
        byte* groupId, int groupIdLen,
        byte* memberKeys, int memberKeysLen,
        NapMlsError* outError);

    [LibraryImport(LibName)]
    public static unsafe partial int napmls_group_member_count(
        IntPtr provider, IntPtr identity, byte* groupId, int groupIdLen);

    // --- Utilities ---
    [LibraryImport(LibName)]
    public static partial void napmls_free_bytes(NapMlsBytes bytes);

    [LibraryImport(LibName)]
    public static partial void napmls_free_error(NapMlsError error);
}
