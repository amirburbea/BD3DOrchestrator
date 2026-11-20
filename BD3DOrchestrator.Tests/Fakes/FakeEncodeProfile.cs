using System.Collections.Generic;

namespace BD3DOrchestrator.Tests.Fakes;

public sealed class FakeEncodeProfile : IEncodeProfile
{
    public string Hash { get; init; } = "abc123def456";
    public string MkvTitleTemplate { get; init; } = "{title}";
    public string OutputNameTemplate { get; init; } = "{title}.3d.tab.mkv";
    public IReadOnlyList<KeyValuePair<string, string>> Settings { get; init; } = [];
    public string SourcePath { get; init; } = @"C:\app\profile.json";
    public string TclBlock { get; init; } = "";
}
