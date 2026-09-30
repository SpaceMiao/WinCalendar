using System;
using System.Linq;
using System.Threading;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using WinCalendar.Interop;
using WinCalendar.Services;

namespace WinCalendar;

public partial class App : Application
{
    private Mutex? _instance;
    private AppController? _controller;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, args) => SettingsStore.Log(args.Exception);
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _instance = new Mutex(true, @"Local\WinCalendar.Desktop.SingleInstance", out var first);
        if (!first)
        {
            var host = Native.FindWindow("WinCalendar.MessageHost", "WinCalendar.MessageHost");
            if (host != 0) Native.PostMessage(host, TaskbarService.ShowSettingsMessage, 0, 0);
            _instance.Dispose(); _instance = null;
            Exit();
            return;
        }
        _controller = new AppController();
        var startup = Environment.GetCommandLineArgs().Contains("--startup")
            || AppInstance.GetCurrent().GetActivatedEventArgs().Kind == ExtendedActivationKind.StartupTask;
        if (!startup) _controller.ShowSettings();
    }
}
