using System;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using WinCalendar.Interop;
using WinCalendar.Views;

namespace WinCalendar.Services;

internal sealed class AppController : IDisposable
{
    private readonly SettingsStore _settings = new();
    private readonly HolidayService _holidays = new();
    private readonly DispatcherQueue _dispatcher;
    private readonly MainWindow _popup;
    private MainWindow? _desktop;
    private SettingsWindow? _settingsWindow;
    private readonly ContextMenuWindow _menu;
    private readonly TaskbarService _taskbar;
    private readonly ClockOverlayService _clock;
    private readonly DispatcherTimer _seconds = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromHours(6) };
    private readonly DispatcherTimer _hoverDismiss = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private bool _disposed;
    private bool _hoverPopup;

    internal AppController()
    {
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _holidays.LoadCache(_settings.Value.IcsUrl);
        _popup = new(_settings, _holidays);
        _menu = new(ShowSettings, Exit);
        _taskbar = new(_dispatcher);
        _clock = new(_settings);
        _taskbar.ClockClicked += (area, right) =>
        {
            var point = new Native.Point(area.Bounds.Right - 1, area.Bounds.Top + 1);
            if (right) { _hoverPopup = false; _hoverDismiss.Stop(); _popup.Hide(); _menu.ShowAt(point, _settings.Value.Theme); }
            else OpenFromClockClick(point, area.Bounds);
        };
        _taskbar.ClockPointerEntered += ShowHoverPopup;
        _taskbar.ClockPointerExited += ScheduleHoverDismiss;
        _taskbar.PointerPressed += HideClickedPopupWhenPointerLeaves;
        _popup.PointerEntered += () => _hoverDismiss.Stop();
        _popup.PointerExited += ScheduleHoverDismiss;
        _taskbar.TrayClicked += right =>
        {
            Native.GetCursorPos(out var point);
            if (right) { _popup.Hide(); _menu.ShowAt(point, _settings.Value.Theme); }
            else TogglePopup(point);
        };
        _taskbar.SettingsRequested += ShowSettings;
        _taskbar.AreasChanged += () => _clock.Update(_taskbar.Clocks, _settings.Value);
        _settings.Changed += ApplySettings;
        _holidays.Changed += OnHolidayChanged;
        _seconds.Tick += (_, _) => _clock.Tick();
        _refresh.Tick += async (_, _) => await _holidays.RefreshAsync(_settings.Value.IcsUrl);
        ApplySettings();
        _hoverDismiss.Tick += (_, _) => DismissHoverPopup();
        _seconds.Start(); _refresh.Start();
        _ = _holidays.RefreshAsync(_settings.Value.IcsUrl);
    }

    internal void ShowSettings()
    {
        _popup.Hide();
        if (_settingsWindow is null)
        {
            _settingsWindow = new(_settings, _holidays, () => _taskbar.Status, Exit);
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        }
        _settingsWindow.ShowSettings();
    }

    private void Exit()
    {
        Dispose();
        Application.Current.Exit();
    }

    private void TogglePopup(Native.Point point, Native.Rect? rect = null)
    {
        _hoverPopup = false;
        _hoverDismiss.Stop();
        _menu.AppWindow.Hide();
        if (_popup.IsVisible && _popup.Monitor == Native.MonitorFromPoint(point, 2)) _popup.Hide();
        else _popup.ShowAt(point, rect);
    }

    private void OpenFromClockClick(Native.Point point, Native.Rect bounds)
    {
        _hoverPopup = false;
        _hoverDismiss.Stop();
        _menu.AppWindow.Hide();
        _popup.ShowAt(point, bounds);
    }

    private void ShowHoverPopup(ClockArea area)
    {
        _hoverDismiss.Stop();
        if (_popup.IsVisible && !_hoverPopup) return;
        _hoverPopup = true;
        _menu.AppWindow.Hide();
        _popup.ShowAt(new Native.Point(area.Bounds.Right - 1, area.Bounds.Top + 1), area.Bounds, activate: false);
    }

    private void ScheduleHoverDismiss()
    {
        if (_hoverPopup) _hoverDismiss.Start();
    }

    private void DismissHoverPopup()
    {
        _hoverDismiss.Stop();
        if (!_hoverPopup || _popup.IsPointerOver()) return;
        _hoverPopup = false;
        _popup.Hide();
    }

    private void HideClickedPopupWhenPointerLeaves(Native.Point point)
    {
        if (_hoverPopup || !_popup.IsVisible || _popup.ContainsPoint(point)) return;
        _popup.Hide();
    }

    private void ApplySettings()
    {
        _holidays.LoadCache(_settings.Value.IcsUrl);
        _taskbar.Enabled = _settings.Value.ReplaceTaskbar;
        if (!_taskbar.Enabled) _popup.Hide();
        _popup.ApplySettings();
        _settingsWindow?.ApplyTheme();
        if (_settings.Value.DesktopWidget)
        {
            if (_desktop is null)
            {
                _desktop = new(_settings, _holidays, true);
                _desktop.Closed += (_, _) => _desktop = null;
            }
            _desktop.ApplySettings();
            _desktop.ShowDesktop();
        }
        else _desktop?.Hide();
        _clock.Update(_taskbar.Clocks, _settings.Value);
    }

    private void OnHolidayChanged() => _dispatcher.TryEnqueue(() =>
    {
        if (_disposed) return;
        _popup.RefreshCalendar();
        _desktop?.RefreshCalendar();
    });

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _settings.Changed -= ApplySettings;
        _holidays.Changed -= OnHolidayChanged;
        _seconds.Stop(); _refresh.Stop(); _hoverDismiss.Stop();
        _taskbar.Dispose(); _clock.Dispose(); _holidays.Dispose();
        _settingsWindow?.Shutdown(); _desktop?.Shutdown(); _menu.Shutdown(); _popup.Shutdown();
    }
}
