using System.Threading.Tasks;
using BD3DOrchestrator.Tests.Fakes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace BD3DOrchestrator.Tests;

public sealed class MainWindowViewModelTests
{
    [Test]
    [Arguments(18.0, "Extra quality: encode at CRF 17 instead of 18 (bigger file)")]
    [Arguments(20.0, "Extra quality: encode at CRF 19 instead of 20 (bigger file)")]
    [Arguments(18.5, "Extra quality: encode at CRF 17.5 instead of 18.5 (bigger file)")]
    public async Task ExtraQuality_ProfileWithCrf_IsAvailableAndLabelShowsBothCrfs(double crf, string expectedLabel)
    {
        JobEditorViewModel vm = new(new FakeEncodeProfile { Crf = (decimal)crf });

        await Assert.That(vm.IsExtraQualityAvailable).IsTrue();
        await Assert.That(vm.ExtraQualityLabel).IsEqualTo(expectedLabel);
    }

    [Test]
    [Arguments(null, "Extra quality (unavailable: the profile does not encode x264 in CRF mode)")]
    [Arguments(0.0, "Extra quality (unavailable: the profile is already at CRF 0, the lowest)")]
    public async Task ExtraQuality_NoLowerCrf_IsUnavailableAndLabelSaysWhy(double? crf, string expectedLabel)
    {
        JobEditorViewModel vm = new(new FakeEncodeProfile { Crf = (decimal?)crf });

        await Assert.That(vm.IsExtraQualityAvailable).IsFalse();
        await Assert.That(vm.ExtraQualityLabel).IsEqualTo(expectedLabel);
    }

    [Test]
    public async Task NewViewModel_StartsWithAFreshEditableJob_AndIsRunningTogglesEditable()
    {
        MainWindowViewModel vm = new(new(new FakeEncodeProfile()));

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
    public async Task JobEditorViewModel_NvencUnavailableByDefault_AndUpdatesLabelWhenAvailable()
    {
        JobEditorViewModel vm = new(new FakeEncodeProfile());

        await Assert.That(vm.IsNvencAvailable).IsFalse();
        await Assert.That(vm.NvencLabel).Contains("unavailable");

        vm.IsNvencAvailable = true;
        await Assert.That(vm.NvencLabel).DoesNotContain("unavailable");
        await Assert.That(vm.NvencLabel).Contains("CQ 19");
        await Assert.That(vm.Job.UseNvenc).IsTrue();

        vm.Job.MovieTitle = "Old";
        vm.ResetJob();
        await Assert.That(vm.Job.MovieTitle).IsEqualTo(string.Empty);
        await Assert.That(vm.Job.UseNvenc).IsTrue();
    }

    [Test]
    public async Task JobEditorViewModel_QsvUnavailableByDefault_AndDefaultsToQsvWhenNvencUnavailable()
    {
        JobEditorViewModel vm = new(new FakeEncodeProfile());

        await Assert.That(vm.IsQsvAvailable).IsFalse();
        await Assert.That(vm.QsvLabel).Contains("unavailable");

        vm.IsQsvAvailable = true;
        await Assert.That(vm.QsvLabel).DoesNotContain("unavailable");
        await Assert.That(vm.QsvLabel).Contains("ICQ 17");
        await Assert.That(vm.Job.UseQsv).IsTrue();
        await Assert.That(vm.Job.UseNvenc).IsFalse();

        // If NVENC is also available, NVENC takes priority as the default.
        vm.IsNvencAvailable = true;
        await Assert.That(vm.Job.UseNvenc).IsTrue();
        await Assert.That(vm.Job.UseQsv).IsFalse();

        vm.ResetJob();
        await Assert.That(vm.Job.UseNvenc).IsTrue();
        await Assert.That(vm.Job.UseQsv).IsFalse();
    }
}
