using System.Text.Json;

namespace WaveTandem.Core;

public sealed record DevicePreference(string Name, OutputProfile Profile);

public sealed record Preferences
{
    public int Version { get; init; } = 1;
    public string Language { get; init; } = "en";
    public string Theme { get; init; } = "system";
    public bool CloseToTray { get; init; }
    public bool HasSeenWelcome { get; init; }
    public string? SourceId { get; init; }
    public string? SourceName { get; init; }
    public Dictionary<string, DevicePreference> Outputs { get; init; } = new(StringComparer.Ordinal);
}

public sealed record SettingsResult(Preferences Preferences, string? Error, bool CanSave);

public sealed class SettingsStore(string path)
{
    private const long MaxSettingsBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, MaxDepth = 12 };

    public SettingsResult Load()
    {
        try
        {
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (input.Length > MaxSettingsBytes) throw new InvalidDataException("Settings file exceeds size limit.");
            var settings = JsonSerializer.Deserialize<Preferences>(input, JsonOptions)
                ?? throw new InvalidDataException("Settings file is empty.");
            if (settings.Version > 1) return new(new(), "Unsupported settings version.", false);
            return new(Sanitize(settings), null, true);
        }
        catch (FileNotFoundException) { return new(new(), null, true); }
        catch (DirectoryNotFoundException) { return new(new(), null, true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            // Keep the existing file available for recovery.
            return new(new(), error.Message, false);
        }
    }

    public void Save(Preferences preferences)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(folder);
        var temporary = Path.Combine(folder, $".settings-{Guid.NewGuid():N}.tmp");
        try
        {
            var body = JsonSerializer.SerializeToUtf8Bytes(Sanitize(preferences), JsonOptions);
            if (body.Length > MaxSettingsBytes) throw new InvalidDataException("Settings exceed size limit.");
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                file.Write(body);
                file.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private static Preferences Sanitize(Preferences preferences)
    {
        var outputs = (preferences.Outputs ?? new())
            .Where(pair => IsValidId(pair.Key) && pair.Value?.Profile is not null)
            .Take(256)
            .ToDictionary(pair => pair.Key, pair => new DevicePreference(
                string.IsNullOrWhiteSpace(pair.Value.Name) ? pair.Key :
                    pair.Value.Name[..Math.Min(pair.Value.Name.Length, 256)],
                pair.Value.Profile.Normalize()), StringComparer.Ordinal);
        return preferences with
        {
            Version = 1,
            Language = preferences.Language is "en" or "ru" or "uk" ? preferences.Language : "en",
            Theme = preferences.Theme is "light" or "dark" or "system" ? preferences.Theme : "system",
            SourceId = IsValidId(preferences.SourceId) ? preferences.SourceId : null,
            SourceName = preferences.SourceName is { Length: > 0 and <= 256 } ? preferences.SourceName : null,
            Outputs = outputs
        };
    }

    private static bool IsValidId(string? id) => id is { Length: > 0 and <= 2048 } && !id.Contains('\0');
}
