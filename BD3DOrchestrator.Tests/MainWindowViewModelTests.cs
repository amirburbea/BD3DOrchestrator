using System.Threading.Tasks;
using BD3DOrchestrator.Tests.Fakes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace BD3DOrchestrator.Tests;

public sealed class MainWindowViewModelTests
{
    private static JobEditorViewModel CreateJobEditor(IEncodeProfile? profile = null) =>
        new(profile ?? new FakeEncodeProfile(), new FakeJobRunner(), new FakeMessageLogger());

    [Test]
    [Arguments(18.0, "Extra quality: encode at CRF 17 instead of 18 (bigger file)")]
    [Arguments(20.0, "Extra quality: encode at CRF 19 instead of 20 (bigger file)")]
    [Arguments(18.5, "Extra quality: encode at CRF 17.5 instead of 18.5 (bigger file)")]
    public async Task ExtraQuality_ProfileWithCrf_IsAvailableAndLabelShowsBothCrfs(double crf, string expectedLabel)
    {
        JobEditorViewModel vm = CreateJobEditor(new FakeEncodeProfile { Crf = (decimal)crf });

        await Assert.That(vm.IsExtraQualityAvailable).IsTrue();
        await Assert.That(vm.ExtraQualityLabel).IsEqualTo(expectedLabel);
    }

    [Test]
    [Arguments(null, "Extra quality (unavailable: the profile does not encode x264 in CRF mode)")]
    [Arguments(0.0, "Extra quality (unavailable: the profile is already at CRF 0, the lowest)")]
    public async Task ExtraQuality_NoLowerCrf_IsUnavailableAndLabelSaysWhy(double? crf, string expectedLabel)
    {
        JobEditorViewModel vm = CreateJobEditor(new FakeEncodeProfile { Crf = (decimal?)crf });

        await Assert.That(vm.IsExtraQualityAvailable).IsFalse();
        await Assert.That(vm.ExtraQualityLabel).IsEqualTo(expectedLabel);
    }

    [Test]
    public async Task NewViewModel_StartsWithAFreshEditableJob_AndIsRunningTogglesEditable()
    {
        MainWindowViewModel vm = new(CreateJobEditor());

        await Assert.That(vm.IsRunning).IsFalse();
        await Assert.That(vm.JobEditor.IsEditable).IsTrue();
        await Assert.That(vm.JobEditor.Job.UseExtraQuality).IsFalse();
        await Assert.That(vm.JobEditor.Job.IsLoaded).IsFalse();

        vm.IsRunning = true;
        await Assert.That(vm.JobEditor.IsEditable).IsFalse();

        vm.IsRunning = false;
        await Assert.That(vm.JobEditor.IsEditable).IsTrue();
    }

    [Test]
    public async Task JobEditorViewModel_NoCapabilitiesByDefault_DisablesHardwareRow()
    {
        JobEditorViewModel vm = CreateJobEditor();

        await Assert.That(vm.Capabilities).IsEqualTo(HardwareCapabilities.None);
        await Assert.That(vm.IsHardwareEncodingAvailable).IsFalse();
        await Assert.That(vm.UseHardwareHevc).IsFalse();
        await Assert.That(vm.AvailableHardwareEncoders.Count).IsEqualTo(0);
        await Assert.That(vm.Job.UseNvenc).IsFalse();
        await Assert.That(vm.Job.UseQsv).IsFalse();
    }

    [Test]
    public async Task JobEditorViewModel_NvencOnly_DefaultsToNvencAndShowsCqSummary()
    {
        JobEditorViewModel vm = CreateJobEditor();
        vm.Capabilities = HardwareCapabilities.Nvenc;

        await Assert.That(vm.IsHardwareEncodingAvailable).IsTrue();
        await Assert.That(vm.UseHardwareHevc).IsTrue();
        await Assert.That(vm.SelectedHardwareEncoder).IsEqualTo(HardwareEncoder.Nvenc);
        await Assert.That(vm.AvailableHardwareEncoders).Contains(HardwareEncoder.Nvenc);
        await Assert.That(vm.AvailableHardwareEncoders).DoesNotContain(HardwareEncoder.Qsv);
        await Assert.That(vm.HardwareQualitySummary).Contains("CQ 19");
        await Assert.That(vm.Job.UseNvenc).IsTrue();
        await Assert.That(vm.Job.UseQsv).IsFalse();

        vm.Job.MovieTitle = "Old";
        vm.ResetJob();
        await Assert.That(vm.Job.MovieTitle).IsEqualTo(string.Empty);
        await Assert.That(vm.UseHardwareHevc).IsTrue();
        await Assert.That(vm.Job.UseNvenc).IsTrue();
    }

    [Test]
    public async Task JobEditorViewModel_QsvWithVpp_DefaultsToQsvVppAndUpdatesExtraQualityLabel()
    {
        JobEditorViewModel vm = CreateJobEditor();
        vm.Capabilities = HardwareCapabilities.Qsv | HardwareCapabilities.VppQsv;

        await Assert.That(vm.IsHardwareEncodingAvailable).IsTrue();
        await Assert.That(vm.UseHardwareHevc).IsTrue();
        await Assert.That(vm.SelectedHardwareEncoder).IsEqualTo(HardwareEncoder.QsvVpp);
        await Assert.That(vm.AvailableHardwareEncoders).Contains(HardwareEncoder.QsvVpp);
        await Assert.That(vm.AvailableHardwareEncoders).Contains(HardwareEncoder.Qsv);
        await Assert.That(vm.AvailableHardwareEncoders).DoesNotContain(HardwareEncoder.Nvenc);
        await Assert.That(vm.HardwareQualitySummary).Contains("ICQ 15");
        await Assert.That(vm.ExtraQualityLabel).Contains("ICQ 14 instead of 15");
        await Assert.That(vm.Job.UseQsv).IsTrue();
        await Assert.That(vm.Job.UseQsvVpp).IsTrue();
        await Assert.That(vm.Job.UseNvenc).IsFalse();

        vm.SelectedHardwareEncoder = HardwareEncoder.Qsv;
        await Assert.That(vm.Job.UseQsv).IsTrue();
        await Assert.That(vm.Job.UseQsvVpp).IsFalse();

        vm.UseHardwareHevc = false;
        await Assert.That(vm.Job.UseQsv).IsFalse();
        await Assert.That(vm.Job.UseQsvVpp).IsFalse();
        await Assert.That(vm.Job.UseNvenc).IsFalse();
        await Assert.That(vm.ExtraQualityLabel).Contains("CRF 17 instead of 18");
    }

    [Test]
    public async Task JobEditorViewModel_AllCapabilities_PrefersNvencAndSwitchesViaDropdown()
    {
        JobEditorViewModel vm = CreateJobEditor();
        vm.Capabilities = HardwareCapabilities.Nvenc | HardwareCapabilities.Qsv | HardwareCapabilities.VppQsv;

        await Assert.That(vm.AvailableHardwareEncoders.Count).IsEqualTo(3);
        await Assert.That(vm.SelectedHardwareEncoder).IsEqualTo(HardwareEncoder.Nvenc);
        await Assert.That(vm.ExtraQualityLabel).Contains("CQ 18 instead of 19");
        await Assert.That(vm.Job.UseNvenc).IsTrue();
        await Assert.That(vm.Job.UseQsv).IsFalse();
        await Assert.That(vm.Job.UseQsvVpp).IsFalse();

        vm.SelectedHardwareEncoder = HardwareEncoder.QsvVpp;
        await Assert.That(vm.Job.UseQsv).IsTrue();
        await Assert.That(vm.Job.UseQsvVpp).IsTrue();
        await Assert.That(vm.Job.UseNvenc).IsFalse();
        await Assert.That(vm.HardwareQualitySummary).Contains("ICQ 15");
        await Assert.That(vm.ExtraQualityLabel).Contains("ICQ 14 instead of 15");

        vm.ResetJob();
        await Assert.That(vm.SelectedHardwareEncoder).IsEqualTo(HardwareEncoder.Nvenc);
        await Assert.That(vm.Job.UseNvenc).IsTrue();
        await Assert.That(vm.Job.UseQsv).IsFalse();
        await Assert.That(vm.Job.UseQsvVpp).IsFalse();
    }

    [Test]
    public async Task JobEditorViewModel_ReadsPreProbedRunnerCapabilitiesInConstructor()
    {
        FakeJobRunner runner = new() { Capabilities = HardwareCapabilities.Qsv | HardwareCapabilities.VppQsv };
        JobEditorViewModel vm = new(new FakeEncodeProfile(), runner, new FakeMessageLogger());

        await Assert.That(vm.IsHardwareEncodingAvailable).IsTrue();
        await Assert.That(vm.UseHardwareHevc).IsTrue();
        await Assert.That(vm.SelectedHardwareEncoder).IsEqualTo(HardwareEncoder.QsvVpp);
        await Assert.That(vm.Job.UseQsv).IsTrue();
        await Assert.That(vm.Job.UseQsvVpp).IsTrue();
    }
}
