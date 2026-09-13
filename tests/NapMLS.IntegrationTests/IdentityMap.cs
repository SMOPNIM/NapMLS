namespace NapMLS.IntegrationTests;

/// <summary>
/// Maps QQ numbers to MLS identities and key packages.
/// In-memory for P1c-4; will be persisted to SQLite in P1d.
/// </summary>
public sealed class IdentityMap
{
    private readonly string _myQq;
    private readonly MlsClient _client;

    // peer fingerprint → KeyPackage bytes
    private readonly Dictionary<string, byte[]> _peerKeyPackages = new();

    // peer QQ → fingerprint (set after verification)
    private readonly Dictionary<string, string> _trustedPeers = new();

    public string MyQq => _myQq;
    public MlsClient Client => _client;

    public IdentityMap(string qqNumber, MlsClient client)
    {
        _myQq = qqNumber;
        _client = client;
    }

    /// <summary>Register a peer's KeyPackage (received via KP_RESPONSE).</summary>
    public void RegisterKeyPackage(string peerQq, byte[] keyPackage)
    {
        _peerKeyPackages[peerQq] = keyPackage;
    }

    /// <summary>Register a trusted peer's fingerprint (after out-of-band verification).</summary>
    public void TrustPeer(string peerQq, string fingerprint)
    {
        _trustedPeers[peerQq] = fingerprint;
    }

    /// <summary>Get a peer's KeyPackage for adding to a group.</summary>
    public byte[]? GetKeyPackage(string peerQq)
    {
        return _peerKeyPackages.TryGetValue(peerQq, out var kp) ? kp : null;
    }

    /// <summary>Get all KeyPackages as an array (for AddMembers).</summary>
    public byte[][] GetAllKeyPackages(string peerQq)
    {
        var kp = GetKeyPackage(peerQq);
        return kp != null ? [kp] : [];
    }

    /// <summary>Check if a peer is trusted.</summary>
    public bool IsTrusted(string peerQq)
    {
        return _trustedPeers.ContainsKey(peerQq);
    }

    /// <summary>Get peer's trusted fingerprint.</summary>
    public string? GetFingerprint(string peerQq)
    {
        return _trustedPeers.TryGetValue(peerQq, out var fp) ? fp : null;
    }
}
