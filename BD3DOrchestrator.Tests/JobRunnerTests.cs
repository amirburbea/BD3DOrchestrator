using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BD3DOrchestrator.Tests.Fakes;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace BD3DOrchestrator.Tests;

public sealed class JobRunnerTests
{
    private const string Bd3dExe = @"C:\tools\BD3D2MK3D.exe";

    private sealed class FakeProcessManager : IProcessManager
    {
        public Func<string, string?, ProcessHandle?>? OnStartShell { get; set; }
        public Func<string, ProcessHandle[]>? OnGetProcessesByName { get; set; }
        public List<(string FileName, string? WorkingDirectory)> StartedShells { get; } = [];

        public ProcessHandle? StartShell(string fileName, string? workingDirectory = null)
        {
            this.StartedShells.Add((fileName, workingDirectory));
            return this.OnStartShell?.Invoke(fileName, workingDirectory);
        }

        public ProcessHandle[] GetProcessesByName(string processName) => this.OnGetProcessesByName?.Invoke(processName) ?? [];
    }

    /// <summary>A <see cref="JobRunner"/> wired to fakes, with a project directory at P:\Proj.</summary>
    private sealed class Harness : IDisposable
    {
        private readonly HttpClient _httpClient;

        public Harness(Func<HttpRequestMessage, HttpResponseMessage>? tmdb = null, FakeEncodeProfile? profile = null)
        {
            this.Profile = profile ?? ProfileWithCrf(18);
            this.Fs.AddDirectory(this.Settings.ProjectDirectory);
            this._httpClient = new(new FakeHttpMessageHandler(tmdb));
            this.Runner = new(
                this.Settings,
                this.Profile,
                new(this.Settings, this.Fs, this.ProcessRunner),
                new(this._httpClient, this.Settings, this.Fs),
                new(this.Settings, this.Profile, this.Logger, this.Fs, this.ProcessRunner),
                this.Fs,
                this.Logger,
                this.Processes,
                this.Time
            );
        }

        public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 11, 20, 14, 0, 0, TimeSpan.Zero));
        public DateTime NowLocal => this.Time.GetLocalNow().DateTime;
        public FakeFileSystem Fs { get; } = new();
        public FakeProcessRunner ProcessRunner { get; } = new();
        public FakeMessageLogger Logger { get; } = new();
        public FakeProcessManager Processes { get; } = new();
        public FakeEncodeProfile Profile { get; }
        public AppSettings Settings { get; } = new()
        {
            ToolsetDirectory = @"C:\tools",
            BD3DExecutable = Bd3dExe,
            ProjectDirectory = @"P:\Proj",
            TmdbApiReadAccessToken = "token"
        };
        public JobRunner Runner { get; }

        public void Dispose() => this._httpClient.Dispose();
    }

    /// <summary>What EncodeProfile.TclBlock emits for a profile's "x264crf".</summary>
    private static string ProfileCrfBlock(string crf) => $"set ::config(x264crf) \"{crf}\"\nset ::x264crf \"{crf}\"\n";

    private static FakeEncodeProfile ProfileWithCrf(decimal crf) => new()
    {
        Crf = crf,
        TclBlock = ProfileCrfBlock(EncodeProfile.FormatCrf(crf))
    };

    /// <summary>Runs a job whose BD3D2MK3D exits at once into an existing project; returns the plugin.tcl it was given.</summary>
    private static async Task<string> RunAndCaptureHookAsync(Harness h, Job job)
    {
        string? hook = null;
        h.Processes.OnStartShell = (fileName, workingDirectory) =>
        {
            if (fileName == Bd3dExe)
            {
                hook = h.Fs.ReadAllText(Path.Combine(workingDirectory!, "plugin.tcl"));
            }
            return new(101); // exits immediately
        };
        await h.Runner.RunAsync(job, CancellationToken.None);
        return hook!;
    }

    private static Job LoadedJob(string title) => new()
    {
        MovieTitle = title,
        ApplyAvsTweak = false,
        Inspection = new() { MkvPath = $@"M:\Movies\{PathMethods.SanitizeFileName(title)}.3d.mvc.mkv", MovieTitle = title }
    };

    // ---- LoadAsync ----

    [Test]
    [Arguments("2013-11-27", 16, true)]   // animation, pre-2022
    [Arguments("2022-03-11", 16, false)]  // animation, 2022 or later
    [Arguments(null, 16, false)]          // animation, year unknown
    [Arguments("2009-12-18", 878, false)] // live action
    public async Task LoadAsync_AutoEnablesAvsTweakOnlyForAnimationBefore2022(string? releaseDate, int genreId, bool expectedTweak)
    {
        using Harness h = new(_ => new TmdbSearchResults(new TmdbMovie("Movie", releaseDate, genreId)).ToResponse());
        h.Fs.AddFile(@"M:\Movies\Movie.3d.mvc.mkv");
        Job job = new() { ApplyAvsTweak = !expectedTweak };

        await h.Runner.LoadAsync(job, @"M:\Movies\Movie.3d.mvc.mkv");

        await Assert.That(job.ApplyAvsTweak).IsEqualTo(expectedTweak);
    }

    [Test]
    public async Task LoadAsync_AppendsTmdbYearToTitleAndRecordsDetectionReason()
    {
        using Harness h = new(_ => new TmdbSearchResults(new TmdbMovie("Frozen", "2013-11-27", 16)).ToResponse());
        h.Fs.AddFile(@"M:\Movies\Frozen.3d.mvc.mkv");
        Job job = new();

        await h.Runner.LoadAsync(job, @"M:\Movies\Frozen.3d.mvc.mkv");

        await Assert.That(job.InputPath).IsEqualTo(@"M:\Movies\Frozen.3d.mvc.mkv");
        await Assert.That(job.MovieTitle).IsEqualTo("Frozen (2013)");
        await Assert.That(job.DetectionReason).IsEqualTo("Detection: TMDb confirmed Animation (Frozen); year 2013 from TMDb");
        await Assert.That(job.Inspection!.MkvPath).IsEqualTo(@"M:\Movies\Frozen.3d.mvc.mkv");
    }

    // ---- RunAsync ----

    [Test]
    public async Task RunAsync_NothingLoaded_ReturnsFalseWithoutLaunchingAnything()
    {
        using Harness h = new();

        bool succeeded = await h.Runner.RunAsync(new Job(), CancellationToken.None);

        await Assert.That(succeeded).IsFalse();
        await Assert.That(h.Processes.StartedShells.Count).IsEqualTo(0);
    }

    [Test]
    public async Task RunAsync_Bd3dFailsToStart_ReturnsFalseAndDeletesSandbox()
    {
        using Harness h = new();
        string? sandbox = null;
        h.Processes.OnStartShell = (_, workingDirectory) =>
        {
            sandbox = workingDirectory;
            return null;
        };

        bool succeeded = await h.Runner.RunAsync(LoadedJob("Up"), CancellationToken.None);

        await Assert.That(succeeded).IsFalse();
        await Assert.That(sandbox).IsNotNull();
        await Assert.That(h.Fs.DirectoryExists(sandbox!)).IsFalse();
        await Assert.That(h.Processes.StartedShells.Count).IsEqualTo(1);
    }

    [Test]
    public async Task RunAsync_WritesPluginHookWithTclProcsAndEscapedTitle()
    {
        using Harness h = new();
        // The sandbox is deleted when RunAsync returns, so read the hook when BD3D2MK3D would be launched.
        string? hook = null;
        h.Processes.OnStartShell = (_, workingDirectory) =>
        {
            hook = h.Fs.ReadAllText(Path.Combine(workingDirectory!, "plugin.tcl"));
            return null;
        };
        Job job = LoadedJob("Say \"Hi\" [3D]");
        job.Inspection!.MkvPath = @"M:\Movies\Hi.3d.mvc.mkv";

        await h.Runner.RunAsync(job, CancellationToken.None);

        await Assert.That(hook).IsNotNull();
        await Assert.That(hook!).Contains("proc ::GetMkvWritingApplication {mkvfile} {");
        await Assert.That(hook!).Contains("proc ::tk_messageBox {args} {");
        await Assert.That(hook!).Contains("proc ::exec {args} {");
        await Assert.That(hook!).Contains("""OpenMKV "M:/Movies/Hi.3d.mvc.mkv" """.TrimEnd());
        await Assert.That(hook!).Contains("""set ::moviename "Say \"Hi\" \[3D\]" """.TrimEnd());
    }

    [Test]
    public async Task NewJob_HasExtraQualityOff()
    {
        await Assert.That(new Job().UseExtraQuality).IsFalse();
    }

    [Test]
    public async Task NewJob_HasStealDefaultsOffAndDisabled()
    {
        Job job = new();
        await Assert.That(job.StealPath).IsEqualTo(string.Empty);
        await Assert.That(job.DonorHasAudio).IsFalse();
        await Assert.That(job.DonorHasSubtitles).IsFalse();
        await Assert.That(job.StealAudio).IsFalse();
        await Assert.That(job.StealSubtitles).IsFalse();
    }

    [Test]
    public async Task LoadStealFileAsync_AudioOnly_EnablesAudioAndDisablesSubtitles()
    {
        using Harness h = new();
        h.Fs.AddFile(@"C:\tools\mkvmerge.exe");
        h.Fs.AddFile(@"M:\Old\Movie.mkv");
        h.ProcessRunner.EnqueueResult(stdout: """{"tracks":[{"id":0,"type":"video"},{"id":1,"type":"audio"}]}""");
        Job job = new();

        await h.Runner.LoadStealFileAsync(job, @"M:\Old\Movie.mkv");

        await Assert.That(job.StealPath).IsEqualTo(@"M:\Old\Movie.mkv");
        await Assert.That(job.DonorHasAudio).IsTrue();
        await Assert.That(job.DonorHasSubtitles).IsFalse();
        await Assert.That(job.StealAudio).IsTrue();
        await Assert.That(job.StealSubtitles).IsFalse();

        JobRunner.ClearStealFile(job);
        await Assert.That(job.StealPath).IsEqualTo(string.Empty);
        await Assert.That(job.DonorHasAudio).IsFalse();
        await Assert.That(job.StealAudio).IsFalse();
    }

    [Test]
    public async Task LoadStealFileAsync_AudioAndSubtitles_EnablesBoth()
    {
        using Harness h = new();
        h.Fs.AddFile(@"C:\tools\mkvmerge.exe");
        h.Fs.AddFile(@"M:\Old\Movie.mkv");
        h.ProcessRunner.EnqueueResult(stdout: """{"tracks":[{"id":0,"type":"video"},{"id":1,"type":"audio"},{"id":2,"type":"subtitles"}]}""");
        Job job = new();

        await h.Runner.LoadStealFileAsync(job, @"M:\Old\Movie.mkv");

        await Assert.That(job.DonorHasAudio).IsTrue();
        await Assert.That(job.DonorHasSubtitles).IsTrue();
        await Assert.That(job.StealAudio).IsTrue();
        await Assert.That(job.StealSubtitles).IsTrue();
    }

    [Test]
    public async Task RunAsync_NoStealFile_DoesNotUncheckAudioOrSubtitles()
    {
        using Harness h = new();
        h.Fs.AddFile(@"P:\Proj\Up\MKV3D\__ENCODE_3D_LAUNCHER.cmd");

        string hook = await RunAndCaptureHookAsync(h, LoadedJob("Up"));

        await Assert.That(hook).DoesNotContain("::first_subtitle_entryn");
        await Assert.That(hook).DoesNotContain("::default_audio");
    }

    [Test]
    public async Task RunAsync_StealAudioAndSubtitles_UnchecksBothBeforeGenerateAvs()
    {
        using Harness h = new();
        h.Fs.AddFile(@"P:\Proj\Up\MKV3D\__ENCODE_3D_LAUNCHER.cmd");
        Job job = LoadedJob("Up");
        job.StealPath = @"M:\Old\Up.3d.tab.mkv";
        job.StealAudio = true;
        job.StealSubtitles = true;

        string hook = await RunAndCaptureHookAsync(h, job);

        int openAt = hook.IndexOf("OpenMKV ", StringComparison.Ordinal);
        int uncheckAudioAt = hook.IndexOf("set ::default_audio -1", StringComparison.Ordinal);
        int uncheckSubAt = hook.IndexOf("set ::default_sub -1", StringComparison.Ordinal);
        int generateAt = hook.IndexOf("GenerateAvs", StringComparison.Ordinal);
        await Assert.That(openAt).IsGreaterThanOrEqualTo(0);
        await Assert.That(uncheckAudioAt).IsGreaterThan(openAt);
        await Assert.That(uncheckSubAt).IsGreaterThan(openAt);
        await Assert.That(generateAt).IsGreaterThan(uncheckAudioAt);
        await Assert.That(generateAt).IsGreaterThan(uncheckSubAt);
        await Assert.That(hook).Contains("::first_subtitle_entryn");
        await Assert.That(h.Logger.Messages).Contains("[Steal] Stealing audio + subtitles from 'Up.3d.tab.mkv' -> unchecking those streams in BD3D2MK3D.");
    }

    [Test]
    public async Task RunAsync_ExtraQualityOff_LeavesProfileCrfAlone()
    {
        using Harness h = new();
        h.Fs.AddFile(@"P:\Proj\Up\MKV3D\__ENCODE_3D_LAUNCHER.cmd");

        string hook = await RunAndCaptureHookAsync(h, LoadedJob("Up"));

        await Assert.That(hook).Contains(ProfileCrfBlock("18"));
        await Assert.That(hook).DoesNotContain("Per-movie override");
        await Assert.That(h.Fs.ReadAllText(@"P:\Proj\Up\MKV3D\encode_profile.txt")).DoesNotContain("x264crf");
    }

    [Test]
    [Arguments(18.0, "18", "17")]
    [Arguments(20.0, "20", "19")]
    [Arguments(18.5, "18.5", "17.5")]
    public async Task RunAsync_ExtraQuality_SetsProfileCrfMinusOneAfterProfileBlock_AndRecordsIt(double profileCrf, string profileText, string expected)
    {
        using Harness h = new(profile: ProfileWithCrf((decimal)profileCrf));
        h.Fs.AddFile(@"P:\Proj\Up\MKV3D\__ENCODE_3D_LAUNCHER.cmd");
        Job job = LoadedJob("Up");
        job.UseExtraQuality = true;

        string hook = await RunAndCaptureHookAsync(h, job);

        // Tcl runs top to bottom, so the override must come after the profile's own x264crf lines.
        int profileAt = hook.IndexOf(ProfileCrfBlock(profileText), StringComparison.Ordinal);
        int overrideConfigAt = hook.IndexOf($"set ::config(x264crf) \"{expected}\"", StringComparison.Ordinal);
        int overrideLiveAt = hook.IndexOf($"set ::x264crf \"{expected}\"", StringComparison.Ordinal);
        await Assert.That(profileAt).IsGreaterThanOrEqualTo(0);
        await Assert.That(overrideConfigAt).IsGreaterThan(profileAt);
        await Assert.That(overrideLiveAt).IsGreaterThan(profileAt);
        await Assert.That(h.Fs.ReadAllText(@"P:\Proj\Up\MKV3D\encode_profile.txt")).Contains($"x264crf={expected}");
        await Assert.That(h.Logger.Messages).Contains($"[Profile] Extra quality: x264 CRF {profileText} -> {expected} for this movie.");
    }

    [Test]
    [Arguments(null, "[Profile] Extra quality ignored: the profile does not encode x264 in CRF mode.")]
    [Arguments(0.0, "[Profile] Extra quality ignored: the profile is already at CRF 0, the lowest.")]
    public async Task RunAsync_ExtraQuality_NoLowerCrf_IsIgnoredAndLogged(double? crf, string expectedLog)
    {
        using Harness h = new(profile: new FakeEncodeProfile { Crf = (decimal?)crf });
        h.Fs.AddFile(@"P:\Proj\Up\MKV3D\__ENCODE_3D_LAUNCHER.cmd");
        Job job = LoadedJob("Up");
        job.UseExtraQuality = true;

        string hook = await RunAndCaptureHookAsync(h, job);

        await Assert.That(hook).DoesNotContain("Per-movie override");
        await Assert.That(h.Fs.ReadAllText(@"P:\Proj\Up\MKV3D\encode_profile.txt")).DoesNotContain("x264crf");
        await Assert.That(h.Logger.Messages).Contains(expectedLog);
    }

    [Test]
    public async Task RunAsync_KillsHungMkvInfoOlderThan10Seconds_AndLeavesFreshOneAlone()
    {
        using Harness h = new();
        bool bd3dExited = false;
        bool hungKilled = false;
        bool hungDisposed = false;
        bool freshKilled = false;
        bool freshDisposed = false;
        h.Processes.OnStartShell = (fileName, _) => fileName == Bd3dExe
            ? new(101, hasExited: () => bd3dExited, kill: () => bd3dExited = true)
            : new(202);
        h.Processes.OnGetProcessesByName = name => name == "mkvinfo"
            ? [
                new(301, () => h.NowLocal - TimeSpan.FromSeconds(11), kill: () => hungKilled = true, dispose: () => hungDisposed = true),
                new(302, () => h.NowLocal - TimeSpan.FromSeconds(3), kill: () => freshKilled = true, dispose: () => freshDisposed = true)
            ]
            : [];

        // Runs synchronously up to the first PeriodicTimer.WaitForNextTickAsync.
        Task<bool> runTask = h.Runner.RunAsync(LoadedJob("Up"), CancellationToken.None);

        await Assert.That(hungKilled).IsTrue();
        await Assert.That(hungDisposed).IsTrue();
        await Assert.That(freshKilled).IsFalse();
        await Assert.That(freshDisposed).IsTrue();

        // Simulate BD3D2MK3D finishing before the next 2-second tick.
        h.Fs.AddFile(@"P:\Proj\Up\MKV3D\BD3D2MK3D.log", "Successfully done");
        h.Fs.AddFile(@"P:\Proj\Up\MKV3D\__ENCODE_3D_LAUNCHER.cmd", "@echo off");
        h.Time.Advance(TimeSpan.FromSeconds(2));

        bool succeeded = await runTask;

        await Assert.That(succeeded).IsTrue();
        await Assert.That(bd3dExited).IsTrue();
        await Assert.That(h.Processes.StartedShells.Count).IsEqualTo(2);
        await Assert.That(h.Processes.StartedShells[1]).IsEqualTo((@"P:\Proj\Up\MKV3D\__ENCODE_3D_LAUNCHER.cmd", @"P:\Proj\Up\MKV3D"));
        await Assert.That(h.Fs.ReadAllText(@"P:\Proj\Up\MKV3D\encode_profile.txt")).Contains($"hash={h.Profile.Hash}");
    }

    [Test]
    public async Task RunAsync_PrefersFolderFromResolvedTxtOverTitleLookup()
    {
        using Harness h = new();
        // A project folder matching the title exists, but BD3D2MK3D reports a different one.
        h.Fs.AddFile(@"P:\Proj\Up\MKV3D\__ENCODE_3D_LAUNCHER.cmd");
        bool bd3dExited = false;
        h.Processes.OnStartShell = (fileName, workingDirectory) =>
        {
            if (fileName != Bd3dExe)
            {
                return new(202);
            }
            h.Fs.AddFile(Path.Combine(workingDirectory!, "resolved.txt"), "P:\\Proj\\Up_2009\\MKV3D\r\n");
            h.Fs.AddFile(@"P:\Proj\Up_2009\MKV3D\BD3D2MK3D.log", "... Successfully done ...");
            h.Fs.AddFile(@"P:\Proj\Up_2009\MKV3D\__ENCODE_3D_LAUNCHER.cmd");
            return new(101, hasExited: () => bd3dExited, kill: () => bd3dExited = true);
        };

        bool succeeded = await h.Runner.RunAsync(LoadedJob("Up"), CancellationToken.None);

        await Assert.That(succeeded).IsTrue();
        await Assert.That(bd3dExited).IsTrue();
        await Assert.That(h.Processes.StartedShells[1].FileName).IsEqualTo(@"P:\Proj\Up_2009\MKV3D\__ENCODE_3D_LAUNCHER.cmd");
    }

    [Test]
    public async Task RunAsync_FindsSanitizedTitleFolder_AndFallsBackToEncode3dCmd()
    {
        using Harness h = new();
        h.Fs.AddFile(@"P:\Proj\Tron Legacy\MKV3D\__ENCODE_3D.cmd");
        h.Processes.OnStartShell = static (_, _) => new(101); // exits immediately

        bool succeeded = await h.Runner.RunAsync(LoadedJob("Tron: Legacy"), CancellationToken.None);

        await Assert.That(succeeded).IsTrue();
        await Assert.That(h.Processes.StartedShells[1]).IsEqualTo((@"P:\Proj\Tron Legacy\MKV3D\__ENCODE_3D.cmd", @"P:\Proj\Tron Legacy\MKV3D"));
    }

    [Test]
    public async Task RunAsync_ShortTitle_NeverMatchesAnotherMoviesProjectFolder()
    {
        using Harness h = new();
        h.Fs.AddFile(@"P:\Proj\Up 2\MKV3D\__ENCODE_3D_LAUNCHER.cmd");
        h.Fs.AddFile(@"P:\Proj\Upside Down\MKV3D\__ENCODE_3D_LAUNCHER.cmd");
        h.Processes.OnStartShell = static (_, _) => new(101); // exits immediately

        bool succeeded = await h.Runner.RunAsync(LoadedJob("Up"), CancellationToken.None);

        await Assert.That(succeeded).IsFalse();
        await Assert.That(h.Processes.StartedShells.Count).IsEqualTo(1);
        await Assert.That(h.Logger.Messages).Contains("[Error] Could not find generated project folder for 'Up'. Aborting pipeline.");
    }

    [Test]
    public async Task RunAsync_NoLauncherInProjectFolder_ReturnsFalse()
    {
        using Harness h = new();
        h.Fs.AddDirectory(@"P:\Proj\Up\MKV3D");
        h.Processes.OnStartShell = static (_, _) => new(101); // exits immediately

        bool succeeded = await h.Runner.RunAsync(LoadedJob("Up"), CancellationToken.None);

        await Assert.That(succeeded).IsFalse();
        await Assert.That(h.Processes.StartedShells.Count).IsEqualTo(1);
        await Assert.That(h.Logger.Messages).Contains(@"[Error] Batch launcher not found in P:\Proj\Up\MKV3D");
    }

    [Test]
    public async Task RunAsync_Cancelled_KillsBd3dDeletesSandboxAndThrows()
    {
        using Harness h = new();
        using CancellationTokenSource cts = new();
        bool bd3dExited = false;
        string? sandbox = null;
        h.Processes.OnStartShell = (_, workingDirectory) =>
        {
            sandbox = workingDirectory;
            return new(101, hasExited: () => bd3dExited, kill: () => bd3dExited = true);
        };

        Task<bool> runTask = h.Runner.RunAsync(LoadedJob("Up"), cts.Token);
        cts.Cancel();
        OperationCanceledException? caught = null;
        try
        {
            await runTask;
        }
        catch (OperationCanceledException ex)
        {
            caught = ex;
        }

        await Assert.That(caught).IsNotNull();
        await Assert.That(bd3dExited).IsTrue();
        await Assert.That(h.Fs.DirectoryExists(sandbox!)).IsFalse();
        await Assert.That(h.Processes.StartedShells.Count).IsEqualTo(1);
    }

    // ---- CleanupStaleTempDirectories ----

    [Test]
    public async Task CleanupStaleTempDirectories_DeletesOnlyFoldersOlderThan12Hours()
    {
        using Harness h = new();
        string baseTemp = Path.Combine(Path.GetTempPath(), "BD3DOrchestrator");
        string staleDir = Path.Combine(baseTemp, "Job_stale");
        string freshDir = Path.Combine(baseTemp, "Job_fresh");
        h.Fs.AddDirectory(staleDir, h.NowLocal - TimeSpan.FromHours(13));
        h.Fs.AddFile(Path.Combine(staleDir, "plugin.tcl"), "hook");
        h.Fs.AddDirectory(freshDir, h.NowLocal - TimeSpan.FromHours(2));

        h.Runner.CleanupStaleTempDirectories();

        await Assert.That(h.Fs.DirectoryExists(staleDir)).IsFalse();
        await Assert.That(h.Fs.DirectoryExists(freshDir)).IsTrue();
    }
}
