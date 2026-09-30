using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinCalendar.Interop;
using WinCalendar.Models;
using WinCalendar.Services;
using Windows.Graphics;

namespace WinCalendar.Views;

internal sealed class SettingsWindow : Window
{
    private readonly SettingsStore _store;
    private readonly HolidayService _holidays;
    private readonly Func<string> _taskbarStatus;
    private readonly Action _exit;
    private readonly Grid _root = new();
    private readonly Border _navigationSurface = new();
    private readonly Border _contentSurface = new();
    private readonly StackPanel _navigationItems = new() { Spacing = 4, Margin = new Thickness(8, 12, 8, 12) };
    private readonly Dictionary<string, Button> _navigationButtons = [];
    private readonly StackPanel _page = new() { Spacing = 18, Margin = new Thickness(28, 16, 28, 24), MaxWidth = 700, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly InfoBar _notice = new() { IsClosable = true };
    private TextBlock? _sourceStatus;
    private TextBlock? _clockPreview;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private TextBox? _timeFormat, _dateFormat;
    private readonly nint _windowIcon;
    private string _activePage = "general";
    private AppSettings Settings => _store.Value;

    internal SettingsWindow(SettingsStore store, HolidayService holidays, Func<string> taskbarStatus, Action exit)
    {
        _store = store; _holidays = holidays; _taskbarStatus = taskbarStatus; _exit = exit;
        Title = "WinCalendar 设置";
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "logo.ico");
        _windowIcon = Native.LoadImage(0, iconPath, 1, 0, 0, 0x10);
        if (_windowIcon != 0)
        {
            var handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
            Native.SendMessage(handle, 0x80, 0, _windowIcon);
            Native.SendMessage(handle, 0x80, 1, _windowIcon);
        }
        var initialTheme = ResolveTheme();
        _root.RequestedTheme = initialTheme;
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(176) });
        _root.ColumnDefinitions.Add(new ColumnDefinition());
        var navigationLayout = new Grid();
        navigationLayout.RowDefinitions.Add(new RowDefinition());
        navigationLayout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        navigationLayout.Children.Add(_navigationItems);
        var exitButton = NavigationButton("退出", "\uE711", CalendarView.Brush("#EC7C7C"));
        exitButton.Margin = new Thickness(8, 0, 8, 12);
        exitButton.Click += (_, _) => _exit();
        CalendarView.Add(navigationLayout, exitButton, 0, 1);
        _navigationSurface.Child = navigationLayout;
        _contentSurface.Child = new ScrollViewer { Content = _page, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        CalendarView.Add(_root, _navigationSurface, 0);
        CalendarView.Add(_root, _contentSurface, 1);
        AddNavigationItem("general", "通用设置", Symbol.Setting);
        AddNavigationItem("calendar", "日历内容", Symbol.Calendar);
        AddNavigationItem("clock", "任务栏时钟", Symbol.Clock);
        Content = _root;
        AppWindow.Resize(new SizeInt32(820, 680));
        if (Settings.SettingsX is int x && Settings.SettingsY is int y)
            AppWindow.Move(new PointInt32(x, y));
        if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
        }
        ShowPage(_activePage);
        ApplyTheme();
        _timer.Tick += (_, _) => UpdateStatus();
        _timer.Start();
        Activated += (_, _) => HideMaximizeButton();
        AppWindow.Closing += (_, _) =>
        {
            _timer.Stop();
            var position = AppWindow.Position;
            Settings.SettingsX = position.X;
            Settings.SettingsY = position.Y;
            try { _store.Save(); } catch (Exception ex) { SettingsStore.Log(ex); }
        };
        Closed += (_, _) => { if (_windowIcon != 0) Native.DestroyIcon(_windowIcon); };
    }

    internal void ShowSettings() { ApplyTheme(); UpdateStatus(); _timer.Start(); Activate(); }
    internal void Shutdown() { _timer.Stop(); Close(); }

    private void HideMaximizeButton()
    {
        var handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var style = Native.GetWindowLong(handle, -16);
        Native.SetWindowLong(handle, -16, style & ~(nint)0x00010000);
        Native.SetWindowPos(handle, 0, 0, 0, 0, 0, 0x0027);
    }

    private ElementTheme ResolveTheme() => Settings.Theme switch { "dark" => ElementTheme.Dark, "light" => ElementTheme.Light, _ => ElementTheme.Default };

    internal void ApplyTheme()
    {
        var theme = ResolveTheme();
        _root.RequestedTheme = theme;
        var dark = theme == ElementTheme.Dark || (theme == ElementTheme.Default && _root.ActualTheme == ElementTheme.Dark);
        var surface = CalendarView.Brush(dark ? "#202020" : "#F7F7F7");
        _root.Background = surface;
        _contentSurface.Background = surface;
        _navigationSurface.Background = CalendarView.Brush(dark ? "#292929" : "#FFFFFF");
        foreach (var pair in _navigationButtons)
        {
            var active = pair.Key == _activePage;
            pair.Value.Background = active
                ? CalendarView.Brush(dark ? "#3A3A3A" : "#E8E8E8")
                : new SolidColorBrush(Colors.Transparent);
            pair.Value.Opacity = active ? 1 : 0.78;
        }

        var titleBar = AppWindow.TitleBar;
        var title = dark ? Windows.UI.Color.FromArgb(255, 32, 32, 32) : Windows.UI.Color.FromArgb(255, 247, 247, 247);
        var foreground = dark ? Windows.UI.Color.FromArgb(255, 255, 255, 255) : Windows.UI.Color.FromArgb(255, 28, 28, 28);
        titleBar.BackgroundColor = title;
        titleBar.ForegroundColor = foreground;
        titleBar.InactiveBackgroundColor = title;
        titleBar.InactiveForegroundColor = foreground;
        titleBar.ButtonBackgroundColor = title;
        titleBar.ButtonForegroundColor = foreground;
        titleBar.ButtonInactiveBackgroundColor = title;
        titleBar.ButtonInactiveForegroundColor = foreground;
    }

    private void AddNavigationItem(string name, string label, Symbol symbol)
    {
        var content = new Grid { ColumnSpacing = 10 };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        content.ColumnDefinitions.Add(new ColumnDefinition());
        content.Children.Add(new SymbolIcon(symbol)
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        });
        CalendarView.Add(content, new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center }, 1);
        var button = new Button
        {
            Content = content,
            Height = 38,
            Padding = new Thickness(10, 0, 10, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(4),
            IsTabStop = false,
            AllowFocusOnInteraction = false,
            UseSystemFocusVisuals = false,
        };
        button.Click += (_, _) => ShowPage(name);
        _navigationButtons.Add(name, button);
        _navigationItems.Children.Add(button);
    }

    private static Button NavigationButton(string label, string iconGlyph, Microsoft.UI.Xaml.Media.Brush? foreground = null)
    {
        var content = new Grid { ColumnSpacing = 10 };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        content.ColumnDefinitions.Add(new ColumnDefinition());
        content.Children.Add(new FontIcon { Glyph = iconGlyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        CalendarView.Add(content, new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center }, 1);
        return new Button
        {
            Content = content,
            Height = 38,
            Padding = new Thickness(10, 0, 10, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(4),
            Foreground = foreground,
            IsTabStop = false,
            AllowFocusOnInteraction = false,
            UseSystemFocusVisuals = false,
        };
    }

    private void ShowPage(string name)
    {
        _activePage = name;
        _sourceStatus = null;
        _clockPreview = null;
        _page.Children.Clear();
        _page.Children.Add(new TextBlock { Text = name switch { "calendar" => "日历内容", "clock" => "任务栏时钟", _ => "通用设置" }, FontSize = 26, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        _page.Children.Add(_notice);
        if (name == "general") GeneralPage();
        else if (name == "calendar") CalendarPage();
        else ClockPage();
        ApplyTheme();
        UpdateStatus();
    }

    private void GeneralPage()
    {
        var theme = new ComboBox { Width = 150, ItemsSource = new[] { "深色模式", "浅色模式", "跟随系统" }, SelectedIndex = Settings.Theme switch { "dark" => 0, "light" => 1, _ => 2 } };
        theme.SelectionChanged += (_, _) => Save(() => Settings.Theme = new[] { "dark", "light", "system" }[theme.SelectedIndex]);
        _page.Children.Add(Row("主题模式", theme));
        var autostart = CreateToggle(Settings.Autostart);
        var updatingStartup = false;
        autostart.Toggled += async (_, _) =>
        {
            if (updatingStartup) return;
            updatingStartup = true; autostart.IsEnabled = false;
            try { Settings.Autostart = await StartupService.SetEnabledAsync(autostart.IsOn); _store.Save(); }
            catch (Exception ex) { ShowError(ex.Message); }
            finally { autostart.IsOn = Settings.Autostart; autostart.IsEnabled = true; updatingStartup = false; }
        };
        _page.Children.Add(Row("开机自启动", autostart));
        AddToggle("桌面组件", Settings.DesktopWidget, v => Settings.DesktopWidget = v);
        AddToggle("替换任务栏日历", Settings.ReplaceTaskbar, v => Settings.ReplaceTaskbar = v);
        _page.Children.Add(Hint("关闭设置后继续在后台运行。可通过任务栏时钟或托盘图标的右键菜单退出。"));
    }

    private void CalendarPage()
    {
        var transparency = new Slider { Minimum = 0, Maximum = 100, StepFrequency = 5, Value = Settings.Transparency, Width = 200 };
        transparency.ValueChanged += (_, e) => Save(() => Settings.Transparency = (int)e.NewValue);
        _page.Children.Add(Row("背景透明度", transparency));
        var footer = AddToggle("显示底部信息区域", Settings.ShowFooter, v => Settings.ShowFooter = v);
        var festivals = AddToggle("显示节假日", Settings.ShowFestivals, v => Settings.ShowFestivals = v, 20);
        var yiJi = AddToggle("显示宜忌", Settings.ShowYiJi, v => Settings.ShowYiJi = v, 20);
        var countdown = AddToggle("显示节日倒计时", Settings.ShowCountdown, v => Settings.ShowCountdown = v, 20);
        void SetFooterOptionsEnabled()
        {
            festivals.IsEnabled = footer.IsOn;
            yiJi.IsEnabled = footer.IsOn;
            countdown.IsEnabled = footer.IsOn;
        }
        footer.Toggled += (_, _) => SetFooterOptionsEnabled();
        SetFooterOptionsEnabled();
        var url = new TextBox
        {
            Header = HeaderWithInfo("ICS数据源", "解析事件标题“休｜节日名称”和“班｜补班名称”标记, 启动时及每隔6小时更新"),
            Text = Settings.IcsUrl,
            PlaceholderText = AppSettings.DefaultIcsUrl,
        };
        _page.Children.Add(url);
        var actions = new Grid { ColumnSpacing = 16 };
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var sourceStatus = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.75 };
        _sourceStatus = sourceStatus;
        var save = new Button { Content = "保存" };
        var reset = new Button { Content = "恢复默认" };
        async Task SaveAndRefresh()
        {
            var address = url.Text.Trim();
            if (!Uri.TryCreate(address, UriKind.Absolute, out var parsed) || parsed.Scheme is not ("https" or "http"))
            { ShowError("请输入有效的 HTTP 或 HTTPS 数据源地址。"); return; }
            save.IsEnabled = reset.IsEnabled = false;
            try
            {
                Settings.IcsUrl = address;
                _store.Save();
                await _holidays.RefreshAsync(address);
                UpdateStatus();
            }
            catch (Exception ex) { ShowError(ex.Message); }
            finally { save.IsEnabled = reset.IsEnabled = true; }
        }
        save.Click += async (_, _) => await SaveAndRefresh();
        reset.Click += (_, _) => url.Text = AppSettings.DefaultIcsUrl;
        buttons.Children.Add(save); buttons.Children.Add(reset);
        sourceStatus.HorizontalAlignment = HorizontalAlignment.Right;
        sourceStatus.VerticalAlignment = VerticalAlignment.Center;
        CalendarView.Add(actions, buttons, 0);
        CalendarView.Add(actions, sourceStatus, 1);
        _page.Children.Add(actions);
    }

    private void ClockPage()
    {
        _timeFormat = new TextBox { Header = HeaderWithInfo("时间格式", "HH：24小时 mm：分钟 ss：秒"), Text = Settings.TimeFormat, PlaceholderText = "HH:mm:ss" };
        _dateFormat = new TextBox { Header = HeaderWithInfo("日期格式", "yyyy：年 MM：月 dd：日 ddd：周几 dddd：星期几"), Text = Settings.DateFormat, PlaceholderText = "M月d日 ddd" };
        var apply = new Button { Content = "应用格式" };
        apply.Click += (_, _) =>
        {
            if (!ClockOverlayService.ValidFormat(_timeFormat.Text) || !ClockOverlayService.ValidFormat(_dateFormat.Text))
            { ShowError("格式无效或过长，请检查后重试。"); return; }
            Save(() => { Settings.TimeFormat = _timeFormat.Text; Settings.DateFormat = _dateFormat.Text; });
        };
        var customClock = CreateToggle(Settings.CustomClock);
        void SetEnabled(bool enabled)
        {
            _timeFormat.IsEnabled = _dateFormat.IsEnabled = apply.IsEnabled = enabled;
        }
        customClock.Toggled += (_, _) =>
        {
            Save(() => Settings.CustomClock = customClock.IsOn);
            SetEnabled(customClock.IsOn);
        };
        _page.Children.Add(RowWithInfo("自定义任务栏时钟", "启用后修改 Windows 日期展示格式, 关闭或退出程序后恢复", customClock));
        _page.Children.Add(_timeFormat); _page.Children.Add(_dateFormat);
        var preview = new Grid { RowSpacing = 4 };
        var clockPreview = new TextBlock
        {
            FontSize = 20,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Right,
            TextAlignment = TextAlignment.Right,
        };
        _clockPreview = clockPreview;
        preview.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        preview.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        preview.Children.Add(new TextBlock { Text = "效果预览", FontSize = 12, Opacity = 0.7 });
        CalendarView.Add(preview, clockPreview, 0, 1);
        _page.Children.Add(preview);
        _page.Children.Add(apply);
        SetEnabled(customClock.IsOn);
    }

    private void UpdateStatus()
    {
        if (_sourceStatus is not null) _sourceStatus.Text = _holidays.Status;
        if (_timeFormat is null || _dateFormat is null || _clockPreview is null) return;
        try { _clockPreview.Text = DateTime.Now.ToString(_timeFormat.Text, CultureInfo.GetCultureInfo("zh-CN")) + "\n" + DateTime.Now.ToString(_dateFormat.Text, CultureInfo.GetCultureInfo("zh-CN")); }
        catch (FormatException) { _clockPreview.Text = "格式无效"; }
    }

    private void Save(Action change)
    {
        try { change(); _store.Save(); ApplyTheme(); }
        catch (Exception ex) { SettingsStore.Log(ex); ShowError("设置未能保存：" + ex.Message); }
    }
    private void ShowError(string message) { _notice.Message = message; _notice.Severity = InfoBarSeverity.Error; _notice.IsOpen = true; }
    private ToggleSwitch AddToggle(string label, bool initial, Action<bool> changed, double indent = 0)
    {
        var toggle = CreateToggle(initial);
        toggle.Toggled += (_, _) => Save(() => changed(toggle.IsOn));
        var row = Row(label, toggle);
        row.Margin = new Thickness(indent, 0, 0, 0);
        _page.Children.Add(row);
        return toggle;
    }

    private static ToggleSwitch CreateToggle(bool initial) => new()
    {
        IsOn = initial,
        OnContent = "开",
        OffContent = "关",
        MinWidth = 0,
        Width = 72,
        HorizontalAlignment = HorizontalAlignment.Right,
    };

    private static StackPanel HeaderWithInfo(string label, string tooltip)
    {
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        header.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        var icon = CreateSymbolIcon(Symbol.Comment);
        ToolTipService.SetToolTip(icon, tooltip);
        ToolTipService.SetPlacement(icon, Microsoft.UI.Xaml.Controls.Primitives.PlacementMode.Right);
        header.Children.Add(icon);
        return header;
    }

    private static SymbolIcon CreateSymbolIcon(Symbol symbol) => new(symbol)
    {
        Width = 20,
        Height = 20,
        Margin = new Thickness(0, 4, 0, 0),
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        Scale = new System.Numerics.Vector3(0.60f, 0.60f, 1f),
    };

    private static Grid Row(string label, FrameworkElement control)
        => Row(label, new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center }, control);

    private static Grid RowWithInfo(string label, string tooltip, FrameworkElement control)
        => Row(label, HeaderWithInfo(label, tooltip), control);

    private static Grid Row(string label, FrameworkElement header, FrameworkElement control)
    {
        AutomationProperties.SetName(control, label);
        var row = new Grid { ColumnSpacing = 16 };
        row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        row.Children.Add(header);
        CalendarView.Add(row, control, 1);
        return row;
    }

    private static TextBlock Hint(string text) => new() { Text = text, FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap };
}
