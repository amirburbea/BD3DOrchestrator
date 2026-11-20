using System.Threading.Tasks;
using BD3DOrchestrator.Tests.Fakes;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace BD3DOrchestrator.Tests;

public sealed class AppSettingsValidatorTests
{
    private static AppSettings CreateValidSettings(FakeFileSystem fs)
    {
        fs.AddDirectory(@"C:\tools");
        fs.AddFile(@"C:\tools\BD3D2MK3D.exe");
        fs.AddFile(@"C:\tools\ffmpeg.exe");
        fs.AddFile(@"C:\app\profile.json", "{}");
        return new()
        {
            ToolsetDirectory = @"C:\tools",
            BD3DExecutable = @"C:\tools\BD3D2MK3D.exe",
            FfmpegExecutable = @"C:\tools\ffmpeg.exe",
            ProjectDirectory = @"D:\Projects",
            OutputDirectory = @"D:\Output",
            ProfilePath = @"C:\app\profile.json",
            TmdbApiReadAccessToken = "token-123"
        };
    }

    [Test]
    public async Task Validate_AllValid_ReturnsSuccess()
    {
        FakeFileSystem fs = new();
        AppSettings settings = CreateValidSettings(fs);
        AppSettingsValidator validator = new(fs);

        ValidateOptionsResult result = validator.Validate(null, settings);

        await Assert.That(result.Succeeded).IsTrue();
    }

    [Test]
    public async Task Validate_MissingFilesAndRelativePaths_ReportsEveryFailure()
    {
        FakeFileSystem fs = new();
        AppSettings settings = new()
        {
            ToolsetDirectory = @"C:\missing-tools",
            BD3DExecutable = @"C:\missing\BD3D2MK3D.exe",
            FfmpegExecutable = "",
            ProjectDirectory = @"relative\projects",
            OutputDirectory = @"relative\output",
            ProfilePath = @"C:\missing\profile.json",
            TmdbApiReadAccessToken = "   "
        };
        AppSettingsValidator validator = new(fs);

        ValidateOptionsResult result = validator.Validate(null, settings);

        await Assert.That(result.Failed).IsTrue();
        await Assert.That(string.Join("\n", result.Failures!)).IsEqualTo(string.Join("\n",
            @"ToolsetDirectory: folder not found: 'C:\missing-tools'.",
            @"BD3DExecutable: file not found: 'C:\missing\BD3D2MK3D.exe'.",
            "FfmpegExecutable is not set.",
            @"ProjectDirectory must be a full path (got 'relative\projects').",
            @"OutputDirectory must be a full path (got 'relative\output').",
            @"Encoding profile not found: 'C:\missing\profile.json' (set ProfilePath or put profile.json next to the exe).",
            "TmdbApiReadAccessToken is not set."));
    }

    [Test]
    public async Task Validate_EmptyOutputDirectory_IsAllowed_ButEmptyProjectDirectoryIsNot()
    {
        FakeFileSystem fs = new();
        AppSettings settings = CreateValidSettings(fs);
        settings.OutputDirectory = "";
        settings.ProjectDirectory = " ";
        AppSettingsValidator validator = new(fs);

        ValidateOptionsResult result = validator.Validate(null, settings);

        await Assert.That(string.Join("\n", result.Failures!)).IsEqualTo("ProjectDirectory is not set.");
    }
}
