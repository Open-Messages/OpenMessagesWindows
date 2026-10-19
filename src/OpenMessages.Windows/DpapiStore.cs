using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using OpenMessages.Core;

namespace OpenMessages.Windows;

/// <summary>
/// Stores values in %APPDATA%\OpenMessages, encrypted with DPAPI for the current Windows user, so
/// other accounts on the PC (and copies of the files) can't read the session or sync key.
/// </summary>
public sealed class DpapiStore : ISecureStore
{
    private static readonly byte[] Entropy = "OpenMessages.Windows.v1"u8.ToArray();

    public T? Load<T>(string name)
    {
        try
        {
            var path = PathFor(name);
            if (!File.Exists(path)) return default;
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<T>(plain);
        }
        catch (Exception e) when (e is CryptographicException or JsonException or IOException)
        {
            return default;
        }
    }

    public void Save<T>(string name, T value)
    {
        Directory.CreateDirectory(AppConfig.DataDirectory);
        var plain = JsonSerializer.SerializeToUtf8Bytes(value);
        File.WriteAllBytes(PathFor(name), ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser));
    }

    // File.Delete ignores a missing file but throws if the directory is missing (first run).
    public void Delete(string name)
    {
        var path = PathFor(name);
        if (File.Exists(path)) File.Delete(path);
    }

    private static string PathFor(string name) => Path.Combine(AppConfig.DataDirectory, $"{name}.bin");
}
