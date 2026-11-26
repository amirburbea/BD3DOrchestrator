using System.Threading;
using System.Threading.Tasks;

namespace BD3DOrchestrator.Tests.Fakes;

internal sealed class FakeJobRunner : IJobRunner
{
    public HardwareCapabilities Capabilities { get; set; } = HardwareCapabilities.None;

    public Task<HardwareCapabilities> ProbeCapabilitiesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(this.Capabilities);

    public Task LoadAsync(Job job, string path, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task LoadStealFileAsync(Job job, string path, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
