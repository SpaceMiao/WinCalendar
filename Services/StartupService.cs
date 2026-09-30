using System;
using System.Threading.Tasks;
using Microsoft.Win32;
using Windows.ApplicationModel;

namespace WinCalendar.Services;

internal static class StartupService
{
    internal static bool IsPackaged
    {
        get { try { _ = Package.Current.Id; return true; } catch (InvalidOperationException) { return false; } }
    }

    internal static async Task<bool> SetEnabledAsync(bool enabled)
    {
        if (IsPackaged)
        {
            var task = await StartupTask.GetAsync("WinCalendarStartup");
            if (!enabled) { task.Disable(); return false; }
            var state = await task.RequestEnableAsync();
            if (state is StartupTaskState.DisabledByUser or StartupTaskState.DisabledByPolicy)
                throw new InvalidOperationException("开机启动已被系统禁用，请在 Windows 设置 → 应用 → 启动中启用 WinCalendar。");
            return state is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;
        }
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (enabled) key.SetValue("WinCalendar", $"\"{Environment.ProcessPath}\" --startup");
        else key.DeleteValue("WinCalendar", false);
        return enabled;
    }
}
