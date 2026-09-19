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

    public static AppConfig Load(string? path = null)
    {
        var configPath = path ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
        try
        {
            if (File.Exists(configPath))
            {
                var json = File.ReadAllText(configPath);
                return JsonSerializer.Deserialize<AppConfig>(json, JsonOpts) ?? new AppConfig();
            }
        }
        catch
        {
            // Corrupted config — return defaults
        }
        return new AppConfig();
    }

    public static void Save(AppConfig config, string? path = null)
    {
        var configPath = path ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
        var dir = Path.GetDirectoryName(configPath);
        if (dir != null && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(config, JsonOpts);
        File.WriteAllText(configPath, json);
    }

    public static bool Exists(string? path = null)
    {
        var configPath = path ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
        return File.Exists(configPath);
    }
}
