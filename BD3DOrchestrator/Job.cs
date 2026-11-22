namespace BD3DOrchestrator;

/// <summary>
/// Everything that belongs to one movie: what was loaded, what was detected, and the options the user chose.
/// Starting the next movie is just <c>viewModel.Job = new()</c>.
/// </summary>
public sealed class Job : ObservableObject
{
    public string InputPath
    {
        get;
        set => this.SetField(ref field, value);
    } = "(Or click Browse to select a file from SMB or Local Drive)";

    public string MovieTitle
    {
        get;
        set => this.SetField(ref field, value);
    } = string.Empty;

    public string DetectionReason
    {
        get;
        set => this.SetField(ref field, value);
    } = "Detection: Waiting for file...";

    public bool ApplyAvsTweak
    {
        get;
        set => this.SetField(ref field, value);
    }

    /// <summary>
    /// Encode one CRF step below the profile's (see <see cref="IEncodeProfile.ExtraQualityCrf"/>), for the rare film
    /// worth the bigger file. Off by default; everything else in the profile still applies.
    /// </summary>
    public bool UseExtraQuality
    {
        get;
        set => this.SetField(ref field, value);
    }

    /// <summary>Result of inspecting the input file; null until something has been loaded.</summary>
    public MediaInspectionResult? Inspection { get; set; }

    public bool IsLoaded => this.Inspection is { MkvPath.Length: > 0 };

    /// <summary>The title typed/detected by the user, falling back to the one parsed from the file name.</summary>
    public string EffectiveTitle => this.MovieTitle.Trim() is { Length: > 0 } title ? title : this.Inspection?.MovieTitle ?? string.Empty;
}
