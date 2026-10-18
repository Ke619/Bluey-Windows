using System;
using System.Threading;
using System.Windows;

namespace Bluey;

public static class Program
{
    [STAThread]
    public static void Main()
    {
        using var mutex = new Mutex(true, "Bluey.Windows.SingleInstance", out var first);
        if (!first) return;

        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write("Unhandled: " + e.ExceptionObject);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.DispatcherUnhandledException += (_, e) =>
        {
            Log.Write("UI error: " + e.Exception);
            e.Handled = true;
        };

        try
        {
            var host = new BlueyHost(app);
            host.Start();
            app.Run();
            host.Dispose();
        }
        catch (Exception ex)
        {
            Log.Write("Fatal: " + ex);
            System.Windows.MessageBox.Show("Bluey couldn't start:\n\n" + ex.Message + "\n\nDetails are in %AppData%\\Bluey\\log.txt",
                "Bluey", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
