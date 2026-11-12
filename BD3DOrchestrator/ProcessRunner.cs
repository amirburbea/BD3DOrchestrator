using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace BD3DOrchestrator;

public class ProcessRunner
{
    public static async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(
        string executable,
        string arguments,
        string? workingDir = null,
        Action<string>? onStdout = null,
        Action<string>? onStderr = null,
        CancellationToken ct = default)
    {
        ProcessStartInfo psi = new()
        {
            FileName = executable,
            Arguments = arguments,
            WorkingDirectory = workingDir ?? Directory.GetCurrentDirectory(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using Process process = new() { StartInfo = psi };
        StringWriter stdoutWriter = new();
        StringWriter stderrWriter = new();

        process.OutputDataReceived += (s, e) =>
        {
            if (e.Data is { } data)
            {
                stdoutWriter.WriteLine(data);
                onStdout?.Invoke(data);
            }
        };

        process.ErrorDataReceived += (s, e) =>
        {
            if (e.Data is { } data)
            {
                stderrWriter.WriteLine(data);
                onStderr?.Invoke(data);
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        return (process.ExitCode, stdoutWriter.ToString(), stderrWriter.ToString());
    }
}
