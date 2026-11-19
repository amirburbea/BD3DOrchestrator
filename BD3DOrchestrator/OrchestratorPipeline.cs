using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace BD3DOrchestrator;

public partial class OrchestratorPipeline(string ffmpegPath, Action<string> logger)
{
    private static readonly HashSet<string> _trackOptionSwitches =
    [
        with(StringComparer.OrdinalIgnoreCase),
        "--track-name",
        "--language",
        "--default-track",
        "--forced-track",
        "--sync",
        "--compression",
        "--audio-tracks",
        "--cues"
    ];

    private static readonly JsonSerializerOptions _writeOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>
    /// Replaces BD3D2MK3D's generated output file name (Title + stereo/resolution/director
    /// suffixes) and MKV segment title in the mux option files with the profile's naming template.
    /// The output directory chosen by BD3D2MK3D is preserved.
    /// </summary>
    public void ApplyOutputNaming(string projectMkv3dDir, string title, EncodeProfile profile)
    {
        string cleanTitle = EncodeProfile.SanitizeFileName(title);
        foreach (string name in new[] { "__MUX_3D_OPTIONS.json", "_CUSTOM_MUX_3D_OPTIONS.json" })
        {
            string jsonPath = Path.Combine(projectMkv3dDir, name);
            if (!File.Exists(jsonPath))
            {
                continue;
            }
            try
            {
                JsonDocumentOptions options = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
                List<string> args = [];
                using (JsonDocument doc = JsonDocument.Parse(File.ReadAllText(jsonPath), options))
                {
                    args.AddRange(doc.RootElement.EnumerateArray().Select(element => element.GetString() ?? ""));
                }
                bool custom = name.StartsWith("_CUSTOM", StringComparison.OrdinalIgnoreCase);
                for (int i = 0; i < args.Count - 1; i++)
                {
                    switch (args[i])
                    {
                        case "--output":
                            {
                                string fileName = EncodeProfile.ApplyTitle(profile.OutputNameTemplate, cleanTitle);
                                if (custom)
                                {
                                    fileName = $"{Path.ChangeExtension(fileName, null)}.custom{Path.GetExtension(fileName)}";
                                }
                                string newPath = Path.GetDirectoryName(args[i + 1]) is { Length: > 0 } dir ? Path.Combine(dir, fileName) : fileName;
                                logger($"[Naming] {Path.GetFileName(args[i + 1])} -> {fileName}");
                                args[i + 1] = newPath;
                            }
                            break;
                        case "--title":
                            args[i + 1] = EncodeProfile.ApplyTitle(profile.MkvTitleTemplate, title);
                            break;
                    }
                }
                File.WriteAllText(jsonPath, JsonSerializer.Serialize(args, _writeOptions));
            }
            catch (Exception ex)
            {
                logger($"[Naming] Error renaming output in {name}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Converts a locally demuxed DTS file to E-AC3 5.1. The input is the raw elementary stream
    /// from the project folder, so no data is read from the (network-hosted) source MKV.
    /// </summary>
    public async Task<string?> ConvertAudioToEac3Async(string inputMkv, string outputEac3Path, CancellationToken cancellationToken = default)
    {
        logger($"[Audio] Checking audio streams for {Path.GetFileName(inputMkv)}...");
        string probeArgs = $"-v error -select_streams a:0 -show_entries stream=codec_name,profile,channels -of csv=p=0 \"{inputMkv}\"";
        string ffprobeExe = Path.Combine(Path.GetDirectoryName(ffmpegPath) ?? "", "ffprobe.exe");
        if (!File.Exists(ffprobeExe))
        {
            ffprobeExe = "ffprobe.exe";
        }
        (int probeCode, string probeData, _) = await ProcessRunner.RunAsync(ffprobeExe, probeArgs, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (probeCode != 0 || string.IsNullOrWhiteSpace(probeData))
        {
            logger("[Audio] Warning: Could not ffprobe audio streams, falling back to 5.1 EAC3 direct copy/downmix.");
        }
        string[] parts = probeData.Trim().ToLower().Split(',');
        string codec = parts is [{ } token, ..] ? token.Trim() : "";
        int channels = parts is [_, _, { } count, ..] && int.TryParse(count.Trim(), out int ch) ? ch : 6;
        logger($"[Audio] Detected audio: Codec={codec}, Channels={channels}");
        List<string> audioArgs = [];
        if (channels == 8)
        {
            logger("[Audio] 7.1 detected -> Converting to EAC3 5.1 with Gain-Safe Fold...");
            string panFilter = "pan=5.1|FL=FL|FR=FR|FC=1.1*FC|LFE=LFE|BL=0.707*SL+0.707*BL|BR=0.707*SR+0.707*BR";
            audioArgs.AddRange(["-af", panFilter, "-c:a", "eac3", "-b:a", "640k"]);
        }
        else if (channels == 7)
        {
            logger("[Audio] 6.1 detected -> Converting to EAC3 5.1 with Rear-Center fold...");
            string panFilter = "pan=5.1|FL=FL|FR=FR|FC=1.1*FC|LFE=LFE|BL=0.707*SL+0.707*BC|BR=0.707*SR+0.707*BC";
            audioArgs.AddRange(["-af", panFilter, "-c:a", "eac3", "-b:a", "640k"]);
        }
        else
        {
            logger("[Audio] Converting to EAC3 5.1 640k...");
            audioArgs.AddRange(["-c:a", "eac3", "-b:a", "640k", "-ac", "6"]);
        }
        string ffmpegArgs = $"-y -i \"{inputMkv}\" -map 0:a:0 {string.Join(" ", audioArgs)} \"{outputEac3Path}\"";
        (int exitCode, _, string stderr) = await ProcessRunner.RunAsync(
            ffmpegPath,
            ffmpegArgs,
            onStderr: line =>
            {
                if (line.Contains("size="))
                {
                    logger($"[FFmpeg] {line}");
                }
            },
            cancellationToken: cancellationToken
        ).ConfigureAwait(false);
        if (exitCode == 0 && File.Exists(outputEac3Path))
        {
            logger($"[Audio] Successfully converted to EAC3: {Path.GetFileName(outputEac3Path)}");
            return outputEac3Path;
        }
        logger($"[Audio] Error converting audio with FFmpeg: {stderr}");
        return null;
    }

    /// <summary>
    /// Locates the DTS / DTS-HD file that BD3D2MK3D (tsMuxeR) already demuxed into the project
    /// folder, by reading the audio entry from __MUX_3D_OPTIONS.json. Falls back to scanning the
    /// project folder for a demuxed .dts/.dtshd file. Never touches the source MKV.
    /// </summary>
    public string? FindDemuxedDtsFile(string projectMkv3dDir)
    {
        string jsonPath = Path.Combine(projectMkv3dDir, "__MUX_3D_OPTIONS.json");
        if (File.Exists(jsonPath))
        {
            try
            {
                JsonDocumentOptions options = new()
                {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true
                };
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(jsonPath), options);
                foreach (JsonElement element in doc.RootElement.EnumerateArray())
                {
                    string arg = element.GetString() ?? "";
                    if (arg.EndsWith(".dts", StringComparison.OrdinalIgnoreCase) ||
                        arg.EndsWith(".dtshd", StringComparison.OrdinalIgnoreCase))
                    {
                        string candidate = Path.Combine(projectMkv3dDir, arg);
                        if (File.Exists(candidate))
                        {
                            logger($"[Audio] __MUX_3D_OPTIONS.json references demuxed DTS: {arg}");
                            return candidate;
                        }
                        logger($"[Audio] Warning: JSON references '{arg}' but it is not on disk.");
                    }
                }
            }
            catch (Exception ex)
            {
                logger($"[Audio] Could not parse __MUX_3D_OPTIONS.json: {ex.Message}");
            }
        }
        // Local-only fallback (element.g. JSON already patched by a previous run)
        string? scanned = Directory.GetFiles(projectMkv3dDir, "*.*")
            .Where(f => f.EndsWith(".dts", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".dtshd", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => new FileInfo(f).Length)
            .FirstOrDefault();
        if (scanned != null)
        {
            logger($"[Audio] Using demuxed DTS found in project folder: {Path.GetFileName(scanned)}");
        }
        return scanned;
    }

    public bool InjectAvsTweak(string projectMkv3dDir)
    {
        string avsFile = Path.Combine(projectMkv3dDir, "__ENCODE_3D_MOVIE.avs");
        if (!File.Exists(avsFile))
        {
            logger($"[AVS Patch] File not found: {avsFile}");
            return false;
        }
        string content = File.ReadAllText(avsFile);
        string tweakLine = "Tweak(sat=1.1, cont=1.05)";
        if (content.Contains(tweakLine))
        {
            logger("[AVS Patch] Tweak already exists in AVS file. Skipping.");
            return true;
        }
        string backupFile = avsFile + ".bak";
        File.Copy(avsFile, backupFile, overwrite: true);
        logger($"[AVS Patch] Created backup at {Path.GetFileName(backupFile)}");
        string newContent = AvsEndRegex().Replace(content, $"{tweakLine}\r\nReturn(last)");
        File.WriteAllText(avsFile, newContent);
        logger($"[AVS Patch] Injected '{tweakLine}' right before Return(last)!");
        return true;
    }

    public void NeutralizePostProcessCmd(string projectMkv3dDir)
    {
        foreach (string name in new[] { "__POSTPROCESS_3D.cmd", "_CUSTOM_POSTPROCESS_3D.cmd", "_POSTPROCESS_2D.cmd" })
        {
            string targetFile = Path.Combine(projectMkv3dDir, name);
            if (!File.Exists(targetFile))
            {
                continue;
            }
            // BD3D puts "start "" ." and "pause" at the end of post-process scripts which pops
            // Explorer and blocks exit!
            File.WriteAllText(targetFile, "@echo off\r\nREM Neutralized Explorer popup and pause\r\nexit /B 0\r\n");
            logger($"[PostProcess] Neutralized {Path.GetFileName(targetFile)} (disabled 'pause' and Explorer popups).");
        }
    }

    public bool UpdateMuxJson(string projectMkv3dDir, string? customEac3File, IReadOnlyList<string> companionFiles)
    {
        bool anyUpdated = false;
        foreach (string targetName in new[] { "__MUX_3D_OPTIONS.json", "_CUSTOM_MUX_3D_OPTIONS.json" })
        {
            string jsonPath = Path.Combine(projectMkv3dDir, targetName);
            if (!File.Exists(jsonPath))
            {
                continue;
            }
            if (this.UpdateSingleMuxJson(jsonPath, projectMkv3dDir, customEac3File, companionFiles))
            {
                anyUpdated = true;
            }
        }
        return anyUpdated;
    }

    private bool UpdateSingleMuxJson(string jsonPath, string projectMkv3dDir, string? customEac3File, IReadOnlyList<string> companionFiles)
    {
        try
        {
            string rawJson = File.ReadAllText(jsonPath);
            JsonDocumentOptions options = new()
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            };
            using JsonDocument doc = JsonDocument.Parse(rawJson, options);
            List<string> arguments = [.. doc.RootElement.EnumerateArray().Select(element => element.GetString() ?? "")];
            // If we converted DTS to custom EAC3, strip original DTS/DTS-HD audio tracks from arguments
            if (!string.IsNullOrEmpty(customEac3File) && File.Exists(customEac3File))
            {
                while (true)
                {
                    int dtsIndex = -1;
                    for (int i = 0; i < arguments.Count; i++)
                    {
                        string arg = arguments[i];
                        if (arg.EndsWith(".dts", StringComparison.OrdinalIgnoreCase) ||
                            arg.EndsWith(".dtshd", StringComparison.OrdinalIgnoreCase))
                        {
                            dtsIndex = i;
                            break;
                        }
                    }
                    if (dtsIndex < 0)
                    {
                        break;
                    }
                    int startIndex = dtsIndex;
                    while (startIndex >= 2 && _trackOptionSwitches.Contains(arguments[startIndex - 2]))
                    {
                        startIndex -= 2;
                    }
                    int removeCount = dtsIndex - startIndex + 1;
                    logger($"[Mux Options] Stripping original DTS track '{arguments[dtsIndex]}' and its switches from {Path.GetFileName(jsonPath)}.");
                    arguments.RemoveRange(startIndex, removeCount);
                }
            }
            // Check if any existing audio tracks remain in JSON
            bool hasAudioTrack = false;
            foreach (string arg in arguments)
            {
                if (arg.EndsWith(".dts", StringComparison.OrdinalIgnoreCase) ||
                    arg.EndsWith(".dtshd", StringComparison.OrdinalIgnoreCase) ||
                    arg.EndsWith(".ac3", StringComparison.OrdinalIgnoreCase) ||
                    arg.EndsWith(".ec3", StringComparison.OrdinalIgnoreCase) ||
                    arg.EndsWith(".flac", StringComparison.OrdinalIgnoreCase) ||
                    arg.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) ||
                    arg.EndsWith(".mka", StringComparison.OrdinalIgnoreCase))
                {
                    hasAudioTrack = true;
                    break;
                }
            }
            // If we converted DTS to custom EAC3, inject it
            if (!string.IsNullOrEmpty(customEac3File) && File.Exists(customEac3File))
            {
                string destEac3 = Path.Combine(projectMkv3dDir, Path.GetFileName(customEac3File));
                if (!destEac3.Equals(customEac3File, StringComparison.OrdinalIgnoreCase))
                {
                    File.Copy(customEac3File, destEac3, overwrite: true);
                }
                logger($"[Mux Options] Injecting converted EAC3 audio: {Path.GetFileName(destEac3)}");
                arguments.Add("--track-name");
                arguments.Add("0:English (E-AC-3 5.1 640k)");
                arguments.Add("--language");
                arguments.Add("0:eng");
                arguments.Add("--default-track");
                arguments.Add(hasAudioTrack ? "0:no" : "0:yes");
                arguments.Add(Path.GetFileName(destEac3));
                hasAudioTrack = true;
            }
            // If still no audio track in JSON, check if tsMuxeR demuxed an audio file in the
            // project folder that BD3D2MK3D missed or failed to rename (element.g. *.ec3, *.ac3, *.dts)
            if (!hasAudioTrack)
            {
                List<string> audioCandidates = [];
                foreach (string f in Directory.GetFiles(projectMkv3dDir, "*.*"))
                {
                    if (f.EndsWith(".ec3", StringComparison.OrdinalIgnoreCase) ||
                        f.EndsWith(".ac3", StringComparison.OrdinalIgnoreCase) ||
                        f.EndsWith(".dts", StringComparison.OrdinalIgnoreCase) ||
                        f.EndsWith(".mka", StringComparison.OrdinalIgnoreCase))
                    {
                        audioCandidates.Add(f);
                    }
                }
                if (audioCandidates is [{ } primaryAudio, ..])
                {
                    string ext = Path.GetExtension(primaryAudio).TrimStart('.').ToUpperInvariant();
                    logger($"[Mux Options] Auto-recovered orphaned demuxed audio: {Path.GetFileName(primaryAudio)}");
                    arguments.Add("--track-name");
                    arguments.Add($"0:English ({ext})");
                    arguments.Add("--language");
                    arguments.Add("0:eng");
                    arguments.Add("--default-track");
                    arguments.Add("0:yes");
                    arguments.Add(Path.GetFileName(primaryAudio));
                    hasAudioTrack = true;
                }
            }
            // Ingest all matching companion files (.ec3, .mka, .mks, .sup, .srt, .ass)
            foreach (string companion in companionFiles)
            {
                if (!File.Exists(companion))
                {
                    continue;
                }
                string ext = Path.GetExtension(companion).ToLowerInvariant();
                string destCompanion = Path.Combine(projectMkv3dDir, Path.GetFileName(companion));
                if (!destCompanion.Equals(companion, StringComparison.OrdinalIgnoreCase))
                {
                    File.Copy(companion, destCompanion, overwrite: true);
                }
                logger($"[Mux Options] Ingesting companion file into mux: {Path.GetFileName(destCompanion)}");
                if (ext is ".ec3" or ".ac3" or ".mka")
                {
                    arguments.Add("--track-name");
                    arguments.Add($"0:English ({ext.TrimStart('.').ToUpperInvariant()})");
                    arguments.Add("--language");
                    arguments.Add("0:eng");
                    arguments.Add("--default-track");
                    arguments.Add(hasAudioTrack ? "0:no" : "0:yes");
                    arguments.Add(Path.GetFileName(destCompanion));
                    hasAudioTrack = true;
                }
                else if (ext is ".mks" or ".sup" or ".srt" or ".ass")
                {
                    string lang = "eng";
                    string compLower = Path.GetFileName(companion).ToLowerInvariant();
                    if (compLower.Contains(".spa.") || compLower.Contains(".es."))
                    {
                        lang = "spa";
                    }
                    else if (compLower.Contains(".fre.") || compLower.Contains(".fra.") || compLower.Contains(".fr."))
                    {
                        lang = "fre";
                    }
                    else if (compLower.Contains(".ger.") || compLower.Contains(".deu.") || compLower.Contains(".de."))
                    {
                        lang = "ger";
                    }
                    arguments.Add("--language");
                    arguments.Add($"0:{lang}");
                    if (compLower.Contains("forced"))
                    {
                        arguments.Add("--forced-track");
                        arguments.Add("0:yes");
                        arguments.Add("--default-track");
                        arguments.Add("0:yes");
                        arguments.Add("--track-name");
                        arguments.Add($"0:{(lang == "eng" ? "English" : lang.ToUpperInvariant())} (Forced)");
                    }
                    arguments.Add(Path.GetFileName(destCompanion));
                }
            }
            string outputJson = JsonSerializer.Serialize(arguments, _writeOptions);
            File.WriteAllText(jsonPath, outputJson);
            logger($"[Mux Options] Successfully updated {Path.GetFileName(jsonPath)} with audio and companion tracks!");
            return true;
        }
        catch (Exception ex)
        {
            logger($"[Mux Options] Error updating JSON options {Path.GetFileName(jsonPath)}: {ex.Message}");
            return false;
        }
    }

    [GeneratedRegex(@"(?m)^Return\(last\)")]
    private static partial Regex AvsEndRegex();
}
