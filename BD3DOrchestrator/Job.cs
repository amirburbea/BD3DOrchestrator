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
    } = string.Empty;

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

    /// <summary>
    /// Encode 10-bit HEVC via NVIDIA NVENC (CQ 19, or CQ 18 with Extra Quality) instead of x264.
    /// </summary>
    public bool UseNvenc
    {
        get;
        set
        {
            if (this.SetField(ref field, value) && value)
            {
                this.UseQsv = false;
            }
        }
    }

    /// <summary>
    /// Encode 10-bit HEVC via Intel Quick Sync (ICQ 16, or ICQ 15 with Extra Quality) instead of x264.
    /// </summary>
    public bool UseQsv
    {
        get;
        set
        {
            if (this.SetField(ref field, value) && value)
            {
                this.UseNvenc = false;
            }
            else if (!value)
            {
                this.UseQsvVpp = false;
            }
        }
    }

    /// <summary>
    /// When <see cref="UseQsv"/> is true, use hardware <c>vpp_qsv</c> for <c>nv12 -> p010le</c> conversion instead of software <c>format=p010le</c>.
    /// </summary>
    public bool UseQsvVpp
    {
        get;
        set => this.SetField(ref field, value);
    }

    /// <summary>
    /// Optional .mkv or .mks file to steal audio and/or subtitle tracks from.
    /// </summary>
    public string StealPath
    {
        get;
        set => this.SetField(ref field, value);
    } = string.Empty;

    public bool DonorHasAudio
    {
        get;
        set => this.SetField(ref field, value);
    }

    public bool DonorHasSubtitles
    {
        get;
        set => this.SetField(ref field, value);
    }

    public bool StealAudio
    {
        get;
        set => this.SetField(ref field, value);
    }

    public bool StealSubtitles
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
