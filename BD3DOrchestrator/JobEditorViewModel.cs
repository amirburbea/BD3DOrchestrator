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
            this.ApplyDefaultHardwareEncoder();
        }
    }

    public string NvencLabel => this.IsNvencAvailable
        ? "Encode 10-bit HEVC via NVIDIA NVENC (CQ 19, or CQ 18 with Extra Quality)"
        : "Encode 10-bit HEVC via NVIDIA NVENC (unavailable: no compatible NVIDIA GPU/driver)";

    public bool IsQsvAvailable
    {
        get;
        set
        {
            if (!this.SetField(ref field, value))
            {
                return;
            }
            this.OnPropertyChanged(nameof(this.QsvLabel));
            this.ApplyDefaultHardwareEncoder();
        }
    }

    public string QsvLabel => this.IsQsvAvailable
        ? "Encode 10-bit HEVC via Intel Quick Sync (ICQ 16, or ICQ 15 with Extra Quality)"
        : "Encode 10-bit HEVC via Intel Quick Sync (unavailable: no compatible Intel QSV GPU/driver)";

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

    public void ResetJob()
    {
        this.Job = new();
        this.ApplyDefaultHardwareEncoder();
    }

    private void ApplyDefaultHardwareEncoder()
    {
        if (this.IsNvencAvailable)
        {
            this.Job.UseNvenc = true;
        }
        else if (this.IsQsvAvailable)
        {
            this.Job.UseQsv = true;
        }
        else
        {
            this.Job.UseNvenc = false;
            this.Job.UseQsv = false;
        }
    }

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

    public async Task ProbeQsvAsync()
    {
        if (runner is null)
        {
            return;
        }
        bool supported = await Task.Run(() => runner.ProbeQsvAsync());
        this.IsQsvAvailable = supported;
        if (supported)
        {
            logger?.Log("[QSV] 10-bit hevc_qsv hardware support detected.");
        }
    }

    public Task ProbeHardwareEncodersAsync() => Task.WhenAll(this.ProbeNvencAsync(), this.ProbeQsvAsync());
}
