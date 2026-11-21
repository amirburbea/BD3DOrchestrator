namespace BD3DOrchestrator;

/// <summary>App-level UI state. Everything about one movie lives on <see cref="Job"/>.</summary>
public sealed class MainWindowViewModel(IEncodeProfile profile) : ObservableObject
{
    public Job Job
    {
        get;
        set => this.SetField(ref field, value);
    } = new();

    public bool IsRunning
    {
        get;
        set => this.SetField(ref field, value);
    }

    public string ActionButtonText
    {
        get;
        set => this.SetField(ref field, value);
    } = "🚀 Auto-Run Pipeline (Demux ➔ Patch AVS ➔ Ingest Audio ➔ Encode & Mux)";

    /// <summary>Only a profile that encodes x264 in CRF mode has a CRF to lower.</summary>
    public bool IsExtraQualityAvailable => profile.ExtraQualityCrf is not null;

    public string ExtraQualityLabel => profile is { Crf: { } crf, ExtraQualityCrf: { } extraQualityCrf }
        ? $"Extra quality: encode at CRF {EncodeProfile.FormatCrf(extraQualityCrf)} instead of {EncodeProfile.FormatCrf(crf)} (bigger file)"
        : "Extra quality (unavailable: the profile does not encode x264 in CRF mode)";
}
