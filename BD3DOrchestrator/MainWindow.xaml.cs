using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;

namespace BD3DOrchestrator;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly AppSettings _settings;
    private readonly JobRunner _runner;

    private CancellationTokenSource? _pipelineCts;
    private bool _scrollPending;

    public MainWindow(MainViewModel vm, JobRunner runner, AppSettings settings)
    {
        this._vm = vm;
        this._runner = runner;
        this._settings = settings;

        this.InitializeComponent();
        this.DataContext = vm;
        vm.LogEntries.CollectionChanged += (_, _) => this.ScrollLogToEnd();

        JobRunner.CleanupStaleTempDirectories();
        this._vm.Log($"[Settings] Toolset: {this._settings.ToolsetDirectory}");
        this._vm.Log($"[Settings] BD3D: {this._settings.BD3DExecutable}");
        this._vm.Log($"[Settings] FFmpeg: {this._settings.FfmpegExecutable}");
        this._vm.Log($"[Settings] Projects: {this._settings.ProjectDirectory}");
        this._vm.Log($"[Settings] Output: {(string.IsNullOrWhiteSpace(this._settings.OutputDirectory) ? "(project folder)" : this._settings.OutputDirectory)}");
        this._vm.Log("Ready. Drag & Drop a 3D movie folder or MKV file to begin.");
    }

    /// <summary>
    /// Scrolls the log to the newest line once layout has caught up with the new entry. Doing it inside CollectionChanged is too
    /// early (the list hasn't measured the new row yet, so it stays put), and queued calls are coalesced so a burst of lines
    /// costs one scroll.
    /// </summary>
    private void ScrollLogToEnd()
    {
        if (this._scrollPending)
        {
            return;
        }

        this._scrollPending = true;
        this.Dispatcher.BeginInvoke(() =>
        {
            this._scrollPending = false;
            if (this._vm.LogEntries.Count > 0)
            {
                this.LstLog.ScrollIntoView(this._vm.LogEntries[^1]);
            }
        }, DispatcherPriority.Loaded);
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = this._vm.CanExecute && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (this._vm.CanExecute && e.Data.GetData(DataFormats.FileDrop) is string[] and [{ } droppedPath, ..])
        {
            await this.LoadAsync(droppedPath);
        }
    }

    private async void BtnBrowse_Click(object sender, RoutedEventArgs e)
    {
        OpenFileDialog fileDlg = new()
        {
            Title = "Select 3D MVC MKV File",
            Filter = "MKV Files (*.mkv)|*.mkv|All Files (*.*)|*.*"
        };

        if (fileDlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(fileDlg.FileName))
        {
            await this.LoadAsync(fileDlg.FileName);
        }
    }

    // The window awaits one task and resumes on the UI thread; everything inside the runner runs off it.
    private async Task LoadAsync(string path)
    {
        try
        {
            await this._runner.LoadAsync(this._vm.Job, path);
        }
        catch (Exception ex)
        {
            this._vm.Log($"Could not load '{path}': {ex.Message}");
        }
    }

    private async void BtnAutoRunAll_Click(object sender, RoutedEventArgs e)
    {
        if (!File.Exists(this._settings.BD3DExecutable))
        {
            this._vm.Log($"Error: BD3D2MK3D not found at {this._settings.BD3DExecutable}");
            return;
        }

        Job job = this._vm.Job;
        if (!job.IsLoaded)
        {
            MessageBox.Show("Please select a movie file first.", "No File Loaded", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        this._vm.IsRunning = true;
        this._vm.ActionButtonText = "⏳ Pipeline Running in Background...";

        this._pipelineCts?.Cancel();
        this._pipelineCts = new CancellationTokenSource();
        CancellationToken ct = this._pipelineCts.Token;
        bool succeeded = false;

        try
        {
            // Task.Run keeps the runner's synchronous start-up work off the UI thread too.
            succeeded = await Task.Run(() => this._runner.RunAsync(job, ct), ct);
        }
        catch (Exception ex)
        {
            this._vm.Log($"Pipeline error: {ex.Message}");
        }
        finally
        {
            this._vm.IsRunning = false;
            this._vm.ActionButtonText = "🚀 Auto-Run Pipeline (Demux ➔ Patch AVS ➔ Ingest Audio ➔ Encode & Mux)";
        }

        if (succeeded)
        {
            this._vm.Job = new();
        }
    }
}
