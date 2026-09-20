using System.Runtime.InteropServices;

namespace NapMLS.IntegrationTests;

/// <summary>
/// P/Invoke bindings for napmls-ffi.dll
/// All memory returned from Rust MUST be freed via the corresponding free function.
/// Length params use nuint to match Rust's usize (8 bytes on x64).
/// </summary>
internal static partial class NativeMethods
{
    private const string LibName = "napmls_ffi";

    [StructLayout(LayoutKind.Sequential)]
    public struct NapMlsBytes
    {
        public IntPtr ptr;
        public nuint len;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NapMlsError
    {
        public int code;
        public IntPtr message;
    }

    public const int NAPMLS_OK = 0;

    public static string? GetErrorMessage(NapMlsError error)
    {
        if (error.message == IntPtr.Zero) return null;
        return Marshal.PtrToStringUTF8(error.message);
    }

    public static void FreeError(ref NapMlsError error)
    {
        if (error.message != IntPtr.Zero)
        {
            napmls_free_error(error);
            error.message = IntPtr.Zero;
        }
    }

    public static byte[] ReadBytes(NapMlsBytes bytes)
    {
        if (bytes.ptr == IntPtr.Zero || bytes.len == 0)
            return [];
        var result = new byte[(int)bytes.len];
        Marshal.Copy(bytes.ptr, result, 0, (int)bytes.len);
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
    public static unsafe partial int napmls_init(byte* encryptionKey, nuint keyLen);

    [LibraryImport(LibName)]
    public static unsafe partial IntPtr napmls_provider_new(NapMlsError* outError);

    [LibraryImport(LibName)]
    public static partial void napmls_provider_free(IntPtr provider);

    [LibraryImport(LibName)]
    public static unsafe partial int napmls_create_identity(
        IntPtr provider,
        byte* name,
        nuint nameLen,
        IntPtr* outIdentity,
        NapMlsError* outError);

    [LibraryImport(LibName)]
    public static partial void napmls_identity_free(IntPtr identity);

    [LibraryImport(LibName)]
    public static unsafe partial int napmls_create_group(
        IntPtr provider,
        IntPtr identity,
        byte* groupName,
        nuint groupNameLen,
        IntPtr* outGroup,
        NapMlsError* outError);

    [LibraryImport(LibName)]
    public static partial void napmls_group_free(IntPtr group);

    [LibraryImport(LibName)]
    public static unsafe partial int napmls_load_group(
        IntPtr provider,
        byte* groupId,
        nuint groupIdLen,
        IntPtr* outGroup,
        NapMlsError* outError);

    [LibraryImport(LibName)]
    public static unsafe partial int napmls_list_groups(
        IntPtr provider,
        NapMlsBytes* outGroups,
        NapMlsError* outError);

    [LibraryImport(LibName)]
    public static unsafe partial int napmls_generate_key_package(
        IntPtr provider,
        IntPtr identity,
        NapMlsBytes* outKeyPackage,
        NapMlsError* outError);

    [LibraryImport(LibName)]
    public static unsafe partial int napmls_add_members(
        IntPtr provider,
        IntPtr group,
        IntPtr identity,
        byte* keyPackageData,
        nuint keyPackageLen,
        NapMlsBytes* outWelcome,
        NapMlsError* outError);

    [LibraryImport(LibName)]
    public static unsafe partial int napmls_process_welcome(
        IntPtr provider,
        byte* welcomeData,
        nuint welcomeLen,
        IntPtr* outGroup,
        NapMlsError* outError);

    [LibraryImport(LibName)]
    public static unsafe partial int napmls_encrypt(
        IntPtr provider,
        IntPtr group,
        IntPtr identity,
        byte* plaintext,
        nuint plaintextLen,
        NapMlsBytes* outCiphertext,
        NapMlsError* outError);

    [LibraryImport(LibName)]
    public static unsafe partial int napmls_decrypt(
        IntPtr provider,
        IntPtr group,
        byte* ciphertext,
        nuint ciphertextLen,
        NapMlsBytes* outPlaintext,
        NapMlsError* outError);

    [LibraryImport(LibName)]
    public static unsafe partial int napmls_remove_members(
        IntPtr provider,
        IntPtr group,
        IntPtr identity,
        uint removedLeafIndex,
        NapMlsError* outError);

    [LibraryImport(LibName)]
    public static unsafe partial int napmls_group_member_count(
        IntPtr group,
        uint* outCount,
        NapMlsError* outError);

    [LibraryImport(LibName)]
    public static unsafe partial int napmls_find_member_by_name(
        IntPtr group,
        byte* name,
        nuint nameLen,
        uint* outLeafIndex,
        NapMlsError* outError);

    [LibraryImport(LibName)]
    public static unsafe partial int napmls_identity_fingerprint(
        IntPtr identity,
        NapMlsBytes* outFingerprint,
        NapMlsError* outError);

    [LibraryImport(LibName)]
    public static unsafe partial int napmls_group_epoch(
        IntPtr group,
        ulong* outEpoch,
        NapMlsError* outError);

    [LibraryImport(LibName)]
    public static unsafe partial int napmls_group_id(
        IntPtr group,
        NapMlsBytes* outGroupId,
        NapMlsError* outError);

    [LibraryImport(LibName)]
    public static unsafe partial int napmls_group_members(
        IntPtr group,
        NapMlsBytes* outMembers,
        NapMlsError* outError);

    [LibraryImport(LibName)]
    public static partial void napmls_free_bytes(NapMlsBytes bytes);

    [LibraryImport(LibName)]
    public static partial void napmls_free_error(NapMlsError error);

    [LibraryImport(LibName)]
    public static partial void napmls_reset_counters();

    [LibraryImport(LibName)]
    public static unsafe partial void napmls_get_alloc_stats(
        nuint* outAllocCount,
        nuint* outFreeCount,
        nuint* outBytesAllocated,
        nuint* outBytesFreed);

    [LibraryImport(LibName)]
    public static unsafe partial IntPtr napmls_provider_new_from_file(
        byte* dbPath,
        NapMlsError* outError);

    [LibraryImport(LibName)]
    public static unsafe partial int napmls_register_identity(
        IntPtr provider,
        byte* username,
        nuint usernameLen,
        IntPtr identity,
        NapMlsError* outError);

    [LibraryImport(LibName)]
    public static unsafe partial int napmls_load_identity(
        IntPtr provider,
        byte* username,
        nuint usernameLen,
        IntPtr* outIdentity,
        NapMlsError* outError);
}
