using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;

namespace BD3DOrchestrator;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _vm;
    private readonly AppSettings _settings;
    private readonly JobRunner _runner;

    private CancellationTokenSource? _pipelineCts;
    private int _logLineCount;
    private bool _followTail = true;
    private bool _scrollPending;

    private const int MaxLogLines = 5000;
    private const int LogTrimBatch = 500;

    public MainWindow(MainWindowViewModel vm, JobRunner runner, AppSettings settings)
    {
        this._vm = vm;
        this._runner = runner;
        this._settings = settings;
        this.InitializeComponent();
        this.DataContext = vm;
        // TextBox has no ScrollChanged event of its own; it bubbles up from its internal ScrollViewer.
        this.TxtLog.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(this.TxtLog_ScrollChanged));
        vm.LineLogged += this.AppendLog;
        JobRunner.CleanupStaleTempDirectories();
        this._vm.Log($"[Settings] Toolset: {this._settings.ToolsetDirectory}");
        this._vm.Log($"[Settings] BD3D: {this._settings.BD3DExecutable}");
        this._vm.Log($"[Settings] FFmpeg: {this._settings.FfmpegExecutable}");
        this._vm.Log($"[Settings] Projects: {this._settings.ProjectDirectory}");
        this._vm.Log($"[Settings] Output: {(string.IsNullOrWhiteSpace(this._settings.OutputDirectory) ? "(project folder)" : this._settings.OutputDirectory)}");
        this._vm.Log("Ready. Drag & Drop a 3D movie folder or MKV file to begin.");
    }

    /// <summary>
    /// Remembers whether the user has the view at the bottom. Only scrolling by the user counts: when the text grows or the box
    /// is resized the extent/viewport change, and reading the position then would say "not at the bottom" just because layout
    /// hasn't caught up yet, so following would stop for good.
    /// </summary>
    private void TxtLog_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ExtentHeightChange == 0 && e.ViewportHeightChange == 0)
        {
            this._followTail = e.VerticalOffset + e.ViewportHeight >= e.ExtentHeight - 1;
        }
    }

    /// <summary>
    /// Appends a line to the log box and, while the user is at the bottom, keeps the newest line in view; scrolling up to read
    /// or select older text isn't yanked away. Once the box holds more than MaxLogLines + LogTrimBatch lines, the oldest
    /// LogTrimBatch lines are dropped in one go (trimming per line would rebuild the whole text every time).
    /// </summary>
    private void AppendLog(string line)
    {
        this.TxtLog.AppendText(line + Environment.NewLine);
        this._logLineCount += 1 + line.AsSpan().Count('\n'); // a message may itself contain line breaks
        if (this._logLineCount > MaxLogLines + LogTrimBatch)
        {
            string text = this.TxtLog.Text;
            int cut = 0;
            for (int removed = 0; removed < LogTrimBatch && cut >= 0; removed++)
            {
                cut = text.IndexOf('\n', cut) is var newline and >= 0 ? newline + 1 : -1;
            }
            if (cut > 0)
            {
                this.TxtLog.Text = text[cut..];
                this._logLineCount -= LogTrimBatch;
            }
        }
        // ScrollToEnd has to wait until layout has measured the new text, and queued requests are coalesced so a burst of
        // lines costs one scroll.
        if (!this._followTail || this._scrollPending)
        {
            return;
        }
        this._scrollPending = true;
        if (!this.Dispatcher.CheckAccess())
        {
            this.Dispatcher.BeginInvoke(new Action(ScrollToEnd), DispatcherPriority.Loaded);
        }
        else
        {
            ScrollToEnd();
        }

        void ScrollToEnd()
        {
            this._scrollPending = false;
            this.TxtLog.ScrollToEnd();
        }
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
        // Only one run at a time (the button is disabled while running), so there is never a previous source to cancel.
        using CancellationTokenSource cts = new();
        this._pipelineCts = cts;
        CancellationToken ct = cts.Token;
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
            this._pipelineCts = null;
            this._vm.IsRunning = false;
            this._vm.ActionButtonText = "🚀 Auto-Run Pipeline (Demux ➔ Patch AVS ➔ Ingest Audio ➔ Encode & Mux)";
        }
        if (succeeded)
        {
            this._vm.Job = new();
        }
    }
}
