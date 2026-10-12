using System;
using Microsoft.UI.Xaml;

namespace KSHPLAYER;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) => AppLog.Write("Erreur non gérée", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => AppLog.Write("Erreur fatale", e.ExceptionObject as Exception);
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
    }
}
