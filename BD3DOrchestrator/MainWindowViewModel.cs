using System;
using System.Windows;
using System.Windows.Threading;

namespace BD3DOrchestrator;

/// <summary>App-level UI state. Everything about one movie lives on <see cref="Job"/>.</summary>
public sealed class MainWindowViewModel : ObservableObject
{
    /// <summary>Raised on the UI thread (or the calling thread when there is no UI) with each timestamped log line. The view owns the text.</summary>
    public event Action<string>? LineLogged;

    public Job Job
    {
        get;
        set => this.SetField(ref field, value);
    } = new();

    public bool IsRunning
    {
        get;
        set
        {
            if (this.SetField(ref field, value))
            {
                this.OnPropertyChanged(nameof(this.CanExecute));
            }
        }
    }

    public bool CanExecute => !this.IsRunning;

    public string ActionButtonText
    {
        get;
        set => this.SetField(ref field, value);
    } = "🚀 Auto-Run Pipeline (Demux ➔ Patch AVS ➔ Ingest Audio ➔ Encode & Mux)";

    /// <summary>Timestamps a message and raises <see cref="LineLogged"/> on the UI thread: queued when called from another thread, raised directly when already on the UI thread or when there is no UI (e.g. in tests).</summary>
    public void Log(string message)
    {
        string formatted = $"[{DateTime.Now:HH:mm:ss}] {message}";
        if (Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(() => this.LineLogged?.Invoke(formatted), DispatcherPriority.Background);
        }
        else
        {
            this.LineLogged?.Invoke(formatted);
        }
    }
}
