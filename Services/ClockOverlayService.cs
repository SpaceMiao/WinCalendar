using System;
using System.Globalization;
using System.Threading;
using Microsoft.Win32;
using WinCalendar.Interop;
using WinCalendar.Models;

namespace WinCalendar.Services;

internal sealed class ClockOverlayService : IDisposable
{
    private const string InternationalKey = @"HKEY_CURRENT_USER\Control Panel\International";
    private const string ProfileInternationalKey = @"HKEY_CURRENT_USER\Control Panel\International\User Profile";
    private const string ExplorerAdvancedKey = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    private readonly SettingsStore _store;
    private string? _activeFormat;

    public ClockOverlayService(SettingsStore store) => _store = store;

    public static bool ValidFormat(string format)
    {
        try { return !string.IsNullOrWhiteSpace(format) && format.Length <= 80 && DateTime.Now.ToString(format, CultureInfo.GetCultureInfo("zh-CN")).Length <= 80; }
        catch (FormatException) { return false; }
    }

    public void Update(ClockArea[] clocks, AppSettings settings)
    {
        if (!settings.CustomClock || !ValidFormat(settings.TimeFormat) || !ValidFormat(settings.DateFormat))
        {
            RestoreSystemClock(clocks);
            return;
        }

        if (CaptureOriginalSystemClock()) _store.Save();

        var format = settings.TimeFormat + "\n" + settings.DateFormat;
        if (_activeFormat == format) return;
        WriteFormats(settings.TimeFormat, settings.DateFormat);
        _activeFormat = format;
        RefreshTaskbarClock(clocks);
    }

    public void Tick() { }

    private bool CaptureOriginalSystemClock()
    {
        var settings = _store.Value;
        if (!string.IsNullOrWhiteSpace(settings.OriginalTimeFormat) && !string.IsNullOrWhiteSpace(settings.OriginalDateFormat)) return false;
        settings.OriginalTimeFormat = ReadString(InternationalKey, "sShortTime", "HH:mm");
        settings.OriginalDateFormat = ReadString(InternationalKey, "sShortDate", "yyyy/M/d");
        settings.OriginalShowSeconds = ReadDword(ExplorerAdvancedKey, "ShowSecondsInSystemClock", 0);
        return true;
    }

    private void RestoreSystemClock(ClockArea[] clocks)
    {
        var settings = _store.Value;
        if (_activeFormat is null || string.IsNullOrWhiteSpace(settings.OriginalTimeFormat) || string.IsNullOrWhiteSpace(settings.OriginalDateFormat)) return;
        WriteFormats(settings.OriginalTimeFormat, settings.OriginalDateFormat, settings.OriginalShowSeconds ?? 0);
        _activeFormat = null;
        RefreshTaskbarClock(clocks);
    }

    private static void WriteFormats(string timeFormat, string dateFormat, int? showSeconds = null)
    {
        WriteString(InternationalKey, "sShortTime", timeFormat);
        WriteString(InternationalKey, "sTimeFormat", timeFormat);
        WriteString(InternationalKey, "sLongTime", timeFormat);
        WriteString(InternationalKey, "sShortDate", dateFormat);
        WriteString(InternationalKey, "sLongDate", dateFormat);
        WriteString(ProfileInternationalKey, "sShortTime", timeFormat);
        WriteString(ProfileInternationalKey, "sTimeFormat", timeFormat);
        WriteString(ProfileInternationalKey, "sLongTime", timeFormat);
        WriteString(ProfileInternationalKey, "sShortDate", dateFormat);
        WriteString(ProfileInternationalKey, "sLongDate", dateFormat);
        Registry.SetValue(ExplorerAdvancedKey, "ShowSecondsInSystemClock", showSeconds ?? (timeFormat.Contains(":s", StringComparison.Ordinal) ? 1 : 0), RegistryValueKind.DWord);
        Native.SendMessageTimeout(new nint(0xffff), 0x1A, 0, "intl", 2, 1000, out _);
    }

    private static void RefreshTaskbarClock(ClockArea[] clocks)
    {
        // Explorer caches the date line separately from regional settings. Toggling its
        // seconds setting forces that cache to be rebuilt without restarting Explorer.
        var currentSeconds = ReadDword(ExplorerAdvancedKey, "ShowSecondsInSystemClock", 0);
        Registry.SetValue(ExplorerAdvancedKey, "ShowSecondsInSystemClock", currentSeconds == 0 ? 1 : 0, RegistryValueKind.DWord);
        Thread.Sleep(100);
        Registry.SetValue(ExplorerAdvancedKey, "ShowSecondsInSystemClock", currentSeconds, RegistryValueKind.DWord);
        Native.SendMessageTimeout(new nint(0xffff), 0x1A, 0, "intl", 2, 1000, out _);
        foreach (var clock in clocks)
        {
            Native.InvalidateRect(clock.Taskbar, 0, true);
            Native.UpdateWindow(clock.Taskbar);
        }
    }

    private static string ReadString(string key, string name, string fallback) => Registry.GetValue(key, name, fallback) as string ?? fallback;
    private static int ReadDword(string key, string name, int fallback) => Registry.GetValue(key, name, fallback) is int value ? value : fallback;
    private static void WriteString(string key, string name, string value) => Registry.SetValue(key, name, value, RegistryValueKind.String);

    public void Dispose()
    {
        RestoreSystemClock([]);
        _store.Value.OriginalTimeFormat = null;
        _store.Value.OriginalDateFormat = null;
        _store.Value.OriginalShowSeconds = null;
        _store.Save();
    }
}
