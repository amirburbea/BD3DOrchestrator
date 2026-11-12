using System.Collections.Generic;

namespace BD3DOrchestrator;

public class MediaInspectionResult
{
    public string MkvPath { get; set; } = string.Empty;
    public string MovieTitle { get; set; } = string.Empty;
    public bool HasDtsAudio { get; set; }
    public bool HasEac3Audio { get; set; }
    public List<string> CompanionFiles { get; } = [];
}
