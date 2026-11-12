using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace BD3DOrchestrator;

public partial class MediaInspector(string toolsetPath)
{
    private readonly string _mkvmergePath = Path.Combine(toolsetPath, "mkvmerge.exe");

    public async Task<MediaInspectionResult> InspectAsync(string inputPath, CancellationToken ct = default)
    {
        MediaInspectionResult result = new();

        string targetMkv = "";
        string folder = "";

        if (Directory.Exists(inputPath))
        {
            folder = inputPath;
            string[] mkvs = Directory.GetFiles(folder, "*.3d.mvc.mkv");
            if (mkvs.Length == 0)
            {
                mkvs = Directory.GetFiles(folder, "*.mkv");
            }

            if (mkvs.Length > 0)
            {
                targetMkv = mkvs[0];
            }
        }
        else if (File.Exists(inputPath))
        {
            targetMkv = inputPath;
            folder = Path.GetDirectoryName(inputPath) ?? "";
        }

        result.MkvPath = targetMkv;

        (string title, int year) = MovieDetector.ParseTitleAndYear(
            !string.IsNullOrEmpty(targetMkv) ? Path.GetFileName(targetMkv) : Path.GetFileName(folder)
        );
        result.MovieTitle = title;

        // Search for companion files matching "Title*.(mka|mks|ec3|sup)"
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

            foreach (string companion in companionSet)
            {
                result.CompanionFiles.Add(companion);
            }
        }

        // Run mkvmerge -J to inspect tracks accurately without hanging
        if (File.Exists(targetMkv) && File.Exists(this._mkvmergePath))
        {
            (int exitCode, string stdout, _) = await ProcessRunner.RunAsync(
                this._mkvmergePath,
                $"-J \"{targetMkv}\"",
                workingDir: folder,
                ct: ct
            ).ConfigureAwait(false);

            if (exitCode == 0 && !string.IsNullOrWhiteSpace(stdout))
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

                            if (type.Equals("audio", StringComparison.OrdinalIgnoreCase))
                            {
                                if (codec.Contains("DTS", StringComparison.OrdinalIgnoreCase))
                                {
                                    result.HasDtsAudio = true;
                                }
                                else if (codec.Contains("E-AC-3", StringComparison.OrdinalIgnoreCase) || codec.Contains("AC-3", StringComparison.OrdinalIgnoreCase))
                                {
                                    result.HasEac3Audio = true;
                                }
                            }
                        }
                    }
                }
                catch
                {
                    // Fallback
                }
            }
        }

        return result;
    }

    private static void FindCompanionFiles(string directory, string title, string targetMkv, HashSet<string> foundFiles)
    {
        if (!Directory.Exists(directory) || string.IsNullOrEmpty(title))
        {
            return;
        }

        try
        {
            Regex companionRegex = CompanionFilesRegex();
            string[] matched = Directory.GetFiles(directory, $"{title}*");
            foreach (string file in matched)
            {
                if (file.Equals(targetMkv, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (companionRegex.IsMatch(file))
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
