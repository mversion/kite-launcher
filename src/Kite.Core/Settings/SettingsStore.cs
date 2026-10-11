using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kite.Core.Settings;

public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
    };

    public static T? Load<T>(string path) where T : class
    {
        if (!File.Exists(path)) return null;
        using var s = File.OpenRead(path);
        return JsonSerializer.Deserialize<T>(s, Options);
    }

    /// <summary>Writes atomically (temp file + replace) so a crash never leaves a half-written file.</summary>
    public static void Save<T>(string path, T value)
    {
        var tmp = path + ".tmp";
        using (var s = File.Create(tmp))
            JsonSerializer.Serialize(s, value, Options);
        File.Move(tmp, path, overwrite: true);
    }
}

public sealed class SettingsStore(KitePaths paths)
{
    public KitePaths Paths { get; } = paths;
    public AppSettings Current { get; private set; } = new();
    public bool ExistedOnLoad { get; private set; }

    public AppSettings Load()
    {
        ExistedOnLoad = File.Exists(Paths.SettingsFile);
        Current = Json.Load<AppSettings>(Paths.SettingsFile) ?? new AppSettings();
        return Current;
    }

    /// <summary>Raised after Kite itself writes settings.json (lets file watchers ignore our own writes).</summary>
    public event Action? Saved;

    public void Save()
    {
        Json.Save(Paths.SettingsFile, Current);
        Saved?.Invoke();
    }

    public void Replace(AppSettings settings)
    {
        Current = settings;
        Save();
    }
}
