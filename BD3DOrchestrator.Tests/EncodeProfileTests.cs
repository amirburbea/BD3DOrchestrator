using System.Linq;
using System.Threading.Tasks;
using BD3DOrchestrator.Tests.Fakes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace BD3DOrchestrator.Tests;

public sealed class EncodeProfileTests
{
    [Test]
    public async Task Constructor_ParsesTemplatesAndSettingsAndIgnoresComments()
    {
        FakeFileSystem fs = new();
        fs.AddFile(@"C:\app\profile.json", """
            {
              // comment
              "OutputNameTemplate": "{title}.3D.mkv",
              "MkvTitleTemplate": "{title} (3D)",
              "Settings": {
                "crf": 18,
                "enable_flag": true,
                "disable_flag": false,
                "preset": "slow",
              }
            }
            """);
        AppSettings settings = new() { ProfilePath = @"C:\app\profile.json" };

        EncodeProfile profile = new(settings, fs);

        await Assert.That(profile.OutputNameTemplate).IsEqualTo("{title}.3D.mkv");
        await Assert.That(profile.MkvTitleTemplate).IsEqualTo("{title} (3D)");
        await Assert.That(profile.Settings.Count).IsEqualTo(4);
        await Assert.That(profile.Settings.ElementAt(0).Value).IsEqualTo("18");
        await Assert.That(profile.Settings.ElementAt(1).Value).IsEqualTo("1");
        await Assert.That(profile.Settings.ElementAt(2).Value).IsEqualTo("0");
        await Assert.That(profile.Settings.ElementAt(3).Value).IsEqualTo("slow");
    }

    [Test]
    public async Task Hash_IgnoresCommentsAndWhitespace()
    {
        FakeFileSystem fs1 = new();
        fs1.AddFile(@"C:\app\profile.json", """{"OutputNameTemplate":"{title}.mkv","Settings":{"crf":18}}""");
        FakeFileSystem fs2 = new();
        fs2.AddFile(@"C:\app\profile.json", """
            {
              // extra comment
              "OutputNameTemplate": "{title}.mkv",
              "Settings": { "crf": 18 }
            }
            """);
        AppSettings settings = new() { ProfilePath = @"C:\app\profile.json" };

        EncodeProfile p1 = new(settings, fs1);
        EncodeProfile p2 = new(settings, fs2);

        await Assert.That(p1.Hash).IsEqualTo(p2.Hash);
    }

    [Test]
    public async Task Hash_IgnoresTheOrderOfSettings()
    {
        FakeFileSystem fs1 = new();
        fs1.AddFile(@"C:\app\profile.json", """{"Settings":{"crf":18,"preset":"slow","tune":"film"}}""");
        FakeFileSystem fs2 = new();
        fs2.AddFile(@"C:\app\profile.json", """{"Settings":{"tune":"film","crf":18,"preset":"slow"}}""");
        AppSettings settings = new() { ProfilePath = @"C:\app\profile.json" };

        EncodeProfile p1 = new(settings, fs1);
        EncodeProfile p2 = new(settings, fs2);

        await Assert.That(p1.Hash).IsEqualTo(p2.Hash);
    }

    [Test]
    public async Task Settings_DuplicateName_KeepsLastValueOnce()
    {
        FakeFileSystem fs = new();
        fs.AddFile(@"C:\app\profile.json", """{"Settings":{"x264crf":18,"preset":"slow","x264crf":20}}""");

        EncodeProfile profile = new(new() { ProfilePath = @"C:\app\profile.json" }, fs);

        await Assert.That(profile.Settings.Count).IsEqualTo(2);
        await Assert.That(profile.Settings["x264crf"]).IsEqualTo("20");
        await Assert.That(profile.TclBlock).IsEqualTo("set ::config(x264crf) \"20\"\nset ::x264crf \"20\"\nset ::config(preset) \"slow\"\nset ::preset \"slow\"\n");
    }

    [Test]
    [Arguments("""{"OutputNameTemplate":"{title}.mkv","Settings":{"crf":19}}""")]
    [Arguments("""{"OutputNameTemplate":"{title}.3d.mkv","Settings":{"crf":18}}""")]
    [Arguments("""{"OutputNameTemplate":"{title}.mkv","MkvTitleTemplate":"{title} 3D","Settings":{"crf":18}}""")]
    [Arguments("""{"OutputNameTemplate":"{title}.mkv","Settings":{"crf":18,"preset":"slow"}}""")]
    public async Task Hash_ChangesWhenAnythingAffectingOutputChanges(string changedProfile)
    {
        FakeFileSystem baseline = new();
        baseline.AddFile(@"C:\app\profile.json", """{"OutputNameTemplate":"{title}.mkv","Settings":{"crf":18}}""");
        FakeFileSystem changed = new();
        changed.AddFile(@"C:\app\profile.json", changedProfile);
        AppSettings settings = new() { ProfilePath = @"C:\app\profile.json" };

        EncodeProfile p1 = new(settings, baseline);
        EncodeProfile p2 = new(settings, changed);

        await Assert.That(p1.Hash).IsNotEqualTo(p2.Hash);
        await Assert.That(p1.Hash.Length).IsEqualTo(12);
    }

    [Test]
    [Arguments("""{"Settings":{"x264mode":"CRF","x264crf":18}}""", "18", "17")]
    [Arguments("""{"Settings":{"x264mode":"crf","x264crf":18.5,"use_x265":0}}""", "18.5", "17.5")]
    [Arguments("""{"Settings":{"x264mode":"CRF","x264crf":0.5}}""", "0.5", "0")]
    public async Task Crf_IsReadFromProfile_AndExtraQualityIsOneLower(string json, string expectedCrf, string expectedExtra)
    {
        FakeFileSystem fs = new();
        fs.AddFile(@"C:\app\profile.json", json);
        IEncodeProfile profile = new EncodeProfile(new() { ProfilePath = @"C:\app\profile.json" }, fs);

        await Assert.That(EncodeProfile.FormatCrf(profile.Crf!.Value)).IsEqualTo(expectedCrf);
        await Assert.That(EncodeProfile.FormatCrf(profile.ExtraQualityCrf!.Value)).IsEqualTo(expectedExtra);
    }

    [Test]
    [Arguments("""{"Settings":{"x264crf":18}}""")]                                        // mode not set
    [Arguments("""{"Settings":{"x264mode":"2-pass","x264crf":18}}""")]                    // not CRF mode
    [Arguments("""{"Settings":{"x264mode":"CRF","x264crf":18,"use_x265":1}}""")]          // x265, not x264
    [Arguments("""{"Settings":{"x264mode":"CRF"}}""")]                                    // no CRF value
    public async Task Crf_IsNull_WhenProfileDoesNotEncodeX264InCrfMode(string json)
    {
        FakeFileSystem fs = new();
        fs.AddFile(@"C:\app\profile.json", json);
        IEncodeProfile profile = new EncodeProfile(new() { ProfilePath = @"C:\app\profile.json" }, fs);

        await Assert.That(profile.Crf).IsNull();
        await Assert.That(profile.ExtraQualityCrf).IsNull();
    }

    [Test]
    public async Task Crf_OfTheShippedProfile_Is18()
    {
        FakeFileSystem fs = new();
        string shipped = System.IO.File.ReadAllText(System.IO.Path.Combine(System.AppContext.BaseDirectory, "profile.json"));
        fs.AddFile(@"C:\app\profile.json", shipped);
        IEncodeProfile profile = new EncodeProfile(new() { ProfilePath = @"C:\app\profile.json" }, fs);

        await Assert.That(profile.Crf).IsEqualTo(18m);
        await Assert.That(profile.ExtraQualityCrf).IsEqualTo(17m);
    }

    [Test]
    public async Task TclBlock_EscapesSpecialTclCharacters()
    {
        FakeFileSystem fs = new();
        fs.AddFile(@"C:\app\profile.json", """{"Settings":{"arg":"a$b[c]\"\\"}}""");
        EncodeProfile profile = new(new() { ProfilePath = @"C:\app\profile.json" }, fs);

        await Assert.That(profile.TclBlock).Contains("""set ::config(arg) "a\$b\[c\]\"\\""");
        await Assert.That(profile.TclBlock).Contains("""set ::arg "a\$b\[c\]\"\\""");
    }
}
