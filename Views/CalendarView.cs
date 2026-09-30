using System;
using System.Linq;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using WinCalendar.Services;
using Windows.System;

namespace WinCalendar.Views;

public sealed class CalendarView : UserControl
{
    private readonly SettingsStore _settings;
    private readonly HolidayService _holidays;
    public const double PreferredWidth = 360;
    private readonly StackPanel _layout = new() { Margin = new Thickness(8) };
    private readonly Button _title = new() { FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Padding = new Thickness(4, 0, 4, 0) };
    private readonly Grid _grid = new() { ColumnSpacing = 1, RowSpacing = 1 };
    private readonly StackPanel _footer = new() { Spacing = 12 };
    private readonly Border _footerSurface = new() { Margin = new Thickness(0, 16, 0, 0), Padding = new Thickness(12), CornerRadius = new CornerRadius(8) };
    private readonly Style _buttonStyle;
    private DateTime _month = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    private DateTime _selected = DateTime.Today;
    private int _level;
    private long _lastWheel;
    private DateTime _today = DateTime.Today;
    private ElementTheme _theme = ElementTheme.Default;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(30) };
    public event Action? DismissRequested;
    public event Action? DragStarted;
    public event Action<Windows.Foundation.Point>? DragMoved;
    public event Action? DragCompleted;
    public event Action? ContentSizeChanged;
    private uint _dragPointerId;
    private Windows.Foundation.Point _dragStart;
    private bool _dragging;

    public CalendarView(SettingsStore settings, HolidayService holidays, bool desktop = false)
    {
        _settings = settings;
        _holidays = holidays;
        Width = PreferredWidth;
        FontFamily = new FontFamily("Segoe UI");
        IsTabStop = true;
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("ms-appx:///Views/CalendarStyles.xaml") });
        _buttonStyle = (Style)Resources["CalendarButtonStyle"];
        _title.Style = _buttonStyle;
        _title.Content = " ";
        var nav = new Grid { ColumnSpacing = 9, Height = 31, Margin = new Thickness(0, 0, 4, 14) };
        nav.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        for (var i = 0; i < 3; i++) nav.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        _title.HorizontalAlignment = HorizontalAlignment.Left;
        _title.Click += (_, _) => { _level = Math.Min(2, _level + 1); Render(); };
        nav.Children.Add(_title);
        var today = NavButton("今", "回到今天", (_, _) => GoToToday());
        var previous = ArrowButton(true, "上一页", (_, _) => Move(-1));
        var next = ArrowButton(false, "下一页", (_, _) => Move(1));
        Add(nav, today, 1); Add(nav, previous, 2); Add(nav, next, 3);
        if (desktop)
        {
            var handle = new Button { Style = _buttonStyle, Content = "⠿", Height = 20, HorizontalAlignment = HorizontalAlignment.Center, Padding = new Thickness(20, 0, 20, 0), IsTabStop = false };
            handle.PointerPressed += StartDesktopDrag;
            handle.PointerMoved += MoveDesktopDrag;
            handle.PointerReleased += EndDesktopDrag;
            handle.PointerCanceled += EndDesktopDrag;
            handle.PointerCaptureLost += EndDesktopDrag;
            _layout.Children.Add(handle);
        }
        _layout.Children.Add(nav);
        _layout.Children.Add(_grid);
        _footerSurface.Child = _footer;
        _layout.Children.Add(_footerSurface);
        Content = _layout;
        _grid.PointerWheelChanged += OnWheel;
        KeyDown += OnKey;
        Loaded += (_, _) => { Render(); _timer.Start(); };
        Unloaded += (_, _) => _timer.Stop();
        ActualThemeChanged += (_, _) => { if (_theme == ElementTheme.Default) Render(); };
        _timer.Tick += (_, _) =>
        {
            if (_today == DateTime.Today) return;
            if (_selected == _today) { _selected = DateTime.Today; _month = new(_selected.Year, _selected.Month, 1); }
            _today = DateTime.Today;
            Render();
        };
    }

    public void GoToToday()
    {
        _selected = DateTime.Today;
        _month = new(_selected.Year, _selected.Month, 1);
        _level = 0;
        Render();
    }

    public void Refresh() => Render();
    public void ApplyTheme(ElementTheme theme)
    {
        _theme = theme;
        RequestedTheme = theme;
        Render();
    }
    public Windows.Foundation.Size MeasureContent()
    {
        _layout.Measure(new Windows.Foundation.Size(PreferredWidth, double.PositiveInfinity));
        return new Windows.Foundation.Size(PreferredWidth, Math.Ceiling(_layout.DesiredSize.Height));
    }
    private bool IsDark => _theme == ElementTheme.Dark || (_theme == ElementTheme.Default && ActualTheme == ElementTheme.Dark);
    private SolidColorBrush TextBrush => Brush(IsDark ? "#F4F4F4" : "#202020");
    private SolidColorBrush MutedBrush => Brush(IsDark ? "#A8A8A8" : "#686868");
    private SolidColorBrush AccentBrush => Brush(IsDark ? "#60CDFF" : "#0067C0");

    private void Render()
    {
        if (_grid is null) return;
        _grid.Children.Clear(); _grid.RowDefinitions.Clear(); _grid.ColumnDefinitions.Clear();
        if (_level == 0) RenderDays(); else RenderPicker();
        RenderFooter();
        ContentSizeChanged?.Invoke();
    }

    private void RenderDays()
    {
        _title.Content = $"{_month:yyyy年M月}";
        for (var i = 0; i < 7; i++) _grid.ColumnDefinitions.Add(new());
        _grid.RowDefinitions.Add(new() { Height = new GridLength(39) });
        for (var i = 0; i < 6; i++) _grid.RowDefinitions.Add(new() { Height = new GridLength(43) });
        var labels = new[] { "一", "二", "三", "四", "五", "六", "日" };
        for (var i = 0; i < 7; i++)
        {
            var text = new TextBlock { Text = labels[i], FontSize = 14, Foreground = TextBrush, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 0) };
            Add(_grid, text, i);
        }
        var start = _month.AddDays(-(((int)_month.DayOfWeek + 6) % 7));
        var events = _holidays.GetEntries(start, start.AddDays(42));
        for (var i = 0; i < 42; i++)
        {
            var date = start.AddDays(i);
            var details = CalendarService.Get(date);
            var dayEvents = events.Where(e => e.Start <= date && date < e.End).ToList();
            var kind = dayEvents.Any(e => e.Kind == HolidayKind.Work) ? HolidayKind.Work : dayEvents.Any(e => e.Kind == HolidayKind.Rest) ? HolidayKind.Rest : HolidayKind.Event;
            var selected = date == _selected;
            var isToday = date == DateTime.Today;
            var button = new Button
            {
                Style = _buttonStyle, Width = 43, Height = 43,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                CornerRadius = new CornerRadius(22), BorderThickness = new Thickness(selected && !isToday ? 1 : 0),
                BorderBrush = AccentBrush,
                Background = isToday ? AccentBrush : new SolidColorBrush(Colors.Transparent),
                Foreground = isToday ? Brush(IsDark ? "#10202A" : "#FFFFFF") : date.Month == _month.Month ? TextBrush : Brush(IsDark ? "#666666" : "#BFBFBF")
            };
            var cell = new Grid { Width = 43, Height = 43 };
            var stack = new StackPanel { Spacing = 1, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            stack.Children.Add(new TextBlock { Text = date.Day.ToString(), FontSize = 14, LineHeight = 16, LineStackingStrategy = LineStackingStrategy.BlockLineHeight, HorizontalAlignment = HorizontalAlignment.Center });
            stack.Children.Add(new TextBlock { Text = details.LunarText, FontSize = 10, LineHeight = 12, LineStackingStrategy = LineStackingStrategy.BlockLineHeight, MaxWidth = 43, HorizontalAlignment = HorizontalAlignment.Center,
                FontWeight = string.IsNullOrEmpty(details.Term) ? Microsoft.UI.Text.FontWeights.Normal : Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = isToday ? button.Foreground : !string.IsNullOrEmpty(details.Term) ? Brush(IsDark ? "#81C784" : "#2E7D32") : date.Month == _month.Month ? TextBrush : button.Foreground });
            cell.Children.Add(stack);
            if (kind != HolidayKind.Event)
            {
                var rest = kind == HolidayKind.Rest;
                var dark = IsDark;
                var badge = new Grid
                {
                    Width = 14, Height = 14, Margin = new Thickness(0, 1, 1, 0),
                    HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
                };
                badge.Children.Add(new Ellipse { Fill = Brush(rest ? dark ? "#2D4D2D" : "#DFF6DD" : dark ? "#4D2D2F" : "#FDE7E9") });
                badge.Children.Add(new TextBlock
                {
                    Text = rest ? "休" : "班", FontFamily = new FontFamily("Microsoft YaHei UI"), FontSize = 9,
                    Width = 14, Height = 14, LineHeight = 14, LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                    FontWeight = Microsoft.UI.Text.FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Stretch, TextAlignment = TextAlignment.Center,
                    RenderTransform = new TranslateTransform { Y = -1 },
                    Foreground = Brush(rest ? dark ? "#99FF99" : "#107C10" : dark ? "#FF9999" : "#A80000")
                });
                cell.Children.Add(badge);
            }
            button.Content = cell;
            var description = $"{date:yyyy年M月d日} {details.LunarText} {string.Join("、", details.Festivals.Concat(dayEvents.Select(e => e.Name)).Distinct())}";
            AutomationProperties.SetName(button, description + (kind == HolidayKind.Rest ? " 休假" : kind == HolidayKind.Work ? " 补班" : ""));
            AutomationProperties.SetItemStatus(button, (selected ? "已选中 " : "") + (isToday ? "今天" : ""));
            button.IsEnabled = date.Year is >= 1901 and <= 2099;
            button.Click += (_, _) => { _selected = date; _month = new(date.Year, date.Month, 1); Render(); };
            Add(_grid, button, i % 7, i / 7 + 1);
        }
    }

    private void RenderPicker()
    {
        var firstYear = _month.Year / 10 * 10 - 1;
        _title.Content = _level == 1 ? $"{_month.Year}年" : $"{firstYear + 1} — {firstYear + 10}";
        for (var i = 0; i < 4; i++) _grid.ColumnDefinitions.Add(new());
        for (var i = 0; i < 3; i++) _grid.RowDefinitions.Add(new() { Height = new GridLength(92) });
        for (var i = 0; i < 12; i++)
        {
            var value = _level == 1 ? i + 1 : firstYear + i;
            var level = _level;
            var button = new Button { Style = _buttonStyle, Content = level == 1 ? $"{value}月" : value.ToString(), HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch, Margin = new Thickness(3), CornerRadius = new CornerRadius(8), IsEnabled = level == 1 || value is >= 1901 and <= 2099 };
            button.Click += (_, _) =>
            {
                _month = level == 1 ? new(_month.Year, value, 1) : new(value, _month.Month, 1);
                _level--;
                Render();
            };
            Add(_grid, button, i % 4, i / 4);
        }
    }

    private void RenderFooter()
    {
        _footer.Children.Clear();
        var config = _settings.Value;
        _footerSurface.Visibility = config.ShowFooter && _level == 0 && (config.ShowFestivals || config.ShowYiJi || config.ShowCountdown) ? Visibility.Visible : Visibility.Collapsed;
        _footerSurface.Background = BackgroundBrush(IsDark ? "#2D2D2D" : "#F7F7F7");
        if (_footerSurface.Visibility == Visibility.Collapsed) return;
        var details = CalendarService.Get(_selected);
        if (config.ShowFestivals)
        {
            var names = details.Festivals.Concat(_holidays.GetEntries(_selected, _selected.AddDays(1)).Select(e => e.Name)).Distinct().ToArray();
            var row = new TextBlock { Text = names.Length == 0 ? "当前无节假日" : string.Join(" · ", names), FontSize = 12, Foreground = MutedBrush, TextWrapping = TextWrapping.Wrap };
            ToolTipService.SetToolTip(row, row.Text);
            _footer.Children.Add(row);
        }
        if (config.ShowYiJi)
        {
            if (config.ShowFestivals) _footer.Children.Add(Divider());
            var rows = new StackPanel { Spacing = 6 };
            rows.Children.Add(YiJiRow("宜", details.Yi, true));
            rows.Children.Add(YiJiRow("忌", details.Ji, false));
            _footer.Children.Add(rows);
        }
        if (config.ShowCountdown)
        {
            if (config.ShowFestivals || config.ShowYiJi) _footer.Children.Add(Divider());
            var countdown = new Grid { ColumnSpacing = 6 };
            countdown.ColumnDefinitions.Add(new() { Width = new GridLength(14) });
            countdown.ColumnDefinitions.Add(new());
            countdown.Children.Add(new FontIcon { Glyph = "\uE917", FontSize = 14, Foreground = MutedBrush, VerticalAlignment = VerticalAlignment.Center });
            Add(countdown, new TextBlock { Text = _holidays.Countdown(DateTime.Today), FontSize = 12, Foreground = MutedBrush, TextWrapping = TextWrapping.Wrap }, 1);
            _footer.Children.Add(countdown);
        }
    }

    private Grid YiJiRow(string label, string text, bool yi)
    {
        var dark = IsDark;
        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new() { Width = new GridLength(18) }); grid.ColumnDefinitions.Add(new());
        grid.Children.Add(new Border
        {
            Background = Brush(yi ? dark ? "#1E3A2F" : "#E6F4EA" : dark ? "#3C1E1E" : "#FCE8E6"),
            CornerRadius = new CornerRadius(9), Width = 18, Height = 18,
            Child = new TextBlock { Text = label, Foreground = Brush(yi ? dark ? "#81C784" : "#1E8E3E" : dark ? "#F28B82" : "#D93025"), FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
        });
        var contents = new TextBlock { Text = text, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 13, LineHeight = 20, LineStackingStrategy = LineStackingStrategy.BlockLineHeight, Foreground = TextBrush, VerticalAlignment = VerticalAlignment.Center, RenderTransform = new TranslateTransform { Y = -1 } };
        ToolTipService.SetToolTip(contents, text);
        Add(grid, contents, 1);
        return grid;
    }

    private Border Divider() => new() { Height = 1, Margin = new Thickness(-4, 0, -4, 0), Background = Brush(IsDark ? "#424242" : "#E8E8E8") };
    private void Move(int direction)
    {
        var next = _level == 0 ? _month.AddMonths(direction) : _month.AddYears(direction * (_level == 1 ? 1 : 10));
        if (next.Year is < 1901 or > 2099) return;
        _month = next; Render();
    }
    private void OnWheel(object sender, PointerRoutedEventArgs e)
    {
        e.Handled = true;
        if (Environment.TickCount64 - _lastWheel < 160) return;
        _lastWheel = Environment.TickCount64;
        Move(e.GetCurrentPoint(_grid).Properties.MouseWheelDelta > 0 ? -1 : 1);
    }
    private void OnKey(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape) { if (_level > 0) { _level--; Render(); } else DismissRequested?.Invoke(); e.Handled = true; }
        else if (e.Key == VirtualKey.PageUp) { Move(-1); e.Handled = true; }
        else if (e.Key == VirtualKey.PageDown) { Move(1); e.Handled = true; }
        else if (e.Key == VirtualKey.Home) { GoToToday(); e.Handled = true; }
    }

    private void StartDesktopDrag(object sender, PointerRoutedEventArgs e)
    {
        var element = (UIElement)sender;
        if (!element.CapturePointer(e.Pointer)) return;
        _dragging = true;
        _dragPointerId = e.Pointer.PointerId;
        _dragStart = e.GetCurrentPoint(this).Position;
        DragStarted?.Invoke();
        e.Handled = true;
    }

    private void MoveDesktopDrag(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging || e.Pointer.PointerId != _dragPointerId) return;
        var position = e.GetCurrentPoint(this).Position;
        DragMoved?.Invoke(new Windows.Foundation.Point(position.X - _dragStart.X, position.Y - _dragStart.Y));
        e.Handled = true;
    }

    private void EndDesktopDrag(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging || e.Pointer.PointerId != _dragPointerId) return;
        _dragging = false;
        ((UIElement)sender).ReleasePointerCaptures();
        DragCompleted?.Invoke();
        e.Handled = true;
    }

    private Button NavButton(string text, string tooltip, RoutedEventHandler click)
    {
        var button = new Button { Style = _buttonStyle, Content = text, FontSize = 12, Width = 24, Height = 24, CornerRadius = new CornerRadius(12) };
        button.Click += click; AutomationProperties.SetName(button, tooltip); return button;
    }
    private Button ArrowButton(bool up, string tooltip, RoutedEventHandler click)
    {
        var button = NavButton("", tooltip, click);
        var triangle = new Polygon { Width = 8, Height = 5, Stretch = Stretch.Fill };
        triangle.Points = up
            ? new PointCollection { new(0, 5), new(4, 0), new(8, 5) }
            : new PointCollection { new(0, 0), new(8, 0), new(4, 5) };
        triangle.SetBinding(Shape.FillProperty, new Microsoft.UI.Xaml.Data.Binding { Source = button, Path = new PropertyPath("Foreground") });
        button.Content = triangle;
        return button;
    }
    internal static void Add(Grid grid, FrameworkElement child, int column, int row = 0) { Grid.SetColumn(child, column); Grid.SetRow(child, row); grid.Children.Add(child); }
    private SolidColorBrush BackgroundBrush(string hex)
    {
        var alpha = (byte)Math.Round((100 - Math.Clamp(_settings.Value.Transparency, 0, 100)) * 2.55);
        return new SolidColorBrush(Windows.UI.Color.FromArgb(alpha,
            Convert.ToByte(hex.Substring(1, 2), 16),
            Convert.ToByte(hex.Substring(3, 2), 16),
            Convert.ToByte(hex.Substring(5, 2), 16)));
    }
    internal static SolidColorBrush Brush(string hex) => new(Windows.UI.Color.FromArgb(255, Convert.ToByte(hex.Substring(1, 2), 16), Convert.ToByte(hex.Substring(3, 2), 16), Convert.ToByte(hex.Substring(5, 2), 16)));
}
