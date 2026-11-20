using System;
using System.IO;

namespace BD3DOrchestrator;

/// <summary>
/// Per-machine settings, bound from appsettings.json (shared) and appsettings.local.json (this PC only).
/// There are deliberately no machine-specific defaults: anything missing is reported by <see cref="AppSettingsValidator"/>.
/// </summary>
public class AppSettings
{
    /// <summary>Folder containing mkvmerge.exe and the other BD3D2MK3D tools.</summary>
    public string ToolsetDirectory { get; set; } = string.Empty;

    public string BD3DExecutable { get; set; } = string.Empty;

    public string FfmpegExecutable { get; set; } = string.Empty;

    /// <summary>Where BD3D2MK3D builds the project (demux, AVS, encoded video).</summary>
    public string ProjectDirectory { get; set; } = string.Empty;

    /// <summary>Where the final MKV is written. Empty = inside the project folder.</summary>
    public string OutputDirectory { get; set; } = string.Empty;

    /// <summary>Shared encoding profile. Empty = profile.json next to the exe.</summary>
    public string ProfilePath { get; set; } = string.Empty;

    /// <summary>TMDb API credentials for movie title/year/animation lookup.</summary>
    public TmdbOptions Tmdb { get; set; } = new();

    public string ResolvedProfilePath => string.IsNullOrWhiteSpace(this.ProfilePath)
        ? Path.Combine(AppContext.BaseDirectory, "profile.json")
        : this.ProfilePath;

    /// <summary>Expands %VARIABLES% in every path setting.</summary>
    public void ExpandEnvironmentVariables()
    {
        this.ToolsetDirectory = Environment.ExpandEnvironmentVariables(this.ToolsetDirectory);
        this.BD3DExecutable = Environment.ExpandEnvironmentVariables(this.BD3DExecutable);
        this.FfmpegExecutable = Environment.ExpandEnvironmentVariables(this.FfmpegExecutable);
        this.ProjectDirectory = Environment.ExpandEnvironmentVariables(this.ProjectDirectory);
        this.OutputDirectory = Environment.ExpandEnvironmentVariables(this.OutputDirectory);
        this.ProfilePath = Environment.ExpandEnvironmentVariables(this.ProfilePath);
    }
}
