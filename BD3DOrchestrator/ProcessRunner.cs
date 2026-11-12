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

        static DataReceivedEventHandler DataHandler(StringWriter writer, Action<string>? onData) => (s, e) =>
        {
            if (e.Data is not { } data)
            {
                return;
            }
            writer.WriteLine(data);
            onData?.Invoke(data);
        };
    }
}
