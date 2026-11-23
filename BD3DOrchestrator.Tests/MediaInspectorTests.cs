using System.Threading.Tasks;
using BD3DOrchestrator.Tests.Fakes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace BD3DOrchestrator.Tests;

public sealed class MediaInspectorTests
{
    [Test]
    public async Task InspectAsync_FolderInput_PrefersMvcMkvAndFindsCompanionsAcrossFolders()
    {
        FakeFileSystem fs = new();
        fs.AddFile(@"C:\tools\mkvmerge.exe");
        fs.AddFile(@"M:\Movies\Avatar (2009)\Avatar (2009).mkv");
        fs.AddFile(@"M:\Movies\Avatar (2009)\Avatar (2009).3d.mvc.mkv");
        fs.AddFile(@"M:\Movies\Avatar (2009)\Avatar.eng.srt");
        fs.AddFile(@"M:\Movies\Avatar.spa.sup");
        fs.AddFile(@"M:\Movies\Avatar (2009)\Avatar\Avatar.Commentary.mka");
        fs.AddFile(@"M:\Movies\Avatar (2009)\Avatar (2009)\Avatar.ec3");

        FakeProcessRunner processRunner = new FakeProcessRunner().EnqueueResult(
            stdout: """{"tracks":[{"type":"audio","codec":"DTS-HD Master Audio"},{"type":"audio","codec":"E-AC-3"}]}"""
        );
        AppSettings settings = new() { ToolsetDirectory = @"C:\tools" };
        MediaInspector inspector = new(settings, fs, processRunner);

        MediaInspectionResult result = await inspector.InspectAsync(@"M:\Movies\Avatar (2009)");

        await Assert.That(result.MkvPath).IsEqualTo(@"M:\Movies\Avatar (2009)\Avatar (2009).3d.mvc.mkv");
        await Assert.That(result.MovieTitle).IsEqualTo("Avatar");
        await Assert.That(result.HasDtsAudio).IsTrue();
        await Assert.That(result.HasDolbyAudio).IsTrue();
        await Assert.That(result.CompanionFiles.Count).IsEqualTo(4);
    }

    [Test]
    public async Task InspectAsync_MissingMkvmerge_SkipsProcessAndStillReturnsCompanions()
    {
        FakeFileSystem fs = new();
        fs.AddDirectory(@"C:\tools");
        fs.AddFile(@"M:\Movies\Up (2009).3d.mvc.mkv");
        fs.AddFile(@"M:\Movies\Up.eng.srt");
        FakeProcessRunner processRunner = new();
        MediaInspector inspector = new(new() { ToolsetDirectory = @"C:\tools" }, fs, processRunner);

        MediaInspectionResult result = await inspector.InspectAsync(@"M:\Movies\Up (2009).3d.mvc.mkv");

        await Assert.That(result.MovieTitle).IsEqualTo("Up");
        await Assert.That(result.CompanionFiles).Contains(@"M:\Movies\Up.eng.srt");
        await Assert.That(processRunner.Calls.Count).IsEqualTo(0);
    }

    [Test]
    public async Task InspectAsync_RecordsEmbeddedSrtTrackIdsAndIgnoresPgs()
    {
        FakeFileSystem fs = new();
        fs.AddFile(@"C:\tools\mkvmerge.exe");
        fs.AddFile(@"M:\Movies\Movie.3d.mvc.mkv");

        FakeProcessRunner processRunner = new FakeProcessRunner().EnqueueResult(
            stdout: """{"tracks":[{"id":0,"type":"video","codec":"MPEG-4p10"},{"id":1,"type":"audio","codec":"AC-3"},{"id":2,"type":"subtitles","codec":"HDMV PGS","properties":{"codec_id":"S_HDMV/PGS"}},{"id":3,"type":"subtitles","codec":"SubRip/SRT","properties":{"codec_id":"S_TEXT/UTF8"}},{"id":4,"type":"subtitles","codec":"SubRip/SRT","properties":{"codec_id":"S_TEXT/UTF8"}}]}"""
        );
        MediaInspector inspector = new(new() { ToolsetDirectory = @"C:\tools" }, fs, processRunner);

        MediaInspectionResult result = await inspector.InspectAsync(@"M:\Movies\Movie.3d.mvc.mkv");

        await Assert.That(result.EmbeddedSrtTrackIds).IsEquivalentTo([3, 4]);
    }
}
