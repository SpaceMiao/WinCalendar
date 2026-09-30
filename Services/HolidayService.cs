using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using WinCalendar.Models;
using IcsCalendar = Ical.Net.Calendar;

namespace WinCalendar.Services;

public enum HolidayKind { Event, Rest, Work }
public sealed record HolidayEntry(DateTime Start, DateTime End, string Name, HolidayKind Kind);

public sealed class HolidayService : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(25), MaxResponseContentBufferSize = 4 * 1024 * 1024 };
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly object _gate = new();
    private readonly Dictionary<(DateTime, DateTime), IReadOnlyList<HolidayEntry>> _ranges = new();
    private IcsCalendar? _calendar;
    private string? _loadedUrl;
    public string Status { get; private set; } = "尚未更新";
    public event Action? Changed;

    public void LoadCache(string url)
    {
        lock (_gate)
        {
            if (_loadedUrl == url) return;
            _loadedUrl = url;
            _calendar = null;
            _ranges.Clear();
            var path = CachePath(url);
            try
            {
                if (File.Exists(path))
                {
                    _calendar = Parse(File.ReadAllText(path));
                    Status = $"已读取缓存 · {File.GetLastWriteTime(path):yyyy-MM-dd HH:mm}";
                }
                else if (url == AppSettings.DefaultIcsUrl)
                {
                    _calendar = Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Assets", "china-calendar-2026.ics")));
                    Status = "使用内置2026年数据，等待更新";
                }
                else Status = "新数据源尚未下载";
            }
            catch (Exception ex)
            {
                SettingsStore.Log(ex);
                Status = "本地数据不可用，等待下载";
            }
        }
        Changed?.Invoke();
    }

    public async Task RefreshAsync(string url)
    {
        LoadCache(url);
        await _refreshLock.WaitAsync();
        try
        {
            if (_loadedUrl != url) return;
            Status = "正在更新…";
            Changed?.Invoke();
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                throw new ArgumentException("请输入有效的 HTTP 或 HTTPS 地址");
            var text = await _http.GetStringAsync(uri);
            var parsed = await Task.Run(() => Parse(text));
            SettingsStore.WriteAtomic(CachePath(url), text);
            lock (_gate)
            {
                // A settings change during a download must not overwrite the new source.
                if (_loadedUrl != url) return;
                _calendar = parsed;
                _ranges.Clear();
            }
            Status = $"最近更新: {DateTime.Now:yyyy-MM-dd HH:mm} · {parsed.Events.Count} 个事件";
        }
        catch (Exception ex)
        {
            SettingsStore.Log(ex);
            if (_loadedUrl == url)
                Status = $"更新失败，{(_calendar is null ? "暂无可用数据" : "继续使用本地数据")}：{ex.Message}";
        }
        finally
        {
            _refreshLock.Release();
            Changed?.Invoke();
        }
    }

    private static IcsCalendar Parse(string text)
    {
        if (!text.Contains("BEGIN:VCALENDAR", StringComparison.OrdinalIgnoreCase) || !text.Contains("END:VCALENDAR", StringComparison.OrdinalIgnoreCase))
            throw new FormatException("该地址返回的内容不是完整的 ICS 日历");
        var calendar = IcsCalendar.Load(text) ?? throw new FormatException("无法读取 ICS 数据");
        if (calendar.Events.Count > 10000) throw new FormatException("数据源事件过多（上限10000条）");
        if (calendar.Events.Any(e => e.DtStart is null)) throw new FormatException("ICS 事件缺少开始日期");
        return calendar;
    }

    public IReadOnlyList<HolidayEntry> GetEntries(DateTime from, DateTime to)
    {
        lock (_gate)
        {
            if (_calendar is null) return Array.Empty<HolidayEntry>();
            if (_ranges.TryGetValue((from, to), out var cached)) return cached;
            var result = new List<HolidayEntry>();
            try
            {
                // Ical.Net handles folded lines, escaping, exclusive DTEND, RRULE and exceptions.
                foreach (var occurrence in _calendar.GetOccurrences<CalendarEvent>(new CalDateTime(DateOnly.FromDateTime(from)))
                    .TakeWhile(o => o.Period.StartTime.Value < to.AddDays(1)).Take(10001))
                {
                    if (result.Count >= 10000) throw new FormatException("指定日期范围内的事件过多");
                    if (occurrence.Source is not CalendarEvent ev || ev.Status == "CANCELLED") continue;
                    var start = LocalDateTime(occurrence.Period.StartTime);
                    var end = occurrence.Period.EffectiveEndTime is { } ending ? LocalDateTime(ending) : start.AddDays(1);
                    if (end <= start) end = start.AddDays(1);
                    if (end <= from || start >= to) continue;
                    var title = ev.Summary?.Trim() ?? "未命名事件";
                    var kind = title.StartsWith("休｜") || title.StartsWith("休|") ? HolidayKind.Rest
                        : title.StartsWith("班｜") || title.StartsWith("班|") ? HolidayKind.Work : HolidayKind.Event;
                    if (kind != HolidayKind.Event) title = title[2..].Trim();
                    result.Add(new(start.Date, end.TimeOfDay == TimeSpan.Zero ? end.Date : end.Date.AddDays(1), title, kind));
                }
            }
            catch (Exception ex)
            {
                SettingsStore.Log(ex);
                Status = "部分ICS事件无法解析：" + ex.Message;
            }
            if (_ranges.Count > 36) _ranges.Clear();
            _ranges[(from, to)] = result;
            return result;
        }
    }

    private static DateTime LocalDateTime(CalDateTime date) => !date.HasTime || date.IsFloating ? date.Value : date.AsUtc.ToLocalTime();

    public string Countdown(DateTime today)
    {
        var entries = GetEntries(today, today.AddYears(2));
        bool IsWork(DateTime date) => entries.Any(e => e.Kind == HolidayKind.Work && e.Start <= date && date < e.End);
        var rests = entries.Where(e => e.Kind == HolidayKind.Rest).OrderBy(e => e.Start).ToList();
        var active = rests.FirstOrDefault(e => e.Start <= today && today < e.End);
        if (active is not null && !IsWork(today)) return $"{active.Name} · 正在休假";
        foreach (var rest in rests)
        {
            var start = rest.Start <= today ? today.AddDays(1) : rest.Start;
            while (start < rest.End && IsWork(start)) start = start.AddDays(1);
            if (start < rest.End) return $"距离 {start:yyyy年M月d日} {rest.Name} 还有 {(start - today).Days} 天";
        }
        return "暂无后续假期数据";
    }

    private static string CachePath(string url) => Path.Combine(SettingsStore.DataDirectory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url))) + ".ics");
    public void Dispose() => _http.Dispose();
}
