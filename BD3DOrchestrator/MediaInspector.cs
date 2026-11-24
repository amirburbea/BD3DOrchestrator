using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace BD3DOrchestrator;

public partial class MediaInspector(AppSettings settings, IFileSystem fileSystem, IProcessRunner processRunner)
{
    private readonly string _mkvmergePath = Path.Combine(settings.ToolsetDirectory, "mkvmerge.exe");

    public async Task<MediaInspectionResult> InspectAsync(string mkvPath, CancellationToken cancellationToken = default)
    {
        if (!fileSystem.FileExists(mkvPath))
        {
            return new();
        }
        string folder = Path.GetDirectoryName(mkvPath) ?? "";
        (string title, int year) = MovieDetector.ParseTitleAndYear(Path.GetFileName(mkvPath));
        MediaInspectionResult result = new()
        {
            MkvPath = mkvPath,
            MovieTitle = title
        };
        if (fileSystem.DirectoryExists(folder))
        {
            HashSet<string> companionSet = [with(StringComparer.OrdinalIgnoreCase)];
            // 1. Files matching the movie name in the containing folder, parent folder, and title-named subfolders
            if (!string.IsNullOrEmpty(title))
            {
                this.FindCompanionFiles(folder, title, mkvPath, companionSet);
                if (fileSystem.GetParentDirectory(folder) is { } parentDir && fileSystem.DirectoryExists(parentDir))
                {
                    this.FindCompanionFiles(parentDir, title, mkvPath, companionSet);
                }
                string sub1 = Path.Combine(folder, title);
                if (fileSystem.DirectoryExists(sub1))
                {
                    this.FindCompanionFiles(sub1, title, mkvPath, companionSet);
                }
                if (year > 0)
                {
                    string sub2 = Path.Combine(folder, $"{title} ({year})");
                    if (fileSystem.DirectoryExists(sub2))
                    {
                        this.FindCompanionFiles(sub2, title, mkvPath, companionSet);
                    }
                }
            }
            // 2. Any *.mka or *.srt in the containing folder when it has exactly 1 .mkv
            try
            {
                if (fileSystem.GetFiles(folder, "*.mkv").Length == 1)
                {
                    foreach (string pattern in new[] { "*.mka", "*.srt" })
                    {
                        foreach (string file in fileSystem.GetFiles(folder, pattern))
                        {
                            companionSet.Add(file);
                        }
                    }
                }
            }
            catch
            {
            }
            result.CompanionFiles.AddRange(companionSet);
        }
        if (!fileSystem.FileExists(this._mkvmergePath))
        {
            return result;
        }
        // Run mkvmerge -J to inspect tracks accurately without hanging
        (int exitCode, string stdout, _) = await processRunner.RunAsync(
            this._mkvmergePath,
            $"-J \"{mkvPath}\"",
            workingDir: folder,
            cancellationToken: cancellationToken
        ).ConfigureAwait(false);
        if (exitCode is 0 && !string.IsNullOrWhiteSpace(stdout))
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(stdout);
                if (doc.RootElement.TryGetProperty("tracks", out JsonElement tracks))
                {
                    foreach (JsonElement track in tracks.EnumerateArray())
                    {
                        string type = track.TryGetProperty("type", out JsonElement tElem) ? (tElem.GetString() ?? "") : "";
                        string codec = track.TryGetProperty("codec", out JsonElement cElem) ? (cElem.GetString() ?? "") : "";
                        if (type.Equals("subtitles", StringComparison.OrdinalIgnoreCase))
                        {
                            string codecId = track.TryGetProperty("properties", out JsonElement props) &&
                                             props.TryGetProperty("codec_id", out JsonElement cidElem)
                                ? (cidElem.GetString() ?? "")
                                : "";
                            if ((codecId.Equals("S_TEXT/UTF8", StringComparison.OrdinalIgnoreCase) ||
                                 codec.Contains("SRT", StringComparison.OrdinalIgnoreCase)) &&
                                track.TryGetProperty("id", out JsonElement idElem) &&
                                idElem.TryGetInt32(out int trackId))
                            {
                                result.EmbeddedSrtTrackIds.Add(trackId);
                            }
                            continue;
                        }
                        if (!type.Equals("audio", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }
                        if (codec.Contains("DTS", StringComparison.OrdinalIgnoreCase))
                        {
                            result.HasDtsAudio = true;
                        }
                        else if (codec.Contains("AC-3", StringComparison.OrdinalIgnoreCase) || codec.Contains("TrueHD", StringComparison.OrdinalIgnoreCase))
                        {
                            result.HasDolbyAudio = true;
                        }
                    }
                }
            }
            catch
            {
                // Fallback
            }
        }
        return result;
    }

    /// <summary>Inspects a donor .mkv/.mks file via mkvmerge -J to see whether it contains audio and/or subtitle tracks.</summary>
    public async Task<(bool HasAudio, bool HasSubtitles)> InspectStealFileAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!fileSystem.FileExists(path) || !fileSystem.FileExists(this._mkvmergePath))
        {
            return (false, false);
        }
        (int exitCode, string stdout, _) = await processRunner.RunAsync(
            this._mkvmergePath,
            $"-J \"{path}\"",
            workingDir: Path.GetDirectoryName(path),
            cancellationToken: cancellationToken
        ).ConfigureAwait(false);
        if (exitCode is not 0 || string.IsNullOrWhiteSpace(stdout))
        {
            return (false, false);
        }
        bool hasAudio = false;
        bool hasSubtitles = false;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(stdout);
            if (doc.RootElement.TryGetProperty("tracks", out JsonElement tracks))
            {
                foreach (JsonElement track in tracks.EnumerateArray())
                {
                    string type = track.TryGetProperty("type", out JsonElement tElem) ? (tElem.GetString() ?? "") : "";
                    if (type.Equals("audio", StringComparison.OrdinalIgnoreCase))
                    {
                        hasAudio = true;
                    }
                    else if (type.Equals("subtitles", StringComparison.OrdinalIgnoreCase))
                    {
                        hasSubtitles = true;
                    }
                }
            }
        }
        catch
        {
            // Ignore malformed JSON
        }
        return (hasAudio, hasSubtitles);
    }

    private void FindCompanionFiles(string directory, string title, string targetMkv, ICollection<string> foundFiles)
    {
        if (!fileSystem.DirectoryExists(directory) || string.IsNullOrEmpty(title))
        {
            return;
        }
        try
        {
            foreach (string file in fileSystem.GetFiles(directory, $"{title}*"))
            {
                if (!file.Equals(targetMkv, StringComparison.Ordinal) && Path.GetExtension(file) is ".mka" or ".mks" or ".ec3" or ".ac3" or ".sup" or ".srt" or ".ass")
                {
                    foundFiles.Add(file);
                }
            }
        }
        catch
        {
        }
    }
}
