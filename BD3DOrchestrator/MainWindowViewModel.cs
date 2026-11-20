using System;

namespace BD3DOrchestrator;

/// <summary>App-level UI state. Everything about one movie lives on <see cref="Job"/>.</summary>
public sealed class MainWindowViewModel : ObservableObject, IMessageLogger
{
    public event Action<string>? MessageLogged;

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

    public void Log(string message) => this.MessageLogged?.Invoke(message);
}
