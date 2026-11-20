using System;
using System.Diagnostics;
using System.IO;

namespace BD3DOrchestrator;

/// <summary>
/// Abstraction for launching shell processes and inspecting/killing running OS processes.
/// </summary>
public interface IProcessManager
{
    ProcessHandle[] GetProcessesByName(string processName);

    ProcessHandle? StartShell(string fileName, string? workingDirectory = null);
}

/// <summary>
/// Production <see cref="IProcessManager"/> backed by <see cref="Process"/>.
/// </summary>
public sealed class ProcessManager : IProcessManager
{
    public ProcessHandle[] GetProcessesByName(string processName) => Array.ConvertAll(
        Process.GetProcessesByName(processName),
        static process => new ProcessHandle(process)
    );

    public ProcessHandle? StartShell(string fileName, string? workingDirectory = null)
    {
        ProcessStartInfo startInfo = new(fileName)
        {
            WorkingDirectory = workingDirectory ?? Path.GetDirectoryName(fileName),
            UseShellExecute = true
        };
        return Process.Start(startInfo) is { } process ? new ProcessHandle(process) : null;
    }
}
