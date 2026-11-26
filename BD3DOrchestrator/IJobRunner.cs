using System.Threading;
using System.Threading.Tasks;

namespace BD3DOrchestrator;

public interface IJobRunner
{
    HardwareCapabilities Capabilities { get; }
    Task<HardwareCapabilities> ProbeCapabilitiesAsync(CancellationToken cancellationToken = default);
    Task LoadAsync(Job job, string path, CancellationToken cancellationToken = default);
    Task LoadStealFileAsync(Job job, string path, CancellationToken cancellationToken = default);
}
