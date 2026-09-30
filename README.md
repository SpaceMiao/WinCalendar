# WinCalendar

Windows 11 日历，使用 C# / .NET 8 / WinUI 3。保留 Visual Studio 单项目 MSIX 结构。

## 运行

在 Visual Studio 中打开 `WinCalendar.slnx`，选择 **x64 / Debug / WinCalendar (Package)**，构建并运行。普通启动打开设置，关闭设置后继续后台运行。重复启动会打开已有实例的设置。

本地非打包运行也可使用以下命令：

```powershell
dotnet build WinCalendar.csproj -p:Platform=x64 -p:WindowsPackageType=None -p:GenerateAppxPackageOnBuild=false -p:AppxPackageSigningEnabled=false -p:OutputPath=bin/LocalRun/
.\bin\LocalRun\WinCalendar.exe
```

需要 .NET 8 Desktop Runtime 和项目所用版本的 Windows App Runtime（当前依赖 Windows App SDK 2.5.1）。MSIX 部署由 Visual Studio 管理。UI Automation 使用 Windows Desktop 框架程序集；所有应用页面均为 WinUI 3。

## 功能

- 点击任务栏时钟弹出日历，支持各显示器的时钟区域和不同 DPI；再次点击、失焦或按 Esc 收起。右键菜单包含设置与退出。
- 月视图展示公历、农历、节气和休班标记；滚轮或上下箭头翻月，点击年月逐级选择月份和年份。`Home` 返回今天，`PageUp / PageDown` 翻页。
- 主界面采用参考项目的紧凑布局：40×40日期格、绿色休假角标、红色补班角标和浅灰底部信息区。窗口按实际内容测量高度，没有整页滚动条或重复边框；可用屏幕空间不足时整体等比缩小。
- 底部展示所选日期节假日、宜忌及以今天为基准的假期倒计时。假期内显示“正在休假”。翻月保留选中日期，重新打开浮窗选中今天。
- 桌面日历可用顶部拖动柄移动，保存位置；桌面窗口不随失焦隐藏。
- 设置包含深色、浅色、跟随系统主题，透明度，开机启动，桌面组件、任务栏替换和日历内容开关。
- 自定义时钟支持时间、日期格式和即时预览，使用原时钟区域上的覆盖窗口，不修改系统时间或全局区域格式。长格式受原时钟区域宽度限制。
- 托盘图标左键打开日历、右键打开菜单；即使关闭任务栏替换也可操作。

## ICS 数据

默认源：[China Calendar 2026](https://chinacalendar.app/ics/china-calendar-2026.ics)。可在“日历内容”设置中修改为其他 HTTP/HTTPS ICS 地址。

- `休｜名称` / `休|名称`：休假；`班｜名称` / `班|名称`：补班。未带前缀的事件仅显示名称。
- 支持全天、多天事件，以及 Ical.Net 支持的重复规则、例外日期、折行和转义。结束日期按 ICS 的排除规则处理；同日冲突时补班优先。
- 启动时及每6小时更新，提供手动刷新。各地址分别缓存，下载或解析失败保留原数据。内置2026年快照供首次离线启动使用。
- 农历、节气、宜忌独立离线计算，未收录年份不推测放假与补班安排。默认 URL 是固定年份，需要在后续年份更换。
- 配置、缓存和错误日志位于 `%LOCALAPPDATA%\WinCalendar`。日志文件为 `app.log`。

## 实现位置

- `Views/CalendarView.cs`、`Views/CalendarStyles.xaml`：日历、年月选择及按钮样式；`MainWindow.xaml.cs`：浮窗和桌面窗口。
- `Views/SettingsWindow.cs`、`Views/ContextMenuWindow.cs`：设置与菜单。
- `Services/HolidayService.cs`、`CalendarService.cs`：ICS、农历及宜忌。
- `Services/TaskbarService.cs`、`ClockOverlayService.cs`：时钟检测、点击钩子及自定义时钟。
- `Services/AppController.cs`：窗口生命周期、更新定时器和退出清理。

任务栏检测使用 UI Automation，在独立后台任务中枚举主、副任务栏；鼠标钩子只读取缓存并派发点击。Explorer 重启后自动重新检测。Windows 任务栏内部控件结构随系统版本可能变化，检测状态可在通用设置查看。

农历依赖 [lunar-csharp](https://github.com/6tail/lunar-csharp)，ICS 解析依赖 [Ical.Net](https://github.com/ical-org/ical.net)，均按上游 MIT 许可使用。默认ICS数据快照来自需求中提供的 China Calendar 文件。

按项目要求未添加测试项目或测试代码。
