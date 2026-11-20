using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace BD3DOrchestrator.Tests.Fakes;

public sealed class FakeProcessRunner : IProcessRunner
{
    public readonly record struct RecordedCall(string Executable, string Arguments, string? WorkingDir);

    private readonly Queue<Func<RecordedCall, (int ExitCode, string Stdout, string Stderr)>> _handlers = new();

    public List<RecordedCall> Calls { get; } = [];

    public FakeProcessRunner EnqueueResult(int exitCode = 0, string stdout = "", string stderr = "") =>
        this.EnqueueHandler(_ => (exitCode, stdout, stderr));

    public FakeProcessRunner EnqueueHandler(Func<RecordedCall, (int ExitCode, string Stdout, string Stderr)> handler)
    {
        this._handlers.Enqueue(handler);
        return this;
    }

    public Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(
        string executable,
        string arguments,
        string? workingDir = null,
        Action<string>? onStdout = null,
        Action<string>? onStderr = null,
        CancellationToken cancellationToken = default)
    {
        RecordedCall call = new(executable, arguments, workingDir);
        this.Calls.Add(call);
        (int ExitCode, string Stdout, string Stderr) result = this._handlers.Count > 0
            ? this._handlers.Dequeue()(call)
            : (0, "", "");
        if (!string.IsNullOrEmpty(result.Stdout) && onStdout is not null)
        {
            foreach (string line in result.Stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                onStdout(line);
            }
        }
        if (!string.IsNullOrEmpty(result.Stderr) && onStderr is not null)
        {
            foreach (string line in result.Stderr.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                onStderr(line);
            }
        }
        return Task.FromResult(result);
    }
}
