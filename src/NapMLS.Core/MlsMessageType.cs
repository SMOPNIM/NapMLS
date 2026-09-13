namespace NapMLS.Core;

/// <summary>MLS message types for the [MLS:...] wire format.</summary>
public enum MlsMessageType
{
    MSG,        // Application message (always chunked)
    COMMIT,     // Commit message (may be chunked)
    WELCOME,    // Welcome message (usually single)
    KP,         // KeyPackage exchange (usually single)
    UPDATE,     // Same as COMMIT (may be chunked)
    RESYNC,     // Resync request (single)
    RESYNC_RESP // Resync response (chunked)
}
