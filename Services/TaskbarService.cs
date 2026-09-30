using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using UIAutomationCore.Interop;
using Microsoft.UI.Dispatching;
using WinCalendar.Interop;

namespace WinCalendar.Services;

internal sealed record ClockArea(nint Taskbar, Native.Rect Bounds);

internal sealed class TaskbarService : IDisposable
{
    private readonly DispatcherQueue _dispatcher;
    private readonly Native.HookProc _hookProc;
    private readonly Native.WndProc _hostProc;
    private readonly CancellationTokenSource _stop = new();
    private volatile ClockArea[] _clocks = [];
    private volatile bool _enabled = true;
    private nint _hook, _host;
    private uint _hookThread;
    private bool _leftCaptured, _rightCaptured;
    private Native.NotifyIcon _icon;
    private bool _ownsIcon;
    private readonly uint _taskbarCreated = Native.RegisterWindowMessage("TaskbarCreated");
    internal const uint ShowSettingsMessage = 0x8002;
    public event Action<ClockArea, bool>? ClockClicked;
    public event Action<ClockArea>? ClockPointerEntered;
    public event Action? ClockPointerExited;
    public event Action<Native.Point>? PointerPressed;
    public event Action<bool>? TrayClicked;
    public event Action? SettingsRequested;
    public event Action? AreasChanged;
    public ClockArea[] Clocks => _clocks;
    public string Status { get; private set; } = "正在检测任务栏时钟…";
    public bool Enabled { get => _enabled; set => _enabled = value; }

    public TaskbarService(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
        _hookProc = HookCallback;
        _hostProc = HostCallback;
        var wc = new Native.WindowClass { Name = "WinCalendar.MessageHost", Proc = _hostProc, Instance = Native.GetModuleHandle(null) };
        Native.RegisterClass(ref wc);
        _host = Native.CreateWindowEx(0x80, wc.Name, wc.Name, 0, 0, 0, 0, 0, 0, 0, wc.Instance, 0);
        if (_host == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法创建任务栏消息窗口");
        AddTrayIcon();
        var thread = new Thread(HookLoop) { IsBackground = true, Name = "WinCalendar mouse hook" };
        thread.Start();
        _ = DetectLoopAsync();
    }

    private void HookLoop()
    {
        _hookThread = Native.GetCurrentThreadId();
        Native.PeekMessage(out _, 0, 0, 0, 0);
        if (_stop.IsCancellationRequested) return;
        _hook = Native.SetWindowsHookEx(14, _hookProc, Native.GetModuleHandle(null), 0);
        if (_hook == 0)
        {
            Status = "无法安装任务栏鼠标钩子，请通过托盘图标打开日历";
            SettingsStore.Log(new Win32Exception(Marshal.GetLastWin32Error(), Status));
            return;
        }
        while (Native.GetMessage(out var message, 0, 0, 0) > 0)
        {
            Native.TranslateMessage(ref message);
            Native.DispatchMessage(ref message);
        }
        Native.UnhookWindowsHookEx(_hook);
        _hook = 0;
    }

    private nint HookCallback(int code, nint wParam, nint lParam)
    {
        if (code < 0) return Native.CallNextHookEx(0, code, wParam, lParam);
        var message = (uint)wParam;
        // Always pair a swallowed down with its up, including releases outside the clock.
        if (message == 0x202 && _leftCaptured) { _leftCaptured = false; return 1; }
        if (message == 0x205 && _rightCaptured) { _rightCaptured = false; return 1; }
        if (message is not (0x200 or 0x201 or 0x204)) return Native.CallNextHookEx(0, code, wParam, lParam);
        var point = Marshal.PtrToStructure<Native.MouseData>(lParam).Point;
        if (message is 0x201 or 0x204) _dispatcher.TryEnqueue(() => PointerPressed?.Invoke(point));
        if (!_enabled) return Native.CallNextHookEx(0, code, wParam, lParam);
        var clock = ClockAt(point);
        if (message == 0x200)
        {
            if (!Equals(clock, _hoveredClock))
            {
                _hoveredClock = clock;
                if (clock is null) _dispatcher.TryEnqueue(() => ClockPointerExited?.Invoke());
                else _dispatcher.TryEnqueue(() => ClockPointerEntered?.Invoke(clock));
            }
            return Native.CallNextHookEx(0, code, wParam, lParam);
        }
        if (clock is null) return Native.CallNextHookEx(0, code, wParam, lParam);
        var right = message == 0x204;
        if (_dispatcher.TryEnqueue(() => ClockClicked?.Invoke(clock, right)))
        {
            if (right) _rightCaptured = true; else _leftCaptured = true;
            return 1;
        }
        return Native.CallNextHookEx(0, code, wParam, lParam);
    }

    private ClockArea? _hoveredClock;

    private ClockArea? ClockAt(Native.Point point)
    {
        foreach (var clock in _clocks)
        {
            if (!clock.Bounds.Contains(point) || !Native.IsWindowVisible(clock.Taskbar)) continue;
            // Reject covered/auto-hidden taskbars and fullscreen applications, not just matching coordinates.
            if (Native.GetAncestor(Native.WindowFromPoint(point), 2) == clock.Taskbar) return clock;
        }
        return null;
    }

    private async Task DetectLoopAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var clocks = await Task.Run(DetectClocks);
                if (_stop.IsCancellationRequested) break;
                _clocks = clocks;
                if (_hook != 0) Status = clocks.Length == 0 ? "尚未找到任务栏时钟，可通过托盘图标打开日历" : $"已连接 {clocks.Length} 个屏幕的任务栏时钟";
                _dispatcher.TryEnqueue(() => AreasChanged?.Invoke());
                await Task.Delay(2500, _stop.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { SettingsStore.Log(ex); }
    }

    private static ClockArea[] DetectClocks()
    {
        var comResult = Native.CoInitializeEx(0, 0);
        IUIAutomation? automation = null;
        var taskbars = new List<nint>();
        try
        {
            automation = new CUIAutomation8Class();
            Native.EnumWindows((hwnd, _) =>
            {
                if (Native.ClassName(hwnd) is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") taskbars.Add(hwnd);
                return true;
            }, 0);
            var areas = new List<ClockArea>();
            foreach (var hwnd in taskbars)
            {
                IUIAutomationElement? root = null;
                IUIAutomationElement? found = null;
                try
                {
                    if (!Native.IsWindowVisible(hwnd)) continue;
                    root = automation.ElementFromHandle(hwnd);
                    found = FindClockElement(automation, root);
                    if (found is null || found.CurrentIsOffscreen != 0) continue;
                    var rect = found.CurrentBoundingRectangle;
                    if (rect.right - rect.left < 12 || rect.bottom - rect.top < 10) continue;
                    var bounds = new Native.Rect { Left = rect.left, Top = rect.top, Right = rect.right, Bottom = rect.bottom };
                    Native.GetWindowRect(hwnd, out var bar);
                    if (bounds.Left >= bar.Left && bounds.Right <= bar.Right && bounds.Top >= bar.Top && bounds.Bottom <= bar.Bottom)
                        areas.Add(new(hwnd, bounds));
                }
                catch (COMException) { }
                finally
                {
                    ReleaseCom(found);
                    ReleaseCom(root);
                }
            }
            return areas.ToArray();
        }
        finally
        {
            ReleaseCom(automation);
            if (comResult >= 0) Native.CoUninitialize();
        }
    }

    private static IUIAutomationElement? FindClockElement(IUIAutomation automation, IUIAutomationElement root)
    {
        foreach (var candidate in new[]
        {
            (UIA_PropertyIds.UIA_AutomationIdPropertyId, "ClockButton"),
            (UIA_PropertyIds.UIA_AutomationIdPropertyId, "SystemTray.DateTimeIcon"),
            (UIA_PropertyIds.UIA_ClassNamePropertyId, "ClockButton"),
            (UIA_PropertyIds.UIA_ClassNamePropertyId, "TrayClockWClass"),
            (UIA_PropertyIds.UIA_ClassNamePropertyId, "SystemTray.DateTimeIcon"),
        })
        {
            var condition = automation.CreatePropertyCondition(candidate.Item1, candidate.Item2);
            try
            {
                var found = root.FindFirst(TreeScope.TreeScope_Descendants, condition);
                if (found is not null) return found;
            }
            finally { ReleaseCom(condition); }
        }

        var omniCondition = automation.CreatePropertyCondition(UIA_PropertyIds.UIA_ClassNamePropertyId, "SystemTray.OmniButton");
        IUIAutomationElementArray? candidates = null;
        try
        {
            candidates = root.FindAll(TreeScope.TreeScope_Descendants, omniCondition);
            for (var index = 0; index < candidates.Length; index++)
            {
                var candidate = candidates.GetElement(index);
                var id = candidate.CurrentAutomationId ?? string.Empty;
                var name = candidate.CurrentName ?? string.Empty;
                if (id.Contains("DateTime", StringComparison.OrdinalIgnoreCase) || name.Contains("时钟") || name.Contains("时间") || name.Contains("日期") || name.Contains("clock", StringComparison.OrdinalIgnoreCase))
                    return candidate;
                ReleaseCom(candidate);
            }
            return null;
        }
        finally
        {
            ReleaseCom(candidates);
            ReleaseCom(omniCondition);
        }
    }

    private static void ReleaseCom(object? value)
    {
        if (value is not null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value);
    }

    private nint HostCallback(nint hwnd, uint message, nint wParam, nint lParam)
    {
        if (message == _taskbarCreated) { AddTrayIcon(); return 0; }
        if (message == ShowSettingsMessage) { SettingsRequested?.Invoke(); return 0; }
        if (message == 0x8001)
        {
            if ((uint)lParam == 0x202) TrayClicked?.Invoke(false);
            if ((uint)lParam == 0x205) TrayClicked?.Invoke(true);
            return 0;
        }
        return Native.DefWindowProc(hwnd, message, wParam, lParam);
    }

    private void AddTrayIcon()
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "logo.ico");
        var icon = Native.LoadImage(0, iconPath, 1, 0, 0, 0x10);
        _ownsIcon = icon != 0;
        if (!_ownsIcon) icon = Native.LoadIcon(0, 32512);
        _icon = new Native.NotifyIcon { Size = (uint)Marshal.SizeOf<Native.NotifyIcon>(), Hwnd = _host, Id = 1, Flags = 7, Callback = 0x8001, Icon = icon, Tip = "WinCalendar", Info = "", InfoTitle = "" };
        Native.Shell_NotifyIcon(0, ref _icon);
    }

    public void Dispose()
    {
        _enabled = false;
        _stop.Cancel();
        if (_hookThread != 0) Native.PostThreadMessage(_hookThread, 0x12, 0, 0);
        Native.Shell_NotifyIcon(2, ref _icon);
        if (_ownsIcon && _icon.Icon != 0) Native.DestroyIcon(_icon.Icon);
        if (_host != 0) Native.DestroyWindow(_host);
        _host = 0;
    }
}
