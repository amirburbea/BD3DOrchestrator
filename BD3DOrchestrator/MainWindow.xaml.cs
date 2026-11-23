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

    private int _logLineCount;
    private bool _followTail = true;
    private bool _scrollPending;

    private const int MaxLogLines = 5000;
    private const int LogTrimBatch = 500;

    public MainWindow(MainWindowViewModel vm, IMessageLogger messageLogger, JobRunner runner, AppSettings settings)
    {
        this._vm = vm;
        this._runner = runner;
        this._settings = settings;
        this.InitializeComponent();
        this.DataContext = vm;
        // TextBox has no ScrollChanged event of its own; it bubbles up from its internal ScrollViewer.
        this.TxtLog.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(this.TxtLog_ScrollChanged));
        messageLogger.MessageLogged += this.AppendLog;
        this._runner.CleanupStaleTempDirectories();
        this.AppendLog($"[Settings] Toolset: {this._settings.ToolsetDirectory}");
        this.AppendLog($"[Settings] BD3D: {this._settings.BD3DExecutable}");
        this.AppendLog($"[Settings] FFmpeg: {this._settings.FfmpegExecutable}");
        this.AppendLog($"[Settings] Projects: {this._settings.ProjectDirectory}");
        this.AppendLog($"[Settings] Output: {(string.IsNullOrWhiteSpace(this._settings.OutputDirectory) ? "(project folder)" : this._settings.OutputDirectory)}");
        this.AppendLog("Ready. Drag & Drop a 3D movie folder or MKV file to begin.");
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
    /// The log's entry point; may be called from any thread. The line is stamped here, before any hop to the UI thread,
    /// so the time is when it was logged rather than when the UI got around to showing it.
    /// </summary>
    private void AppendLog(string message)
    {
        string line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        if (this.Dispatcher.CheckAccess())
        {
            WriteLogLine();
        }
        else
        {
            this.Dispatcher.BeginInvoke(WriteLogLine, DispatcherPriority.Background);
        }

        /// <summary>
        /// UI thread only. Appends a line to the log box and, while the user is at the bottom, keeps the newest line in view;
        /// scrolling up to read or select older text isn't yanked away. Once the box holds more than MaxLogLines + LogTrimBatch
        /// lines, the oldest LogTrimBatch lines are dropped in one go (trimming per line would rebuild the whole text every time).
        /// </summary>
        void WriteLogLine()
        {
            this.TxtLog.AppendText(line + Environment.NewLine);
            this._logLineCount += 1 + line.AsSpan().Count('\n'); // a message may itself contain line breaks
            if (this._logLineCount > MaxLogLines + LogTrimBatch)
            {
                string text = this.TxtLog.Text;
                int cut = 0;
                for (int removed = 0; removed < LogTrimBatch && cut >= 0; removed++)
                {
                    cut = text.IndexOf('\n', cut) is int newline and >= 0 ? newline + 1 : -1;
                }
                if (cut > 0)
                {
                    this.TxtLog.Text = text[cut..];
                    this._logLineCount -= LogTrimBatch;
                }
            }
            // Always deferred, even though we're on the UI thread: ScrollToEnd called now would scroll to the old end, because
            // layout hasn't measured the new text yet. Queued requests are coalesced so a burst of lines costs one scroll.
            if (!this._followTail || this._scrollPending)
            {
                return;
            }
            this._scrollPending = true;
            this.Dispatcher.BeginInvoke(() =>
            {
                this._scrollPending = false;
                this.TxtLog.ScrollToEnd();
            }, DispatcherPriority.Loaded);
        }
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = !this._vm.IsRunning && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (!this._vm.IsRunning && e.Data.GetData(DataFormats.FileDrop) is string[] and [{ } droppedPath, ..])
        {
            _ = this.LoadAsync(droppedPath);
        }
    }

    private void BtnBrowse_Click(object sender, RoutedEventArgs e)
    {
        OpenFileDialog fileDlg = new()
        {
            Title = "Select 3D MVC MKV File",
            Filter = "MKV Files (*.mkv)|*.mkv|All Files (*.*)|*.*"
        };
        if (fileDlg.ShowDialog() == true && fileDlg.FileName is { Length: not 0 } fileName)
        {
            _ = this.LoadAsync(fileName);
        }
    }

    private void BtnBrowseSubtitleDonor_Click(object sender, RoutedEventArgs e)
    {
        OpenFileDialog fileDlg = new()
        {
            Title = "Select MKV or MKS to Steal Subtitles From",
            Filter = "MKV / MKS Files (*.mkv;*.mks)|*.mkv;*.mks|All Files (*.*)|*.*"
        };
        if (fileDlg.ShowDialog() == true && fileDlg.FileName is { Length: not 0 } fileName)
        {
            this._vm.Job.SubtitleDonorPath = fileName;
        }
    }

    private void BtnClearSubtitleDonor_Click(object sender, RoutedEventArgs e)
    {
        this._vm.Job.SubtitleDonorPath = string.Empty;
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
            this.AppendLog($"Could not load '{path}': {ex.Message}");
        }
    }

    private async void BtnAutoRunAll_Click(object sender, RoutedEventArgs e)
    {
        if (!File.Exists(this._settings.BD3DExecutable))
        {
            this.AppendLog($"Error: BD3D2MK3D not found at {this._settings.BD3DExecutable}");
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
        bool succeeded = false;
        try
        {
            // Task.Run keeps the runner's synchronous start-up work off the UI thread too.
            succeeded = await Task.Run(() => this._runner.RunAsync(job, CancellationToken.None));
        }
        catch (Exception ex)
        {
            this.AppendLog($"Pipeline error: {ex.Message}");
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
