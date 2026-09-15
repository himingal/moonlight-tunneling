using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MingalTunnel.Core;

public static class JsonStore
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public static T LoadOrDefault<T>(string path) where T : new()
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options) ?? new T();
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Couldn't read {Path.GetFileName(path)} ({ex.Message}); using defaults. The old file was kept as .broken.");
            try { File.Copy(path, path + ".broken", overwrite: true); } catch { }
        }
        return new T();
    }

    /// <summary>Write-then-rename so a crash mid-save never leaves a truncated settings file.</summary>
    public static void Save<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, Options), new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }
}
