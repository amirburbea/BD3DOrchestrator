using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

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
        this.AppendLog("Ready. Browse for a 3D MKV file to begin.");
        if (this._vm.JobEditor.Capabilities.HasFlag(HardwareCapabilities.Nvenc))
        {
            this.AppendLog("[NVENC] 10-bit hevc_nvenc hardware support detected.");
        }
        if (this._vm.JobEditor.Capabilities.HasFlag(HardwareCapabilities.Qsv))
        {
            string vppNote = this._vm.JobEditor.Capabilities.HasFlag(HardwareCapabilities.VppQsv) ? " (with vpp_qsv)" : "";
            this.AppendLog($"[QSV] 10-bit hevc_qsv hardware support detected{vppNote}.");
        }
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

    private async void BtnAutoRunAll_Click(object sender, RoutedEventArgs e)
    {
        if (!File.Exists(this._settings.BD3DExecutable))
        {
            this.AppendLog($"Error: BD3D2MK3D not found at {this._settings.BD3DExecutable}");
            return;
        }
        Job job = this._vm.JobEditor.Job;
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
            this._vm.ActionButtonText = "🚀 Auto-Run Pipeline";
        }
        if (succeeded)
        {
            this._vm.ResetJob();
        }
    }
}
