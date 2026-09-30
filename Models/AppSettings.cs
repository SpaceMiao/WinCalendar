namespace WinCalendar.Models;

public sealed class AppSettings
{
    public const string DefaultIcsUrl = "https://chinacalendar.app/ics/china-calendar-2026.ics";
    public string Theme { get; set; } = "system";
    public bool Autostart { get; set; }
    public bool DesktopWidget { get; set; }
    public bool ReplaceTaskbar { get; set; } = true;
    public int Transparency { get; set; } = 15;
    public bool ShowFooter { get; set; } = true;
    public bool ShowFestivals { get; set; } = true;
    public bool ShowYiJi { get; set; } = true;
    public bool ShowCountdown { get; set; } = true;
    public string IcsUrl { get; set; } = DefaultIcsUrl;
    public bool CustomClock { get; set; }
    public string TimeFormat { get; set; } = "HH:mm:ss";
    public string DateFormat { get; set; } = "M月d日 ddd";
    public string? OriginalTimeFormat { get; set; }
    public string? OriginalDateFormat { get; set; }
    public int? OriginalShowSeconds { get; set; }
    public int? DesktopX { get; set; }
    public int? DesktopY { get; set; }
    public int? SettingsX { get; set; }
    public int? SettingsY { get; set; }
}
