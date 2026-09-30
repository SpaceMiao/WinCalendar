using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using WinCalendar.Interop;
using WinCalendar.Services;
using WinCalendar.Views;
using WinRT;

namespace WinCalendar;

public sealed partial class MainWindow : Window
{
    private static nint _dispatcherQueueController;
    // WinUI restores WS_DLGFRAME when activating its presenter. Suppress the
    // non-client inset directly, without fighting the presenter's window styles.
    private static readonly Native.SubclassProc ChromeProc = (hwnd, message, wParam, lParam, id, data) =>
    {
        if (message == 0x83) return 0;
        if (message == 0x14)
        {
            Native.GetClientRect(hwnd, out var rect);
            var brush = Native.CreateSolidBrush(0x00FF00FF);
            Native.FillRect((nint)wParam, ref rect, brush);
            Native.DeleteObject(brush);
            return 1;
        }
        return Native.DefSubclassProc(hwnd, message, wParam, lParam);
    };
    private readonly SettingsStore _settings;
    private readonly bool _desktop;
    private readonly CalendarView _calendar;
    private readonly ICompositionSupportsSystemBackdrop _backdropTarget;
    private readonly Windows.UI.Composition.Compositor _compositor;
    private Native.Point _anchor;
    private Native.Rect _clockBounds;
    private Native.Point _desktopDragOrigin;
    private bool _closing;
    private bool _resizeQueued;
    internal nint Handle { get; }
    internal bool IsVisible => AppWindow.IsVisible;
    internal nint Monitor { get; private set; }
    internal event Action? PointerEntered;
    internal event Action? PointerExited;

    internal MainWindow(SettingsStore settings, HolidayService holidays, bool desktop = false)
    {
        InitializeComponent();
        _settings = settings; _desktop = desktop;
        Handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "logo.ico"));
        _calendar = new(settings, holidays, desktop);
        CalendarHost.Child = _calendar;
        _calendar.ContentSizeChanged += QueueContentResize;
        _calendar.DismissRequested += () => { if (!_desktop) Hide(); };
        _calendar.DragStarted += () =>
        {
            _desktopDragOrigin = new Native.Point(AppWindow.Position.X, AppWindow.Position.Y);
        };
        _calendar.DragMoved += delta =>
        {
            var scale = Native.GetDpiForWindow(Handle) / 96.0;
            Native.SetWindowPos(Handle, 0,
                _desktopDragOrigin.X + (int)Math.Round(delta.X * scale),
                _desktopDragOrigin.Y + (int)Math.Round(delta.Y * scale),
                0, 0, 0x1 | 0x4 | 0x10);
        };
        _calendar.DragCompleted += () =>
        {
            Native.GetWindowRect(Handle, out var bounds);
            settings.Value.DesktopX = bounds.Left;
            settings.Value.DesktopY = bounds.Top;
            try { settings.Save(); } catch (Exception ex) { SettingsStore.Log(ex); }
        };
        var presenter = OverlappedPresenter.Create();
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsResizable = false; presenter.IsMaximizable = false; presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = !desktop;
        AppWindow.SetPresenter(presenter);
        AppWindow.IsShownInSwitchers = false;
        if (!Native.SetWindowSubclass(Handle, ChromeProc, 0x5743, 0))
            throw new InvalidOperationException("无法设置日历窗口边框");
        EnsureCompositionDispatcherQueue();
        _compositor = new Windows.UI.Composition.Compositor();
        _backdropTarget = this.As<ICompositionSupportsSystemBackdrop>();
        _backdropTarget.SystemBackdrop = _compositor.CreateColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
        Native.SetWindowLong(Handle, -20, Native.GetWindowLong(Handle, -20) | 0x80000);
        Native.SetLayeredWindowAttributes(Handle, 0x00FF00FF, byte.MaxValue, 0x1);
        var margins = new Native.Margins { Left = -1 };
        Native.DwmExtendFrameIntoClientArea(Handle, ref margins);
        Native.SetWindowPos(Handle, 0, 0, 0, 0, 0, 0x1 | 0x2 | 0x4 | 0x10 | 0x20);
        Closed += (_, _) => Native.RemoveWindowSubclass(Handle, ChromeProc, 0x5743);
        var corner = 2; Native.DwmSetWindowAttribute(Handle, 33, ref corner, sizeof(int));
        var noBorder = unchecked((int)0xFFFFFFFE); Native.DwmSetWindowAttribute(Handle, 34, ref noBorder, sizeof(int));
        Activated += (_, args) =>
        {
            if (!_desktop && args.WindowActivationState == WindowActivationState.Deactivated) Hide();
            if (_desktop && args.WindowActivationState == WindowActivationState.Deactivated)
                Native.SetWindowPos(Handle, 1, 0, 0, 0, 0, 0x1 | 0x2 | 0x10 | 0x200);
        };
        AppWindow.Closing += (_, args) => { if (!_closing) { args.Cancel = true; Hide(); } };
        Surface.ActualThemeChanged += (_, _) => ApplySurface();
        Surface.PointerEntered += (_, _) => PointerEntered?.Invoke();
        Surface.PointerExited += (_, _) => PointerExited?.Invoke();
        ApplySettings();
    }

    private static void EnsureCompositionDispatcherQueue()
    {
        if (Windows.System.DispatcherQueue.GetForCurrentThread() is not null) return;

        var options = new Native.DispatcherQueueOptions
        {
            Size = Marshal.SizeOf<Native.DispatcherQueueOptions>(),
            ThreadType = 2,
            ApartmentType = 2,
        };
        Marshal.ThrowExceptionForHR(Native.CreateDispatcherQueueController(options, out _dispatcherQueueController));
    }

    internal void ApplySettings()
    {
        var theme = _settings.Value.Theme switch { "dark" => ElementTheme.Dark, "light" => ElementTheme.Light, _ => ElementTheme.Default };
        Surface.RequestedTheme = theme;
        _calendar.ApplyTheme(theme);
        ApplySurface();
        _calendar.Refresh();
        if (IsVisible && !_desktop) PositionPopup();
    }

    private void ApplySurface()
    {
        var dark = _settings.Value.Theme switch
        {
            "dark" => true,
            "light" => false,
            _ => Surface.ActualTheme == ElementTheme.Dark,
        };
        var shade = dark ? (byte)32 : (byte)255;
        var tint = Windows.UI.Color.FromArgb(255, shade, shade, shade);
        var transparency = Math.Clamp(_settings.Value.Transparency, 0, 100);
        BackgroundSurface.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(
            (byte)Math.Round((100 - transparency) * 2.55), tint.R, tint.G, tint.B));
    }

    internal void RefreshCalendar() => _calendar.Refresh();
    internal void ShowAt(Native.Point point, Native.Rect? clock = null, bool activate = true)
    {
        _anchor = point;
        _clockBounds = clock ?? new Native.Rect { Left = point.X, Right = point.X, Top = point.Y, Bottom = point.Y };
        Monitor = Native.MonitorFromPoint(point, 2);
        _calendar.GoToToday();
        PositionPopup();
        if (activate)
        {
            Activate();
            PositionPopup();
            Native.SetForegroundWindow(Handle);
            _calendar.Focus(FocusState.Programmatic);
        }
        else
        {
            AppWindow.Show(false);
            PositionPopup();
        }
    }

    internal bool IsPointerOver()
    {
        return Native.GetCursorPos(out var point) && ContainsPoint(point);
    }

    internal bool ContainsPoint(Native.Point point) => Native.GetWindowRect(Handle, out var bounds) && bounds.Contains(point);

    private void PositionPopup()
    {
        var info = Native.MonitorAt(_anchor);
        Native.GetDpiForMonitor(Native.MonitorFromPoint(_anchor, 2), 0, out var dpi, out _);
        var scale = (dpi == 0 ? 96 : dpi) / 96.0;
        var gap = (int)(8 * scale);
        ResizeToContent(info.Work, scale, gap);
        var width = AppWindow.Size.Width;
        var height = AppWindow.Size.Height;
        var x = Math.Clamp(_clockBounds.Right - width, info.Work.Left + gap, Math.Max(info.Work.Left + gap, info.Work.Right - width - gap));
        var y = _clockBounds.Top - height - gap;
        if (y < info.Work.Top) y = _clockBounds.Bottom + gap;
        y = Math.Clamp(y, info.Work.Top + gap, Math.Max(info.Work.Top + gap, info.Work.Bottom - height - gap));
        Native.SetWindowPos(Handle, 0, x, y, 0, 0, 0x1 | 0x4 | 0x10);
    }

    private void ResizeToContent(Native.Rect workArea, double scale, int gap)
    {
        var content = _calendar.MeasureContent();
        var fit = Math.Min(1, Math.Min((workArea.Width - 2 * gap) / (content.Width * scale), (workArea.Height - 2 * gap) / (content.Height * scale)));
        // The non-client handler keeps the measured content and window bounds identical.
        Native.SetWindowPos(Handle, 0, 0, 0,
            (int)Math.Ceiling(content.Width * scale * fit), (int)Math.Ceiling(content.Height * scale * fit),
            0x2 | 0x4 | 0x10 | 0x20);
    }

    private void QueueContentResize()
    {
        if (_resizeQueued || _closing || !IsVisible) return;
        _resizeQueued = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            _resizeQueued = false;
            if (_closing || !IsVisible) return;
            if (_desktop) ShowDesktop(); else PositionPopup();
        });
    }

    internal void ShowDesktop()
    {
        var point = new Native.Point(_settings.Value.DesktopX ?? 36, _settings.Value.DesktopY ?? 80);
        var monitor = Native.MonitorAt(point);
        Native.GetDpiForMonitor(Native.MonitorFromPoint(point, 2), 0, out var dpi, out _);
        var scale = (dpi == 0 ? 96 : dpi) / 96.0;
        ResizeToContent(monitor.Work, scale, 8);
        var width = AppWindow.Size.Width;
        var height = AppWindow.Size.Height;
        var x = Math.Clamp(point.X, monitor.Work.Left, Math.Max(monitor.Work.Left, monitor.Work.Right - width));
        var y = Math.Clamp(point.Y, monitor.Work.Top, Math.Max(monitor.Work.Top, monitor.Work.Bottom - height));
        var progman = Native.FindWindow("Progman", null);
        if (progman != 0) Native.SetWindowLong(Handle, -8, progman);
        AppWindow.Show(false);
        ResizeToContent(monitor.Work, scale, 8);
        Native.SetWindowPos(Handle, 1, x, y, 0, 0, 0x1 | 0x10);
    }

    internal void Hide() => AppWindow.Hide();
    internal void Shutdown() { _closing = true; Close(); }
}
