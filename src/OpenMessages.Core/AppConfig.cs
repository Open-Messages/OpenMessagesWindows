using System.Text.Json;

namespace OpenMessages.Core;

/// <summary>
/// Firebase project values plus a Google "Desktop app" OAuth client, read from firebase-config.json.
/// See README.md for where each value comes from.
/// </summary>
public sealed record AppConfig(string ApiKey, string ProjectId, string GoogleClientId, string GoogleClientSecret)
{
    public const string FileName = "firebase-config.json";

    public static string DataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OpenMessages");

    /// <summary>Looks next to the executable first, then in %APPDATA%\OpenMessages.</summary>
    public static AppConfig Load()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, FileName),
            Path.Combine(DataDirectory, FileName),
        };
        var path = candidates.FirstOrDefault(File.Exists)
            ?? throw new ConfigException($"{FileName} not found. Put it next to OpenMessages.exe or in {DataDirectory}.");
        return Parse(File.ReadAllText(path));
    }

    public static AppConfig Parse(string json)
    {
        JsonElement root;
        try
        {
            root = JsonDocument.Parse(json).RootElement;
        }
        catch (JsonException e)
        {
            throw new ConfigException($"{FileName} isn't valid JSON: {e.Message}");
        }
        string Value(string name) =>
            root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s
                ? s
                : throw new ConfigException($"{FileName} is missing \"{name}\".");
        return new AppConfig(Value("apiKey"), Value("projectId"), Value("googleClientId"), Value("googleClientSecret"));
    }
}

public sealed class ConfigException(string message) : Exception(message);
