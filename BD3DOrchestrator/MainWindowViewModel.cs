namespace BD3DOrchestrator;

/// <summary>App-level UI state. Everything about editing one movie lives on <see cref="JobEditor"/>.</summary>
public sealed class MainWindowViewModel(JobEditorViewModel jobEditor) : ObservableObject
{
    public JobEditorViewModel JobEditor { get; } = jobEditor;

    public bool IsRunning
    {
        get;
        set
        {
            if (this.SetField(ref field, value))
            {
                this.JobEditor.IsEditable = !value;
            }
        }
    }

    public string ActionButtonText
    {
        get;
        set => this.SetField(ref field, value);
    } = "🚀 Auto-Run Pipeline";

    public void ResetJob() => this.JobEditor.ResetJob();
}
