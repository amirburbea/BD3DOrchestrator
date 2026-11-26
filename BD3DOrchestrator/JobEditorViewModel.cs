using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace BD3DOrchestrator;

/// <summary>
/// Standalone state and actions for editing one <see cref="Job"/>. Hosted by <see
/// cref="MainWindowViewModel"/> today, and reusable in a future queue master-detail view or modal dialog.
/// </summary>
public sealed class JobEditorViewModel : ObservableObject
{
    private readonly IEncodeProfile _profile;
    private readonly IJobRunner _runner;
    private readonly IMessageLogger _logger;

    public JobEditorViewModel(IEncodeProfile profile, IJobRunner runner, IMessageLogger logger)
    {
        this._profile = profile;
        this._runner = runner;
        this._logger = logger;
        this.Capabilities = runner.Capabilities;
    }

    public IReadOnlyList<HardwareEncoder> AvailableHardwareEncoders { get; private set; } = [];

    public HardwareCapabilities Capabilities
    {
        get;
        set
        {
            if (!this.SetField(ref field, value))
            {
                return;
            }
            List<HardwareEncoder> encoders = [];
            AddIfPresent(encoders, value, HardwareCapabilities.Nvenc, HardwareEncoder.Nvenc);
            if (AddIfPresent(encoders, value, HardwareCapabilities.Qsv, HardwareEncoder.Qsv))
            {
                AddIfPresent(encoders, value, HardwareCapabilities.VppQsv, HardwareEncoder.QsvVpp);
            }
            encoders.Sort();
            this.AvailableHardwareEncoders = encoders;
            this.ApplyDefaultHardwareEncoder();
            this.OnPropertyChanged(nameof(this.AvailableHardwareEncoders));
            this.OnPropertyChanged(nameof(this.IsHardwareEncodingAvailable));

            static bool AddIfPresent(List<HardwareEncoder> encoders, HardwareCapabilities value, HardwareCapabilities test, HardwareEncoder encoder)
            {
                if ((value & test) != test)
                {
                    return false;
                }
                encoders.Add(encoder);
                return true;
            }
        }


    }

    public string ExtraQualityLabel
    {
        get
        {
            if (this.UseHardwareHevc)
            {
                return this.SelectedHardwareEncoder switch
                {
                    HardwareEncoder.Nvenc => "Extra quality: encode at CQ 18 instead of 19 (bigger file)",
                    HardwareEncoder.QsvVpp or HardwareEncoder.Qsv => "Extra quality: encode at ICQ 15 instead of 16 (bigger file)",
                    _ => string.Empty
                };
            }
            return this._profile is { Crf: { } crf, ExtraQualityCrf: { } extraQualityCrf }
                ? $"Extra quality: encode at CRF {EncodeProfile.FormatCrf(extraQualityCrf)} instead of {EncodeProfile.FormatCrf(crf)} (bigger file)"
                : $"Extra quality (unavailable: {this._profile.ExtraQualityUnavailableReason})";
        }
    }

    public string HardwareQualitySummary => this.SelectedHardwareEncoder switch
    {
        HardwareEncoder.Nvenc => "10-bit hevc_nvenc — CQ 19, or CQ 18 with Extra Quality",
        HardwareEncoder.QsvVpp => "10-bit hevc_qsv (vpp_qsv) — ICQ 16, or ICQ 15 with Extra Quality",
        HardwareEncoder.Qsv => "10-bit hevc_qsv (software p010le) — ICQ 16, or ICQ 15 with Extra Quality",
        _ => string.Empty
    };

    public bool IsEditable
    {
        get;
        set => this.SetField(ref field, value);
    } = true;

    /// <summary>
    /// Hardware encoders always have a 1-step quality boost; x264 requires CRF mode above 0.
    /// </summary>
    public bool IsExtraQualityAvailable => this.UseHardwareHevc || this._profile.ExtraQualityCrf is not null;

    public bool IsHardwareEncodingAvailable => this.AvailableHardwareEncoders.Count > 0;

    public Job Job
    {
        get;
        set => this.SetField(ref field, value);
    } = new();

    public HardwareEncoder SelectedHardwareEncoder
    {
        get;
        set
        {
            if (!this.SetField(ref field, value))
            {
                return;
            }
            if (this.UseHardwareHevc)
            {
                this.ApplySelectedHardwareEncoder();
            }
            this.OnPropertyChanged(nameof(this.HardwareQualitySummary));
            this.OnPropertyChanged(nameof(this.ExtraQualityLabel));
        }
    } = HardwareEncoder.Nvenc;

    public bool UseHardwareHevc
    {
        get => this.Job.UseNvenc || this.Job.UseQsv;
        set
        {
            if (!value)
            {
                this.Job.UseNvenc = false;
                this.Job.UseQsv = false;
            }
            else if (this.IsHardwareEncodingAvailable)
            {
                this.ApplySelectedHardwareEncoder();
            }
            this.OnPropertyChanged();
            this.OnPropertyChanged(nameof(this.HardwareQualitySummary));
            this.OnPropertyChanged(nameof(this.IsExtraQualityAvailable));
            this.OnPropertyChanged(nameof(this.ExtraQualityLabel));
        }
    }

    public void ClearStealFile() => JobRunner.ClearStealFile(this.Job);

    public async Task LoadAsync(string path)
    {
        try
        {
            await this._runner.LoadAsync(this.Job, path);
        }
        catch (Exception ex)
        {
            this._logger.Log($"Could not load '{path}': {ex.Message}");
        }
    }

    public async Task LoadStealFileAsync(string path)
    {
        try
        {
            await this._runner.LoadStealFileAsync(this.Job, path);
        }
        catch (Exception ex)
        {
            this._logger.Log($"[Steal] Could not inspect '{path}': {ex.Message}");
        }
    }

    public void ResetJob()
    {
        this.Job = new();
        this.ApplyDefaultHardwareEncoder();
    }

    private void ApplyDefaultHardwareEncoder()
    {
        if (this.AvailableHardwareEncoders is [{ } first, ..])
        {
            this.SelectedHardwareEncoder = first;
            this.ApplySelectedHardwareEncoder();
        }
        else
        {
            this.Job.UseNvenc = false;
            this.Job.UseQsv = false;
        }
        this.OnPropertyChanged(nameof(this.UseHardwareHevc));
        this.OnPropertyChanged(nameof(this.HardwareQualitySummary));
        this.OnPropertyChanged(nameof(this.IsExtraQualityAvailable));
        this.OnPropertyChanged(nameof(this.ExtraQualityLabel));
    }

    private void ApplySelectedHardwareEncoder()
    {
        switch (this.SelectedHardwareEncoder)
        {
            case HardwareEncoder.Nvenc:
                this.Job.UseNvenc = true;
                this.Job.UseQsvVpp = false;
                break;
            case HardwareEncoder.QsvVpp:
                this.Job.UseQsv = true;
                this.Job.UseQsvVpp = true;
                break;
            case HardwareEncoder.Qsv:
                this.Job.UseQsv = true;
                this.Job.UseQsvVpp = false;
                break;
        }
    }
}
