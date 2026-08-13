using System.Windows;
using HalimRecovery.Core.Logging;

namespace HalimRecovery.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Log.Info("App", $"Halim Recovery {AppVersion} started");
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error("App", "Unhandled UI exception", args.Exception);
            MessageBox.Show(
                $"An unexpected error occurred:\n\n{args.Exception.Message}\n\nDetails were written to the log:\n{Log.LogDirectory}",
                "Halim Recovery", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Error("App", "Fatal exception", args.ExceptionObject as Exception);
    }

    public static string AppVersion => "0.5.1";
}
