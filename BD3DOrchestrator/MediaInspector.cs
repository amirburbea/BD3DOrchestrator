using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace BD3DOrchestrator;

public partial class MediaInspector(AppSettings settings)
{
    private readonly string _mkvmergePath = Path.Combine(settings.ToolsetDirectory, "mkvmerge.exe");

    public async Task<MediaInspectionResult> InspectAsync(string inputPath, CancellationToken cancellationToken = default)
    {
        string targetMkv = "";
        string folder = "";
        if (Directory.Exists(inputPath))
        {
            folder = inputPath;
            string[] mkvs = Directory.GetFiles(folder, "*.3d.mvc.mkv") is { Length: > 0 } mvcFiles ? mvcFiles : Directory.GetFiles(folder, "*.mkv");
            if (mkvs is [{ } first, ..])
            {
                targetMkv = first;
            }
        }
        else if (File.Exists(inputPath))
        {
            targetMkv = inputPath;
            folder = Path.GetDirectoryName(inputPath) ?? "";
        }
        (string title, int year) = MovieDetector.ParseTitleAndYear(
            !string.IsNullOrEmpty(targetMkv) ? Path.GetFileName(targetMkv) : Path.GetFileName(folder)
        );
        MediaInspectionResult result = new()
        {
            MkvPath = targetMkv,
            MovieTitle = title
        };
        // Search for companion mvcFiles matching "Title*.(mka|mks|ec3|sup)"
        if (Directory.Exists(folder) && !string.IsNullOrEmpty(title))
        {
            HashSet<string> companionSet = [with(StringComparer.OrdinalIgnoreCase)];
            // 1. Current directory
            FindCompanionFiles(folder, title, targetMkv, companionSet);
            // 2. Parent directory (e.g. root SMB share when MKV is in a subfolder)
            if (Directory.GetParent(folder) is { Exists: true } parentDir)
            {
                FindCompanionFiles(parentDir.FullName, title, targetMkv, companionSet);
            }
            // 3. Title-named subdirectories (e.g. when MKV is in root but companion is in subfolder)
            string sub1 = Path.Combine(folder, title);
            if (Directory.Exists(sub1))
            {
                FindCompanionFiles(sub1, title, targetMkv, companionSet);
            }
            if (year > 0)
            {
                string sub2 = Path.Combine(folder, $"{title} ({year})");
                if (Directory.Exists(sub2))
                {
                    FindCompanionFiles(sub2, title, targetMkv, companionSet);
                }
            }
            result.CompanionFiles.AddRange(companionSet);
        }
        if (!File.Exists(targetMkv) || !File.Exists(this._mkvmergePath))
        {
            return result;
        }
        // Run mkvmerge -J to inspect tracks accurately without hanging
        (int exitCode, string stdout, _) = await ProcessRunner.RunAsync(
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

    private static void FindCompanionFiles(string directory, string title, string targetMkv, ICollection<string> foundFiles)
    {
        if (!Directory.Exists(directory) || string.IsNullOrEmpty(title))
        {
            return;
        }
        try
        {
            foreach (string file in Directory.GetFiles(directory, $"{title}*"))
            {
                if (!file.Equals(targetMkv, StringComparison.OrdinalIgnoreCase) && CompanionFilesRegex().IsMatch(file))
                {
                    foundFiles.Add(file);
                }
            }
        }
        catch
        {
        }
    }

    [GeneratedRegex(@"\.(mka|mks|ec3|ac3|sup|srt|ass)$", RegexOptions.IgnoreCase, "en-US")]
    private static partial Regex CompanionFilesRegex();
}
