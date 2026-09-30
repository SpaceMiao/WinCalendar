using System;
using System.IO;
using System.Text.Json;
using WinCalendar.Models;

namespace WinCalendar.Services;

public sealed class SettingsStore
{
    public static string DataDirectory { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinCalendar");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public AppSettings Value { get; }
    public event Action? Changed;

    public SettingsStore()
    {
        Directory.CreateDirectory(DataDirectory);
        try
        {
            Value = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path.Combine(DataDirectory, "settings.json"))) ?? new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Value = new();
            if (ex is not FileNotFoundException) Log(ex);
        }
        Value.Transparency = Math.Clamp(Value.Transparency, 0, 100);
        if (string.IsNullOrWhiteSpace(Value.IcsUrl)) Value.IcsUrl = AppSettings.DefaultIcsUrl;
    }

    public void Save()
    {
        WriteAtomic(Path.Combine(DataDirectory, "settings.json"), JsonSerializer.Serialize(Value, JsonOptions));
        Changed?.Invoke();
    }

    internal static void WriteAtomic(string path, string contents)
    {
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, contents);
        File.Move(temporary, path, true);
    }

    public static void Log(Exception ex)
    {
        try { File.AppendAllText(Path.Combine(DataDirectory, "app.log"), $"{DateTime.Now:O} {ex}\n"); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
