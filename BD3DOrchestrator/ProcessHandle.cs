using System;
using System.Diagnostics;

namespace BD3DOrchestrator;

/// <summary>
/// Lightweight handle for a running OS process, allowing <see cref="JobRunner"/> to be tested
/// without spawning real processes.
/// </summary>
public readonly struct ProcessHandle(
    int id,
    Func<DateTime>? getStartTime = null,
    Func<bool>? hasExited = null,
    Action? kill = null,
    Action? dispose = null
) : IDisposable
{
    public ProcessHandle(Process process)
        : this(process.Id, () => process.StartTime, () => process.HasExited, process.Kill, process.Dispose)
    {
    }

    public bool HasExited => hasExited?.Invoke() ?? true;
    public int Id => id;
    public DateTime StartTime => getStartTime?.Invoke() ?? default;

    public void Dispose() => dispose?.Invoke();

    public void Kill() => kill?.Invoke();
}
