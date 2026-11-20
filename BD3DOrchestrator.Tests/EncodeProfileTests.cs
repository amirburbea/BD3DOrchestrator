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
        await Assert.That(profile.Settings[0].Value).IsEqualTo("18");
        await Assert.That(profile.Settings[1].Value).IsEqualTo("1");
        await Assert.That(profile.Settings[2].Value).IsEqualTo("0");
        await Assert.That(profile.Settings[3].Value).IsEqualTo("slow");
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
    public async Task TclBlock_EscapesSpecialTclCharacters()
    {
        FakeFileSystem fs = new();
        fs.AddFile(@"C:\app\profile.json", """{"Settings":{"arg":"a$b[c]\"\\"}}""");
        EncodeProfile profile = new(new() { ProfilePath = @"C:\app\profile.json" }, fs);

        await Assert.That(profile.TclBlock).Contains("""set ::config(arg) "a\$b\[c\]\"\\""");
        await Assert.That(profile.TclBlock).Contains("""set ::arg "a\$b\[c\]\"\\""");
    }
}
