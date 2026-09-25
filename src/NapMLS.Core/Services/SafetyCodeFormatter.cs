namespace NapMLS.Core.Services;

/// <summary>
/// Formats raw fingerprint bytes into NAPMLS-XXXX-XXXX-XXXX-XXXX safety code.
/// </summary>
public static class SafetyCodeFormatter
{
    /// <summary>
    /// Format 8 raw bytes into "NAPMLS-XXXX-XXXX-XXXX-XXXX".
    /// Input must be exactly 8 bytes (from SHA-256 prefix).
    /// </summary>
    public static string Format(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 8)
            throw new ArgumentException("Fingerprint must be at least 8 bytes");

        var hex = Convert.ToHexString(bytes[..8]).ToUpperInvariant();
        // hex = 16 chars, e.g. "A1B2C3D4E5F67890"
        return $"NAPMLS-{hex[..4]}-{hex[4..8]}-{hex[8..12]}-{hex[12..16]}";
    }

    /// <summary>
    /// Format from hex string (e.g. "A1B2C3D4E5F67890").
    /// </summary>
    public static string FormatFromHex(string hex)
    {
        if (hex.Length < 16)
            throw new ArgumentException("Hex string must be at least 16 characters");
        return $"NAPMLS-{hex[..4].ToUpper()}-{hex[4..8].ToUpper()}-{hex[8..12].ToUpper()}-{hex[12..16].ToUpper()}";
    }

    /// <summary>
    /// Validate a safety code format. Returns true if matches NAPMLS-XXXX-XXXX-XXXX-XXXX.
    /// </summary>
    public static bool IsValid(string code)
    {
        if (string.IsNullOrEmpty(code)) return false;
        if (code.Length != 26) return false; // "NAPMLS-" (7) + 4*4 (16) + 3 dashes (3) = 26
        if (!code.StartsWith("NAPMLS-")) return false;

        var parts = code.Split('-');
        if (parts.Length != 5) return false;
        if (parts[0] != "NAPMLS") return false;

        for (int i = 1; i <= 4; i++)
        {
            if (parts[i].Length != 4) return false;
            foreach (char c in parts[i])
            {
                if (!Uri.IsHexDigit(c)) return false;
            }
        }
        return true;
    }
}
