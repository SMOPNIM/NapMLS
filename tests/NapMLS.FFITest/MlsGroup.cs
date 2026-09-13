using System.Text;
using System.Text.Json;

namespace NapMLS.FFITest;

/// <summary>
/// Safe wrapper around a Rust group handle.
/// All mutating operations (add/remove) auto-merge pending commits internally.
/// </summary>
internal sealed class MlsGroup : IDisposable
{
    public IntPtr Handle { get; private set; }
    private bool _disposed;

    internal MlsGroup(IntPtr handle)
    {
        Handle = handle;
    }

    /// <summary>
    /// Create a new MLS group.
    /// </summary>
    public static MlsGroup Create(MlsProvider provider, MlsIdentity identity)
    {
        NativeMethods.NapMlsError err = default;
        IntPtr groupHandle;
        var rc = NativeMethods.napmls_create_group(provider.Handle, identity.Handle, &groupHandle, &err);
        if (rc != 0)
            throw new InvalidOperationException($"Failed to create group: {NativeMethods.GetErrorMessage(err)}");
        return new MlsGroup(groupHandle);
    }

    /// <summary>
    /// Add a member via their serialized KeyPackage. Returns the serialized Welcome.
    /// Internally merges the pending commit.
    /// </summary>
    public byte[] AddMember(MlsProvider provider, MlsIdentity identity, byte[] keyPackageBytes)
    {
        NativeMethods.NapMlsError err = default;
        NativeMethods.NapMlsBytes welcome = default;
        fixed (byte* pKp = keyPackageBytes)
        {
            var rc = NativeMethods.napmls_add_members(
                provider.Handle, Handle, identity.Handle,
                pKp, keyPackageBytes.Length, &welcome, &err);
            if (rc != 0)
                throw new InvalidOperationException($"Failed to add member: {NativeMethods.GetErrorMessage(err)}");
        }
        try
        {
            return NativeMethods.ReadBytes(welcome);
        }
        finally
        {
            NativeMethods.FreeBytes(ref welcome);
        }
    }

    /// <summary>
    /// Remove a member by their LeafNodeIndex.
    /// Internally merges the pending commit.
    /// </summary>
    public void RemoveMember(MlsProvider provider, MlsIdentity identity, uint leafIndex)
    {
        NativeMethods.NapMlsError err = default;
        var rc = NativeMethods.napmls_remove_members(
            provider.Handle, Handle, identity.Handle, leafIndex, &err);
        if (rc != 0)
            throw new InvalidOperationException($"Failed to remove member: {NativeMethods.GetErrorMessage(err)}");
    }

    /// <summary>
    /// Generate a key package for an identity (can be used with AddMember).
    /// </summary>
    public static byte[] GenerateKeyPackage(MlsProvider provider, MlsIdentity identity)
    {
        NativeMethods.NapMlsError err = default;
        NativeMethods.NapMlsBytes kp = default;
        var rc = NativeMethods.napmls_generate_key_package(provider.Handle, identity.Handle, &kp, &err);
        if (rc != 0)
            throw new InvalidOperationException($"Failed to generate key package: {NativeMethods.GetErrorMessage(err)}");
        try
        {
            return NativeMethods.ReadBytes(kp);
        }
        finally
        {
            NativeMethods.FreeBytes(ref kp);
        }
    }

    /// <summary>
    /// Process a Welcome message and join the group.
    /// </summary>
    public static MlsGroup JoinFromWelcome(MlsProvider provider, byte[] welcomeBytes)
    {
        NativeMethods.NapMlsError err = default;
        IntPtr groupHandle;
        fixed (byte* pWelcome = welcomeBytes)
        {
            var rc = NativeMethods.napmls_process_welcome(
                provider.Handle, pWelcome, welcomeBytes.Length, &groupHandle, &err);
            if (rc != 0)
                throw new InvalidOperationException($"Failed to process welcome: {NativeMethods.GetErrorMessage(err)}");
        }
        return new MlsGroup(groupHandle);
    }

    /// <summary>
    /// Encrypt an application message.
    /// </summary>
    public byte[] Encrypt(MlsProvider provider, MlsIdentity identity, byte[] plaintext)
    {
        NativeMethods.NapMlsError err = default;
        NativeMethods.NapMlsBytes cipher = default;
        fixed (byte* pPlain = plaintext)
        {
            var rc = NativeMethods.napmls_encrypt(
                provider.Handle, Handle, identity.Handle,
                pPlain, plaintext.Length, &cipher, &err);
            if (rc != 0)
                throw new InvalidOperationException($"Failed to encrypt: {NativeMethods.GetErrorMessage(err)}");
        }
        try
        {
            return NativeMethods.ReadBytes(cipher);
        }
        finally
        {
            NativeMethods.FreeBytes(ref cipher);
        }
    }

    /// <summary>
    /// Decrypt an application message.
    /// </summary>
    public byte[] Decrypt(MlsProvider provider, byte[] ciphertext)
    {
        NativeMethods.NapMlsError err = default;
        NativeMethods.NapMlsBytes plain = default;
        fixed (byte* pCipher = ciphertext)
        {
            var rc = NativeMethods.napmls_decrypt(
                provider.Handle, Handle, pCipher, ciphertext.Length, &plain, &err);
            if (rc != 0)
                throw new MlsDecryptException(rc, NativeMethods.GetErrorMessage(err));
        }
        try
        {
            return NativeMethods.ReadBytes(plain);
        }
        finally
        {
            NativeMethods.FreeBytes(ref plain);
        }
    }

    /// <summary>
    /// Get the current epoch.
    /// </summary>
    public ulong GetEpoch()
    {
        NativeMethods.NapMlsError err = default;
        ulong epoch;
        var rc = NativeMethods.napmls_group_epoch(Handle, &epoch, &err);
        if (rc != 0)
            throw new InvalidOperationException($"Failed to get epoch: {NativeMethods.GetErrorMessage(err)}");
        return epoch;
    }

    /// <summary>
    /// Get the member count.
    /// </summary>
    public uint GetMemberCount()
    {
        NativeMethods.NapMlsError err = default;
        uint count;
        var rc = NativeMethods.napmls_group_member_count(Handle, &count, &err);
        if (rc != 0)
            throw new InvalidOperationException($"Failed to get member count: {NativeMethods.GetErrorMessage(err)}");
        return count;
    }

    /// <summary>
    /// Get the list of members as JSON.
    /// </summary>
    public string GetMembersJson()
    {
        NativeMethods.NapMlsError err = default;
        NativeMethods.NapMlsBytes members = default;
        var rc = NativeMethods.napmls_group_members(Handle, &members, &err);
        if (rc != 0)
            throw new InvalidOperationException($"Failed to get members: {NativeMethods.GetErrorMessage(err)}");
        try
        {
            return Encoding.UTF8.GetString(NativeMethods.ReadBytes(members));
        }
        finally
        {
            NativeMethods.FreeBytes(ref members);
        }
    }

    /// <summary>
    /// Find a member's LeafNodeIndex by name.
    /// </summary>
    public uint FindMemberByName(string name)
    {
        var nameBytes = Encoding.UTF8.GetBytes(name);
        NativeMethods.NapMlsError err = default;
        uint leafIndex;
        fixed (byte* pName = nameBytes)
        {
            var rc = NativeMethods.napmls_find_member_by_name(
                Handle, pName, nameBytes.Length, &leafIndex, &err);
            if (rc != 0)
                throw new InvalidOperationException($"Member '{name}' not found");
        }
        return leafIndex;
    }

    public void Dispose()
    {
        if (!_disposed && Handle != IntPtr.Zero)
        {
            NativeMethods.napmls_group_free(Handle);
            Handle = IntPtr.Zero;
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }

    ~MlsGroup() => Dispose();
}

/// <summary>
/// Thrown when decryption fails (e.g., after eviction).
/// </summary>
internal sealed class MlsDecryptException : Exception
{
    public int ErrorCode { get; }

    public MlsDecryptException(int errorCode, string? message)
        : base($"Decryption failed (error {errorCode}): {message}")
    {
        ErrorCode = errorCode;
    }
}
