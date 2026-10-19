using System.IO;
using System.Text.Json;
using Microsoft.Win32;
using OpenMessages.Core;

namespace OpenMessages.Windows;

/// <summary>Plain preferences in %APPDATA%\OpenMessages\settings.json.</summary>
public sealed class UserSettings
{
    public bool AutoCopy { get; set; } = true;
    public bool Notify { get; set; } = true;

    private static string FilePath => Path.Combine(AppConfig.DataDirectory, "settings.json");

    public static UserSettings Load()
    {
        try
        {
            return File.Exists(FilePath) ? JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(FilePath)) ?? new() : new();
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            return new();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(AppConfig.DataDirectory);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
    }

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "OpenMessages";

    /// <summary>Starts minimized to the tray at sign-in, via the per-user Run key.</summary>
    public static bool StartWithWindows
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(RunValue) != null;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) key.SetValue(RunValue, $"\"{Environment.ProcessPath}\" --minimized");
            else key.DeleteValue(RunValue, throwOnMissingValue: false);
        }
    }
}
