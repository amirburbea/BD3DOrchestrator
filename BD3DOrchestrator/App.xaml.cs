using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;

namespace BD3DOrchestrator;

public partial class App(IServiceProvider services) : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 1. AppDomain Unhandled Exceptions (general thread pool / background threads)
        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                this.LogCrash("AppDomain", ex);
            }
        };

        // 2. WPF Dispatcher Unhandled Exceptions (UI thread)
        this.DispatcherUnhandledException += (s, args) =>
        {
            this.LogCrash("WPF Dispatcher", args.Exception);
            MessageBox.Show(
                $"An error occurred: {args.Exception.Message}\r\n\r\nDetails written to crash.log",
                "Unhandled Exception",
                MessageBoxButton.OK,
                MessageBoxImage.Error
            );
            args.Handled = true;
        };

        // 3. TaskScheduler Unobserved Task Exceptions (unawaited async tasks)
        TaskScheduler.UnobservedTaskException += (s, args) =>
        {
            this.LogCrash("TaskScheduler", args.Exception);
            args.SetObserved();
        };

        this.MainWindow = services.GetRequiredService<MainWindow>();
        this.MainWindow.Show();
    }

    private void LogCrash(string source, Exception ex)
    {
        try
        {
            string appDir = AppDomain.CurrentDomain.BaseDirectory;
            string logPath = Path.Combine(appDir, "crash.log");
            string entry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{source}] {ex.GetType().FullName}: {ex.Message}\r\n{ex.StackTrace}\r\n";
            if (ex.InnerException is { } inner)
            {
                entry += $"InnerException: {inner.GetType().FullName}: {inner.Message}\r\n{inner.StackTrace}\r\n";
            }
            entry += new string('-', 80) + "\r\n";

            File.AppendAllText(logPath, entry);
        }
        catch
        {
        }
    }
}
