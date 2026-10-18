using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bluey;

internal static class Log
{
    private static readonly string Path = System.IO.Path.Combine(Settings.Folder, "log.txt");
    public static void Write(string message)
    {
        try
        {
            Directory.CreateDirectory(Settings.Folder);
            File.AppendAllText(Path, $"{DateTime.Now:s} {message}{Environment.NewLine}");
        }
        catch { }
    }
}

/// Saved in %AppData%\Bluey\settings.json. The API key is encrypted for your Windows account (DPAPI).
internal sealed class Settings
{
    public static string Folder => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Bluey");
    private static string FilePath => System.IO.Path.Combine(Folder, "settings.json");

    public string? ProtectedKey { get; set; }
    /// Change this to try another Gemini model (for example if Google renames or retires one).
    public string Model { get; set; } = "gemini-flash-latest";
    public bool ComputerControl { get; set; } = true;
    public bool SendScreenshots { get; set; } = true;
    public string Personality { get; set; } = DefaultPersonality;

    public const string DefaultPersonality =
        "You are a small blueberry with big googly eyes who lives on an iPhone under the user's screen and has your own " +
        "cursor. You're a young British guy: dry, quick-witted, a bit cheeky, British phrasing. You never speak out loud: " +
        "your replies pop up as a tiny speech bubble, so keep them to one short line, under fifteen words. Answer " +
        "immediately with the actual answer. Never announce what you're going to do, never recap, never offer more help.";

    [JsonIgnore]
    public string? ApiKey
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
            if (!string.IsNullOrWhiteSpace(env)) return env.Trim();
            if (string.IsNullOrEmpty(ProtectedKey)) return null;
            try
            {
                var bytes = ProtectedData.Unprotect(Convert.FromBase64String(ProtectedKey), null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(bytes);
            }
            catch { return null; }
        }
        set
        {
            ProtectedKey = string.IsNullOrWhiteSpace(value) ? null
                : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value.Trim()), null, DataProtectionScope.CurrentUser));
            Save();
        }
    }

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch (Exception ex) { Log.Write("settings load failed: " + ex.Message); }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { Log.Write("settings save failed: " + ex.Message); }
    }
}
