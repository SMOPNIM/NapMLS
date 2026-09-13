namespace NapMLS.FFITest;

/// <summary>
/// High-level MLS client with serialized FFI access.
/// All FFI calls are serialized through a SemaphoreSlim(1,1).
/// Usage:
///   using var client = new MlsClient();
///   client.Initialize();
///   var alice = client.CreateIdentity("Alice");
///   ...
/// </summary>
internal sealed class MlsClient : IDisposable
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private MlsProvider? _provider;
    private bool _disposed;

    /// <summary>
    /// Initialize the global encryption key and create the provider.
    /// Call once at startup.
    /// </summary>
    public void Initialize(byte[]? encryptionKey = null)
    {
        var key = encryptionKey ?? new byte[32];
        if (encryptionKey == null)
            Random.Shared.NextBytes(key);

        unsafe
        {
            fixed (byte* pKey = key)
            {
                var rc = NativeMethods.napmls_init(pKey, key.Length);
                if (rc != 0)
                    throw new InvalidOperationException("Failed to initialize encryption key");
            }
        }

        _provider = new MlsProvider();
    }

    /// <summary>
    /// Create a new identity. Thread-safe.
    /// </summary>
    public MlsIdentity CreateIdentity(string name)
    {
        EnsureInitialized();
        _lock.Wait();
        try
        {
            return new MlsIdentity(_provider!, name);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Create a new group. Thread-safe.
    /// </summary>
    public MlsGroup CreateGroup(MlsIdentity identity)
    {
        EnsureInitialized();
        _lock.Wait();
        try
        {
            return MlsGroup.Create(_provider!, identity);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Generate a key package for an identity. Thread-safe.
    /// </summary>
    public byte[] GenerateKeyPackage(MlsIdentity identity)
    {
        EnsureInitialized();
        _lock.Wait();
        try
        {
            return MlsGroup.GenerateKeyPackage(_provider!, identity);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Add a member to a group. Returns the Welcome to send.
    /// Thread-safe. Auto-merges pending commit.
    /// </summary>
    public byte[] AddMember(MlsGroup group, MlsIdentity adderIdentity, byte[] keyPackage)
    {
        EnsureInitialized();
        _lock.Wait();
        try
        {
            return group.AddMember(_provider!, adderIdentity, keyPackage);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Process a Welcome and join the group. Thread-safe.
    /// </summary>
    public MlsGroup ProcessWelcome(byte[] welcomeBytes)
    {
        EnsureInitialized();
        _lock.Wait();
        try
        {
            return MlsGroup.JoinFromWelcome(_provider!, welcomeBytes);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Encrypt a message. Thread-safe.
    /// </summary>
    public byte[] Encrypt(MlsGroup group, MlsIdentity identity, byte[] plaintext)
    {
        EnsureInitialized();
        _lock.Wait();
        try
        {
            return group.Encrypt(_provider!, identity, plaintext);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Decrypt a message. Thread-safe.
    /// Throws MlsDecryptException on failure (e.g., after eviction).
    /// </summary>
    public byte[] Decrypt(MlsGroup group, byte[] ciphertext)
    {
        EnsureInitialized();
        _lock.Wait();
        try
        {
            return group.Decrypt(_provider!, ciphertext);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Remove a member from a group. Thread-safe. Auto-merges pending commit.
    /// </summary>
    public void RemoveMember(MlsGroup group, MlsIdentity identity, uint leafIndex)
    {
        EnsureInitialized();
        _lock.Wait();
        try
        {
            group.RemoveMember(_provider!, identity, leafIndex);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Get the group epoch. Thread-safe.
    /// </summary>
    public ulong GetEpoch(MlsGroup group)
    {
        _lock.Wait();
        try
        {
            return group.GetEpoch();
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Get the member count. Thread-safe.
    /// </summary>
    public uint GetMemberCount(MlsGroup group)
    {
        _lock.Wait();
        try
        {
            return group.GetMemberCount();
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Get members as JSON. Thread-safe.
    /// </summary>
    public string GetMembersJson(MlsGroup group)
    {
        _lock.Wait();
        try
        {
            return group.GetMembersJson();
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Find a member's LeafNodeIndex by name. Thread-safe.
    /// </summary>
    public uint FindMemberByName(MlsGroup group, string name)
    {
        _lock.Wait();
        try
        {
            return group.FindMemberByName(name);
        }
        finally
        {
            _lock.Release();
        }
    }

    private void EnsureInitialized()
    {
        if (_provider == null)
            throw new InvalidOperationException("Call Initialize() first");
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _provider?.Dispose();
            _lock.Dispose();
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }

    ~MlsClient() => Dispose();
}
