using System.Text.Json;
using NapMLS.UI.Models;

namespace NapMLS.UI.Services;

/// <summary>
/// Reads and writes config.json in the app directory.
/// Used for NapCat config and identity persistence.
/// Does NOT store secrets (master key, private keys).
/// </summary>
public static class AppConfigService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
    };

    private static string ConfigPath =>
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                return JsonSerializer.Deserialize<AppConfig>(json, JsonOpts) ?? new AppConfig();
            }
        }
        catch
        {
            // Corrupted config — return defaults
        }
        return new AppConfig();
    }

    public static void Save(AppConfig config)
    {
        var json = JsonSerializer.Serialize(config, JsonOpts);
        File.WriteAllText(ConfigPath, json);
    }

    public static bool Exists() => File.Exists(ConfigPath);
}
