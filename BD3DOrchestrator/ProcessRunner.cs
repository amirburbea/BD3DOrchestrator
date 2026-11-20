using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace BD3DOrchestrator;

/// <summary>
/// Abstraction for running external CLI tools (mkvmerge, ffprobe, ffmpeg).
/// </summary>
public interface IProcessRunner
{
    Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(
        string executable,
        string arguments,
        string? workingDir = null,
        Action<string>? onStdout = null,
        Action<string>? onStderr = null,
        CancellationToken cancellationToken = default
    );
}

public sealed class ProcessRunner : IProcessRunner
{
    public async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(
        string executable,
        string arguments,
        string? workingDir = null,
        Action<string>? onStdout = null,
        Action<string>? onStderr = null,
        CancellationToken cancellationToken = default
    )
    {
        using Process process = new()
        {
            StartInfo = new()
            {
                FileName = executable,
                Arguments = arguments,
                WorkingDirectory = workingDir ?? Directory.GetCurrentDirectory(),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        using StringWriter outputDataWriter = new();
        process.OutputDataReceived += DataHandler(outputDataWriter, onStdout);
        using StringWriter errorDataWriter = new();
        process.ErrorDataReceived += DataHandler(errorDataWriter, onStderr);
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return (process.ExitCode, outputDataWriter.ToString(), errorDataWriter.ToString());

        static DataReceivedEventHandler DataHandler(StringWriter writer, Action<string>? onData) => (_, e) =>
        {
            if (e.Data is { } data)
            {
                writer.WriteLine(data);
                onData?.Invoke(data);
            }
        };
    }
}
