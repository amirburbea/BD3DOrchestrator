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

    public async Task<MediaInspectionResult> InspectAsync(string inputPath, CancellationToken cancellationToken = default)
    {
        string targetMkv = "";
        string folder = "";
        if (fileSystem.DirectoryExists(inputPath))
        {
            folder = inputPath;
            string[] mkvs = fileSystem.GetFiles(folder, "*.3d.mvc.mkv") is { Length: > 0 } mvcFiles ? mvcFiles : fileSystem.GetFiles(folder, "*.mkv");
            if (mkvs is [{ } first, ..])
            {
                targetMkv = first;
            }
        }
        else if (fileSystem.FileExists(inputPath))
        {
            targetMkv = inputPath;
            folder = Path.GetDirectoryName(inputPath) ?? "";
        }
        (string title, int year) = MovieDetector.ParseTitleAndYear(Path.GetFileName(string.IsNullOrEmpty(targetMkv) ? folder : targetMkv));
        MediaInspectionResult result = new()
        {
            MkvPath = targetMkv,
            MovieTitle = title
        };
        // Search for companion mvcFiles matching "Title*.(mka|mks|ec3|sup)"
        if (fileSystem.DirectoryExists(folder) && !string.IsNullOrEmpty(title))
        {
            HashSet<string> companionSet = [with(StringComparer.OrdinalIgnoreCase)];
            // 1. Current directory
            this.FindCompanionFiles(folder, title, targetMkv, companionSet);
            // 2. Parent directory (e.g. root SMB share when MKV is in a subfolder)
            if (fileSystem.GetParentDirectory(folder) is { } parentDir && fileSystem.DirectoryExists(parentDir))
            {
                this.FindCompanionFiles(parentDir, title, targetMkv, companionSet);
            }
            // 3. Title-named subdirectories (e.g. when MKV is in root but companion is in subfolder)
            string sub1 = Path.Combine(folder, title);
            if (fileSystem.DirectoryExists(sub1))
            {
                this.FindCompanionFiles(sub1, title, targetMkv, companionSet);
            }
            if (year > 0)
            {
                string sub2 = Path.Combine(folder, $"{title} ({year})");
                if (fileSystem.DirectoryExists(sub2))
                {
                    this.FindCompanionFiles(sub2, title, targetMkv, companionSet);
                }
            }
            result.CompanionFiles.AddRange(companionSet);
        }
        if (!fileSystem.FileExists(targetMkv) || !fileSystem.FileExists(this._mkvmergePath))
        {
            return result;
        }
        // Run mkvmerge -J to inspect tracks accurately without hanging
        (int exitCode, string stdout, _) = await processRunner.RunAsync(
            this._mkvmergePath,
            $"-J \"{targetMkv}\"",
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
