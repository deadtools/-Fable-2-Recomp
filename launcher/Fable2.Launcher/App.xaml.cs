using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace Fable2Launcher;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception error)
                LauncherStartupDiagnostics.Report("Unhandled application exception", error);
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
            LauncherStartupDiagnostics.Report("Unobserved background task exception", args.Exception);
        base.OnStartup(e);
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LauncherStartupDiagnostics.Report("WPF dispatcher exception", e.Exception);
        try
        {
            MessageBox.Show(
                "Fable II Launcher encountered a startup error.\n\n" + e.Exception.Message +
                "\n\nDetails saved to:\n" + LauncherStartupDiagnostics.LogPath,
                "Fable II Launcher 1.6.1 — startup error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch { /* best effort: the exception is still recorded */ }
        // Do not claim recovery from potentially incomplete WPF initialization.
        // The process may exit, but the error will no longer be silent.
    }
}
