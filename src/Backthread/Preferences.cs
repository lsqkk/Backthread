using System.Security;
using System.Text.Json;
using Microsoft.Win32;

namespace Backthread;

internal sealed record Preferences(char SwitchKey = 'J', char SaveKey = 'K',
    bool CloseToTray = true, bool CheckOnLaunch = false, string Theme = "Default", bool Welcomed = false)
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Backthread", "settings.json");
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    internal static Preferences Load()
    {
        try
        {
            var value = JsonSerializer.Deserialize<Preferences>(File.ReadAllText(FilePath)) ?? new();
            return value with
            {
                SwitchKey = IsKey(value.SwitchKey) && IsKey(value.SaveKey) && value.SwitchKey != value.SaveKey ? value.SwitchKey : 'J',
                SaveKey = IsKey(value.SwitchKey) && IsKey(value.SaveKey) && value.SwitchKey != value.SaveKey ? value.SaveKey : 'K',
                Theme = value.Theme is "Light" or "Dark" ? value.Theme : "Default"
            };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return new();
        }
    }

    internal void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this));
        File.Move(temp, FilePath, true);
    }

    internal static bool IsKey(char key) => key is >= 'A' and <= 'Z';

    internal static bool StartsWithWindows()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue("Backthread") is string;
        }
        catch (Exception error) when (error is UnauthorizedAccessException or SecurityException or IOException)
        {
            return false;
        }
    }

    internal static void SetStartup(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
            key.SetValue("Backthread", $"\"{Path.Combine(AppContext.BaseDirectory, "Backthread.exe")}\" --background");
        else
            key.DeleteValue("Backthread", false);
    }
}
