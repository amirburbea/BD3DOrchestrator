using System.Collections.Generic;

namespace BD3DOrchestrator;

public sealed class MediaInspectionResult
{
    public string MkvPath { get; set; } = string.Empty;
    public string MovieTitle { get; set; } = string.Empty;
    public bool HasDtsAudio { get; set; }
    public bool HasDolbyAudio { get; set; }
    public List<string> CompanionFiles { get; } = [];
    public List<int> EmbeddedSrtTrackIds { get; } = [];
}
