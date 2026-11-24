using System;
using System.Threading.Tasks;

namespace BD3DOrchestrator;

/// <summary>
/// Standalone state and actions for editing one <see cref="Job"/>. Hosted by <see cref="MainWindowViewModel"/>
/// today, and reusable in a future queue master-detail view or modal dialog.
/// </summary>
public sealed class JobEditorViewModel(IEncodeProfile profile, JobRunner? runner = null, IMessageLogger? logger = null) : ObservableObject
{
    public Job Job
    {
        get;
        set => this.SetField(ref field, value);
    } = new();

    public bool IsEditable
    {
        get;
        set => this.SetField(ref field, value);
    } = true;

    public bool IsNvencAvailable
    {
        get;
        set
        {
            if (!this.SetField(ref field, value))
            {
                return;
            }
            this.OnPropertyChanged(nameof(this.NvencLabel));
            this.Job.UseNvenc = value;
        }
    }

    public string NvencLabel => this.IsNvencAvailable
        ? "Encode 10-bit HEVC via NVIDIA NVENC (CQ 20, or CQ 19 with Extra Quality)"
        : "Encode 10-bit HEVC via NVIDIA NVENC (unavailable: no compatible NVIDIA GPU/driver)";

    /// <summary>Only a profile that encodes x264 in CRF mode above 0 has a lower CRF to step down to.</summary>
    public bool IsExtraQualityAvailable => profile.ExtraQualityCrf is not null;

    public string ExtraQualityLabel => profile is { Crf: { } crf, ExtraQualityCrf: { } extraQualityCrf }
        ? $"Extra quality: encode at CRF {EncodeProfile.FormatCrf(extraQualityCrf)} instead of {EncodeProfile.FormatCrf(crf)} (bigger file)"
        : $"Extra quality (unavailable: {profile.ExtraQualityUnavailableReason})";

    public async Task LoadAsync(string path)
    {
        if (runner is null)
        {
            return;
        }
        try
        {
            await runner.LoadAsync(this.Job, path);
        }
        catch (Exception ex)
        {
            logger?.Log($"Could not load '{path}': {ex.Message}");
        }
    }

    public async Task LoadStealFileAsync(string path)
    {
        if (runner is null)
        {
            return;
        }
        try
        {
            await runner.LoadStealFileAsync(this.Job, path);
        }
        catch (Exception ex)
        {
            logger?.Log($"[Steal] Could not inspect '{path}': {ex.Message}");
        }
    }

    public void ClearStealFile() => JobRunner.ClearStealFile(this.Job);

    public void ResetJob() => this.Job = new() { UseNvenc = this.IsNvencAvailable };

    public async Task ProbeNvencAsync()
    {
        if (runner is null)
        {
            return;
        }
        bool supported = await Task.Run(() => runner.ProbeNvencAsync());
        this.IsNvencAvailable = supported;
        if (supported)
        {
            logger?.Log("[NVENC] 10-bit hevc_nvenc hardware support detected.");
        }
    }
}
