using System.Collections.Generic;
using System.IO;
using Microsoft.Extensions.Options;

namespace BD3DOrchestrator;

/// <summary>Fails fast, with every problem listed, instead of letting a bad path cost a long encode.</summary>
public class AppSettingsValidator(IFileSystem fileSystem) : IValidateOptions<AppSettings>
{
    public ValidateOptionsResult Validate(string? name, AppSettings options)
    {
        List<string> failures = [];
        this.RequireDirectory(failures, nameof(options.ToolsetDirectory), options.ToolsetDirectory);
        this.RequireFile(failures, nameof(options.BD3DExecutable), options.BD3DExecutable);
        this.RequireFile(failures, nameof(options.FfmpegExecutable), options.FfmpegExecutable);
        RequireRooted(failures, nameof(options.ProjectDirectory), options.ProjectDirectory, allowEmpty: false);
        RequireRooted(failures, nameof(options.OutputDirectory), options.OutputDirectory, allowEmpty: true);
        if (!fileSystem.FileExists(options.ResolvedProfilePath))
        {
            failures.Add($"Encoding profile not found: '{options.ResolvedProfilePath}' (set ProfilePath or put profile.json next to the exe).");
        }
        if (string.IsNullOrWhiteSpace(options.TmdbApiReadAccessToken))
        {
            failures.Add($"{nameof(options.TmdbApiReadAccessToken)} is not set.");
        }
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private void RequireDirectory(List<string> failures, string setting, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            failures.Add($"{setting} is not set.");
        }
        else if (!fileSystem.DirectoryExists(value))
        {
            failures.Add($"{setting}: folder not found: '{value}'.");
        }
    }

    private void RequireFile(List<string> failures, string setting, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            failures.Add($"{setting} is not set.");
        }
        else if (!fileSystem.FileExists(value))
        {
            failures.Add($"{setting}: file not found: '{value}'.");
        }
    }

    private static void RequireRooted(List<string> failures, string setting, string value, bool allowEmpty)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            if (!allowEmpty)
            {
                failures.Add($"{setting} is not set.");
            }
        }
        else if (!Path.IsPathRooted(value))
        {
            failures.Add($"{setting} must be a full path (got '{value}').");
        }
    }
}
