using System.Text.Json;

namespace WaveTandem.Core;

public sealed record DevicePreference(string Name, OutputProfile Profile);
public sealed record ListeningSetup(string Name, bool FollowDefault, string? SourceId, string? SourceName,
    Dictionary<string, DevicePreference> Outputs);

public sealed record Preferences
{
    public int Version { get; init; } = 1;
    public string Language { get; init; } = "en";
    public string Theme { get; init; } = "light";
    public bool FollowDefault { get; init; }
    public bool CloseToTray { get; init; }
    public bool HasSeenWelcome { get; init; }
    public string? SourceId { get; init; }
    public string? SourceName { get; init; }
    public Dictionary<string, DevicePreference> Outputs { get; init; } = new(StringComparer.Ordinal);
    public List<ListeningSetup> Setups { get; init; } = [];
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
        return preferences with
        {
            Version = 1,
            Language = preferences.Language is "en" or "ru" or "uk" ? preferences.Language : "en",
            Theme = preferences.Theme == "dark" ? "dark" : "light",
            SourceId = IsValidId(preferences.SourceId) ? preferences.SourceId : null,
            SourceName = TrimName(preferences.SourceName, 256),
            Outputs = NormalizeOutputs(preferences.Outputs, 256),
            Setups = (preferences.Setups ?? [])
                .Where(setup => setup is not null && TrimName(setup.Name, 40) is not null)
                .Select(setup => setup with
                {
                    Name = TrimName(setup.Name, 40)!,
                    SourceId = IsValidId(setup.SourceId) ? setup.SourceId : null,
                    SourceName = TrimName(setup.SourceName, 256),
                    Outputs = NormalizeOutputs(setup.Outputs, 16)
                })
                .DistinctBy(setup => setup.Name, StringComparer.OrdinalIgnoreCase)
                .Take(12)
                .ToList()
        };
    }

    private static Dictionary<string, DevicePreference> NormalizeOutputs(Dictionary<string, DevicePreference>? outputs, int limit) =>
        (outputs ?? new())
            .Where(pair => IsValidId(pair.Key) && pair.Value?.Profile is not null)
            .Take(limit)
            .ToDictionary(pair => pair.Key, pair => new DevicePreference(
                TrimName(pair.Value.Name, 256) ?? pair.Key[..Math.Min(pair.Key.Length, 256)],
                pair.Value.Profile.Normalize()), StringComparer.Ordinal);

    private static string? TrimName(string? name, int limit)
    {
        var trimmed = name?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed[..Math.Min(trimmed.Length, limit)];
    }

    private static bool IsValidId(string? id) => id is { Length: > 0 and <= 2048 } && !id.Contains('\0');
}
