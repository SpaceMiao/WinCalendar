using System;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinCalendar.Interop;
using Windows.Graphics;

namespace WinCalendar.Views;

internal sealed class ContextMenuWindow : Window
{
    private readonly Border _root;
    private readonly Button _open;
    private readonly Button _quit;
    private readonly nint _handle;
    private readonly Native.SubclassProc _chromeProc;
    private bool _closing;
    internal ContextMenuWindow(Action settings, Action exit)
    {
        Title = "WinCalendar 菜单";
        _chromeProc = (hwnd, message, wParam, lParam, id, data) =>
        {
            if (message == 0x83) return 0;
            if ((message is 0x6 or 0x1C) && (wParam & 0xffff) == 0)
                DispatcherQueue.TryEnqueue(() => { if (!_closing) AppWindow.Hide(); });
            return Native.DefSubclassProc(hwnd, message, wParam, lParam);
        };
        var stack = new StackPanel { Spacing = 2, Margin = new Thickness(4) };
        _root = new Border { Child = stack, CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(0) };
        _root.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("ms-appx:///Views/CalendarStyles.xaml") });
        var menuButtonStyle = (Style)_root.Resources["CalendarButtonStyle"];
        _open = MenuButton("设置", "\uE713", menuButtonStyle);
        _quit = MenuButton("退出", "\uE711", menuButtonStyle);
        _open.Click += (_, _) => { AppWindow.Hide(); settings(); };
        _quit.Click += (_, _) => { AppWindow.Hide(); exit(); };
        stack.Children.Add(_open); stack.Children.Add(_quit);
        Content = _root;
        var presenter = OverlappedPresenter.Create();
        presenter.SetBorderAndTitleBar(false, false); presenter.IsResizable = false; presenter.IsAlwaysOnTop = true;
        AppWindow.SetPresenter(presenter); AppWindow.IsShownInSwitchers = false;
        _handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        Native.SetWindowSubclass(_handle, _chromeProc, 0x5744, 0);
        var corner = 2; Native.DwmSetWindowAttribute(_handle, 33, ref corner, sizeof(int));
        var noBorder = unchecked((int)0xFFFFFFFE); Native.DwmSetWindowAttribute(_handle, 34, ref noBorder, sizeof(int));
        Closed += (_, _) => Native.RemoveWindowSubclass(_handle, _chromeProc, 0x5744);
        Activated += (_, e) => { if (e.WindowActivationState == WindowActivationState.Deactivated) AppWindow.Hide(); };
        AppWindow.Closing += (_, e) => { if (!_closing) { e.Cancel = true; AppWindow.Hide(); } };
        _root.KeyDown += (_, e) => { if (e.Key == Windows.System.VirtualKey.Escape) AppWindow.Hide(); };
    }

    internal void ShowAt(Native.Point point, string theme)
    {
        _root.RequestedTheme = theme switch { "dark" => ElementTheme.Dark, "light" => ElementTheme.Light, _ => ElementTheme.Default };
        var dark = _root.ActualTheme == ElementTheme.Dark;
        _root.Background = CalendarView.Brush(dark ? "#282828" : "#FAFAFA");
        var info = Native.MonitorAt(point);
        Native.GetDpiForMonitor(Native.MonitorFromPoint(point, 2), 0, out var dpi, out _);
        var scale = (dpi == 0 ? 96 : dpi) / 96.0;
        var width = (int)(180 * scale); var height = (int)(86 * scale);
        AppWindow.MoveAndResize(new RectInt32(Math.Clamp(point.X + 8, info.Work.Left, info.Work.Right - width), Math.Clamp(point.Y - height - 8, info.Work.Top, info.Work.Bottom - height), width, height));
        Activate(); Native.SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
    }

    private static Button MenuButton(string text, string iconGlyph, Style style)
    {
        var content = new Grid { ColumnSpacing = 10 };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        content.ColumnDefinitions.Add(new ColumnDefinition());
        content.Children.Add(new FontIcon { Glyph = iconGlyph, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe MDL2 Assets"), FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        CalendarView.Add(content, new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center }, 1);
        return new Button
        {
            Content = content,
            Style = style,
            Height = 38,
            Padding = new Thickness(12, 0, 12, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            CornerRadius = new CornerRadius(4),
            IsTabStop = false,
            AllowFocusOnInteraction = false,
            UseSystemFocusVisuals = false,
        };
    }

    internal void Shutdown() { _closing = true; Close(); }
}
