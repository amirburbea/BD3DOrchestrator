using System;
using System.Text.Json;
using System.Threading.Tasks;
using BD3DOrchestrator.Tests.Fakes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace BD3DOrchestrator.Tests;

public sealed class OrchestratorPipelineTests
{
    private const string ProjectDir = @"P:\Proj\MKV3D";
    private const string MuxJson = @"P:\Proj\MKV3D\__MUX_3D_OPTIONS.json";

    private static OrchestratorPipeline CreatePipeline(FakeFileSystem fs, FakeProcessRunner? runner = null, FakeEncodeProfile? profile = null) =>
        new(
            new() { FfmpegExecutable = @"C:\tools\ffmpeg.exe" },
            profile ?? new FakeEncodeProfile { OutputNameTemplate = "{title}.3d.tab.mkv", MkvTitleTemplate = "{title} (3D)" },
            new FakeMessageLogger(),
            fs,
            runner ?? new FakeProcessRunner()
        );

    /// <summary>The mux arguments joined with '|', so a whole argument list can be compared in order.</summary>
    private static string ReadArgs(FakeFileSystem fs, string path) =>
        string.Join("|", JsonSerializer.Deserialize<string[]>(fs.ReadAllText(path))!);

    private static int CountOccurrences(string text, string value)
    {
        int count = 0;
        for (int i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + value.Length, StringComparison.Ordinal))
        {
            count++;
        }
        return count;
    }

    // ---- ApplyOutputNaming ----

    [Test]
    public async Task ApplyOutputNaming_UpdatesStandardAndCustomMuxJson()
    {
        FakeFileSystem fs = new();
        fs.AddFile(MuxJson, """["--output","D:\\Out\\Orig.mkv","--title","Orig"]""");
        fs.AddFile(@"P:\Proj\MKV3D\_CUSTOM_MUX_3D_OPTIONS.json", """["--output","D:\\Out\\Orig.mkv","--title","Orig"]""");
        OrchestratorPipeline pipeline = CreatePipeline(fs);

        pipeline.ApplyOutputNaming(ProjectDir, "Tron: Legacy");

        await Assert.That(ReadArgs(fs, MuxJson)).IsEqualTo(@"--output|D:\Out\Tron Legacy.3d.tab.mkv|--title|Tron: Legacy (3D)");
        await Assert.That(ReadArgs(fs, @"P:\Proj\MKV3D\_CUSTOM_MUX_3D_OPTIONS.json")).IsEqualTo(@"--output|D:\Out\Tron Legacy.3d.tab.custom.mkv|--title|Tron: Legacy (3D)");
    }

    // ---- FindDemuxedDtsFile ----

    [Test]
    public async Task FindDemuxedDtsFile_PrefersFileReferencedByMuxJson()
    {
        FakeFileSystem fs = new();
        fs.AddFile(MuxJson, """["--output","x.mkv","track2.dts"]""");
        fs.AddFile(@"P:\Proj\MKV3D\track2.dts", length: 100);
        fs.AddFile(@"P:\Proj\MKV3D\bigger.dtshd", length: 50_000);
        OrchestratorPipeline pipeline = CreatePipeline(fs);

        string? found = pipeline.FindDemuxedDtsFile(ProjectDir);

        await Assert.That(found).IsEqualTo(@"P:\Proj\MKV3D\track2.dts");
    }

    [Test]
    public async Task FindDemuxedDtsFile_FallsBackToLargestDtsFileInFolder()
    {
        FakeFileSystem fs = new();
        fs.AddFile(@"P:\Proj\MKV3D\small.dts", length: 100);
        fs.AddFile(@"P:\Proj\MKV3D\main.dtshd", length: 50_000);
        OrchestratorPipeline pipeline = CreatePipeline(fs);

        string? found = pipeline.FindDemuxedDtsFile(ProjectDir);

        await Assert.That(found).IsEqualTo(@"P:\Proj\MKV3D\main.dtshd");
    }

    // ---- InjectAvsTweak ----

    [Test]
    public async Task InjectAvsTweak_CreatesBackupAndIsIdempotent()
    {
        const string original = "LoadPlugin(\"x.dll\")\r\nReturn(last)\r\n";
        FakeFileSystem fs = new();
        fs.AddFile(@"P:\Proj\MKV3D\__ENCODE_3D_MOVIE.avs", original);
        OrchestratorPipeline pipeline = CreatePipeline(fs);

        bool first = pipeline.InjectAvsTweak(ProjectDir);
        bool second = pipeline.InjectAvsTweak(ProjectDir);

        await Assert.That(first).IsTrue();
        await Assert.That(second).IsTrue();
        await Assert.That(fs.ReadAllText(@"P:\Proj\MKV3D\__ENCODE_3D_MOVIE.avs.bak")).IsEqualTo(original);
        string avs = fs.ReadAllText(@"P:\Proj\MKV3D\__ENCODE_3D_MOVIE.avs");
        await Assert.That(avs).IsEqualTo("LoadPlugin(\"x.dll\")\r\nTweak(sat=1.1, cont=1.05)\r\nReturn(last)\r\n");
        await Assert.That(CountOccurrences(avs, "Tweak(")).IsEqualTo(1);
    }

    [Test]
    public async Task InjectAvsTweak_MissingAvs_ReturnsFalse()
    {
        FakeFileSystem fs = new();
        fs.AddDirectory(ProjectDir);
        OrchestratorPipeline pipeline = CreatePipeline(fs);

        await Assert.That(pipeline.InjectAvsTweak(ProjectDir)).IsFalse();
    }

    // ---- NeutralizePostProcessCmd ----

    [Test]
    public async Task NeutralizePostProcessCmd_RewritesExistingScriptsOnly()
    {
        FakeFileSystem fs = new();
        fs.AddFile(@"P:\Proj\MKV3D\__POSTPROCESS_3D.cmd", "start \"\" .\r\npause\r\n");
        fs.AddFile(@"P:\Proj\MKV3D\_POSTPROCESS_2D.cmd", "pause\r\n");
        OrchestratorPipeline pipeline = CreatePipeline(fs);

        pipeline.NeutralizePostProcessCmd(ProjectDir);

        const string neutralized = "@echo off\r\nREM Neutralized Explorer popup and pause\r\nexit /B 0\r\n";
        await Assert.That(fs.ReadAllText(@"P:\Proj\MKV3D\__POSTPROCESS_3D.cmd")).IsEqualTo(neutralized);
        await Assert.That(fs.ReadAllText(@"P:\Proj\MKV3D\_POSTPROCESS_2D.cmd")).IsEqualTo(neutralized);
        await Assert.That(fs.FileExists(@"P:\Proj\MKV3D\_CUSTOM_POSTPROCESS_3D.cmd")).IsFalse();
    }

    // ---- UpdateMuxJson ----

    [Test]
    public async Task UpdateMuxJson_StripsDtsWithItsSwitches_InjectsEac3AndForcedSpanishSubtitle()
    {
        FakeFileSystem fs = new();
        fs.AddFile(MuxJson, """
            [
              "--output", "D:\\Out\\Movie.mkv",
              "--language", "0:eng", "--default-track", "0:yes", "track2.dtshd"
            ]
            """);
        fs.AddFile(@"P:\Proj\MKV3D\converted.ec3");
        fs.AddFile(@"M:\Movie.spa.forced.srt", "1\n00:00:01,000 --> 00:00:02,000\nHola");
        OrchestratorPipeline pipeline = CreatePipeline(fs);

        bool updated = pipeline.UpdateMuxJson(ProjectDir, @"P:\Proj\MKV3D\converted.ec3", [@"M:\Movie.spa.forced.srt"]);

        await Assert.That(updated).IsTrue();
        await Assert.That(ReadArgs(fs, MuxJson)).IsEqualTo(string.Join("|",
            "--output", @"D:\Out\Movie.mkv",
            "--track-name", "0:English (E-AC-3 5.1 640k)", "--language", "0:eng", "--default-track", "0:yes", "converted.ec3",
            "--language", "0:spa", "--forced-track", "0:yes", "--default-track", "0:yes", "--track-name", "0:SPA (Forced)", "Movie.spa.forced.srt"));
        await Assert.That(fs.FileExists(@"P:\Proj\MKV3D\Movie.spa.forced.srt")).IsTrue();
    }

    [Test]
    public async Task UpdateMuxJson_NoAudioTrack_RecoversOrphanedDemuxedAudio()
    {
        FakeFileSystem fs = new();
        fs.AddFile(MuxJson, """["--output","x.mkv","video.h264"]""");
        fs.AddFile(@"P:\Proj\MKV3D\video.h264");
        fs.AddFile(@"P:\Proj\MKV3D\audio.ac3");
        OrchestratorPipeline pipeline = CreatePipeline(fs);

        pipeline.UpdateMuxJson(ProjectDir, null, []);

        await Assert.That(ReadArgs(fs, MuxJson)).IsEqualTo(
            "--output|x.mkv|video.h264|--track-name|0:English (AC3)|--language|0:eng|--default-track|0:yes|audio.ac3");
    }

    [Test]
    public async Task UpdateMuxJson_ExistingAudio_CompanionAudioIsNotDefault_AndFrenchSubtitleTagged()
    {
        FakeFileSystem fs = new();
        fs.AddFile(MuxJson, """["--output","x.mkv","track1.ac3"]""");
        fs.AddFile(@"M:\Movie.Commentary.ec3");
        fs.AddFile(@"M:\Movie.fre.srt");
        OrchestratorPipeline pipeline = CreatePipeline(fs);

        pipeline.UpdateMuxJson(ProjectDir, null, [@"M:\Movie.Commentary.ec3", @"M:\Movie.fre.srt", @"M:\Missing.eng.srt"]);

        await Assert.That(ReadArgs(fs, MuxJson)).IsEqualTo(string.Join("|",
            "--output", "x.mkv", "track1.ac3",
            "--track-name", "0:English (EC3)", "--language", "0:eng", "--default-track", "0:no", "Movie.Commentary.ec3",
            "--language", "0:fre", "Movie.fre.srt"));
    }

    // ---- ConvertAudioToEac3Async ----

    [Test]
    [Arguments("dts,DTS-HD MA,8", "-af pan=5.1|FL=FL|FR=FR|FC=1.1*FC|LFE=LFE|BL=0.707*SL+0.707*BL|BR=0.707*SR+0.707*BR -c:a eac3 -b:a 640k")]
    [Arguments("dts,DTS-ES,7", "-af pan=5.1|FL=FL|FR=FR|FC=1.1*FC|LFE=LFE|BL=0.707*SL+0.707*BC|BR=0.707*SR+0.707*BC -c:a eac3 -b:a 640k")]
    [Arguments("dts,DTS,6", "-c:a eac3 -b:a 640k -ac 6")]
    public async Task ConvertAudioToEac3Async_ChoosesDownmixByChannelCount(string probeOutput, string expectedAudioArgs)
    {
        FakeFileSystem fs = new();
        fs.AddFile(@"C:\tools\ffprobe.exe");
        FakeProcessRunner runner = new FakeProcessRunner()
            .EnqueueResult(exitCode: 0, stdout: probeOutput)
            .EnqueueHandler(_ =>
            {
                fs.AddFile(@"P:\Proj\MKV3D\out.ec3");
                return (0, "", "");
            });
        OrchestratorPipeline pipeline = CreatePipeline(fs, runner);

        string? result = await pipeline.ConvertAudioToEac3Async(@"P:\Proj\MKV3D\main.dtshd", @"P:\Proj\MKV3D\out.ec3");

        await Assert.That(result).IsEqualTo(@"P:\Proj\MKV3D\out.ec3");
        await Assert.That(runner.Calls.Count).IsEqualTo(2);
        await Assert.That(runner.Calls[0].Executable).IsEqualTo(@"C:\tools\ffprobe.exe");
        await Assert.That(runner.Calls[1].Executable).IsEqualTo(@"C:\tools\ffmpeg.exe");
        await Assert.That(runner.Calls[1].Arguments).IsEqualTo(
            $"-y -i \"P:\\Proj\\MKV3D\\main.dtshd\" -map 0:a:0 {expectedAudioArgs} \"P:\\Proj\\MKV3D\\out.ec3\"");
    }

    [Test]
    public async Task ConvertAudioToEac3Async_ProbeFails_UsesFfprobeOnPathAndDefaults51()
    {
        FakeFileSystem fs = new();
        FakeProcessRunner runner = new FakeProcessRunner()
            .EnqueueResult(exitCode: 1)
            .EnqueueHandler(_ =>
            {
                fs.AddFile(@"P:\Proj\MKV3D\out.ec3");
                return (0, "", "");
            });
        OrchestratorPipeline pipeline = CreatePipeline(fs, runner);

        string? result = await pipeline.ConvertAudioToEac3Async(@"P:\Proj\MKV3D\main.dts", @"P:\Proj\MKV3D\out.ec3");

        await Assert.That(result).IsEqualTo(@"P:\Proj\MKV3D\out.ec3");
        await Assert.That(runner.Calls[0].Executable).IsEqualTo("ffprobe.exe");
        await Assert.That(runner.Calls[1].Arguments).Contains("-c:a eac3 -b:a 640k -ac 6");
    }

    [Test]
    public async Task ConvertAudioToEac3Async_FfmpegFails_ReturnsNull()
    {
        FakeFileSystem fs = new();
        FakeProcessRunner runner = new FakeProcessRunner()
            .EnqueueResult(exitCode: 0, stdout: "dts,DTS,6")
            .EnqueueResult(exitCode: 1, stderr: "Invalid data found");
        OrchestratorPipeline pipeline = CreatePipeline(fs, runner);

        string? result = await pipeline.ConvertAudioToEac3Async(@"P:\Proj\MKV3D\main.dts", @"P:\Proj\MKV3D\out.ec3");

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task ExtractTracksAsync_MissingDonor_ReturnsNullWithoutRunningMkvmerge()
    {
        FakeFileSystem fs = new();
        FakeProcessRunner runner = new();
        OrchestratorPipeline pipeline = CreatePipeline(fs, runner);

        string? result = await pipeline.ExtractTracksAsync(@"M:\Old\Missing.mkv", @"P:\Proj\MKV3D\stolen_tracks.mks", includeAudio: true, includeSubtitles: true);

        await Assert.That(result).IsNull();
        await Assert.That(runner.Calls.Count).IsEqualTo(0);
    }

    [Test]
    public async Task ExtractTracksAsync_BothAudioAndSubtitles_RunsSingleMkvmergeCommand()
    {
        FakeFileSystem fs = new();
        fs.AddFile(@"M:\Old\Movie.mkv");
        FakeProcessRunner runner = new FakeProcessRunner()
            .EnqueueHandler(_ =>
            {
                fs.AddFile(@"P:\Proj\MKV3D\stolen_tracks.mks");
                return (0, "", "");
            });
        OrchestratorPipeline pipeline = CreatePipeline(fs, runner);

        string? result = await pipeline.ExtractTracksAsync(@"M:\Old\Movie.mkv", @"P:\Proj\MKV3D\stolen_tracks.mks", includeAudio: true, includeSubtitles: true);

        await Assert.That(result).IsEqualTo(@"P:\Proj\MKV3D\stolen_tracks.mks");
        await Assert.That(runner.Calls.Count).IsEqualTo(1);
        await Assert.That(runner.Calls[0].Executable).IsEqualTo("mkvmerge.exe");
        await Assert.That(runner.Calls[0].Arguments).DoesNotContain("--no-audio");
        await Assert.That(runner.Calls[0].Arguments).DoesNotContain("--no-subtitles");
        await Assert.That(runner.Calls[0].Arguments).Contains(@"--no-video --no-chapters --no-attachments --no-global-tags --no-track-tags -o ""P:\Proj\MKV3D\stolen_tracks.mks"" ""M:\Old\Movie.mkv""");
    }

    [Test]
    public async Task ExtractTracksAsync_AudioOnly_ExcludesSubtitles()
    {
        FakeFileSystem fs = new();
        fs.AddFile(@"M:\Old\Movie.mkv");
        FakeProcessRunner runner = new FakeProcessRunner()
            .EnqueueHandler(_ =>
            {
                fs.AddFile(@"P:\Proj\MKV3D\stolen_tracks.mks");
                return (0, "", "");
            });
        OrchestratorPipeline pipeline = CreatePipeline(fs, runner);

        string? result = await pipeline.ExtractTracksAsync(@"M:\Old\Movie.mkv", @"P:\Proj\MKV3D\stolen_tracks.mks", includeAudio: true, includeSubtitles: false);

        await Assert.That(result).IsEqualTo(@"P:\Proj\MKV3D\stolen_tracks.mks");
        await Assert.That(runner.Calls[0].Arguments).Contains("--no-subtitles");
        await Assert.That(runner.Calls[0].Arguments).DoesNotContain("--no-audio");
    }

    [Test]
    public async Task ExtractTracksAsync_WithTrackIds_PassesSubtitleTracksFilter()
    {
        FakeFileSystem fs = new();
        fs.AddFile(@"M:\Movies\Source.mkv");
        FakeProcessRunner runner = new FakeProcessRunner()
            .EnqueueHandler(_ =>
            {
                fs.AddFile(@"P:\Proj\MKV3D\embedded_srt.mks");
                return (0, "", "");
            });
        OrchestratorPipeline pipeline = CreatePipeline(fs, runner);

        string? result = await pipeline.ExtractTracksAsync(@"M:\Movies\Source.mkv", @"P:\Proj\MKV3D\embedded_srt.mks", includeAudio: false, includeSubtitles: true, [3, 5]);

        await Assert.That(result).IsEqualTo(@"P:\Proj\MKV3D\embedded_srt.mks");
        await Assert.That(runner.Calls[0].Arguments).Contains("--no-audio");
        await Assert.That(runner.Calls[0].Arguments).Contains("--subtitle-tracks 3,5");
    }

    [Test]
    public async Task ExtractTracksAsync_LogsProgressMilestonesFromStdout()
    {
        FakeFileSystem fs = new();
        fs.AddFile(@"M:\Old\Movie.mkv");
        FakeMessageLogger logger = new();
        FakeProcessRunner runner = new FakeProcessRunner()
            .EnqueueHandler(_ =>
            {
                fs.AddFile(@"P:\Proj\MKV3D\stolen_tracks.mks");
                return (0, "Progress: 0%\nProgress: 5%\nProgress: 10%\nProgress: 15%\nProgress: 50%\nProgress: 100%\n", "");
            });
        OrchestratorPipeline pipeline = new(
            new() { FfmpegExecutable = @"C:\tools\ffmpeg.exe" },
            new FakeEncodeProfile { OutputNameTemplate = "{title}.3d.tab.mkv", MkvTitleTemplate = "{title} (3D)" },
            logger,
            fs,
            runner);

        await pipeline.ExtractTracksAsync(@"M:\Old\Movie.mkv", @"P:\Proj\MKV3D\stolen_tracks.mks", includeAudio: true, includeSubtitles: true);

        await Assert.That(logger.Messages).Contains("[Steal] Progress: 10%");
        await Assert.That(logger.Messages).Contains("[Steal] Progress: 50%");
        await Assert.That(logger.Messages).Contains("[Steal] Progress: 100%");
        await Assert.That(logger.Messages).DoesNotContain("[Steal] Progress: 5%");
        await Assert.That(logger.Messages).DoesNotContain("[Steal] Progress: 15%");
    }

    [Test]
    public async Task UpdateMuxJson_WithStolenTracksMks_AppendsFileWithoutLanguageOverride()
    {
        FakeFileSystem fs = new();
        fs.AddFile(@"P:\Proj\MKV3D\__MUX_3D_OPTIONS.json", """["--output", "out.mkv", "video.264"]""");
        fs.AddFile(@"P:\Proj\MKV3D\stolen_tracks.mks");
        OrchestratorPipeline pipeline = CreatePipeline(fs, new FakeProcessRunner());

        bool updated = pipeline.UpdateMuxJson(@"P:\Proj\MKV3D", customEac3File: null, companionFiles: [], extraMksFiles: [@"P:\Proj\MKV3D\stolen_tracks.mks"], stolenHasAudio: true);

        await Assert.That(updated).IsTrue();
        string json = fs.ReadAllText(@"P:\Proj\MKV3D\__MUX_3D_OPTIONS.json");
        await Assert.That(json).Contains("stolen_tracks.mks");
        await Assert.That(json).DoesNotContain("--language");
    }
}
