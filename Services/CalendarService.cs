using System;
using System.Collections.Generic;
using System.Linq;
using Lunar;

namespace WinCalendar.Services;

public sealed record DayDetails(string LunarText, string Term, IReadOnlyList<string> Festivals, string Yi, string Ji);

public static class CalendarService
{
    private static readonly Dictionary<DateTime, DayDetails> Cache = new();
    public static DayDetails Get(DateTime date)
    {
        if (Cache.TryGetValue(date.Date, out var value)) return value;
        var solar = Solar.FromDate(date);
        var lunar = solar.Lunar;
        var festivals = solar.Festivals.Concat(lunar.Festivals).Distinct().ToList();
        var term = lunar.JieQi;
        if (!string.IsNullOrEmpty(term)) festivals.Insert(0, term);
        var text = !string.IsNullOrEmpty(term) ? term : lunar.Day == 1 ? lunar.MonthInChinese + "月" : festivals.FirstOrDefault(f => f.Length <= 3) ?? lunar.DayInChinese;
        value = new(text, term, festivals, string.Join(" · ", lunar.DayYi), string.Join(" · ", lunar.DayJi));
        if (Cache.Count > 2000) Cache.Clear();
        Cache[date.Date] = value;
        return value;
    }
}
