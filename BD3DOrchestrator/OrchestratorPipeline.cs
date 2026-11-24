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

public partial class OrchestratorPipeline(AppSettings settings, IEncodeProfile profile, IMessageLogger logger, IFileSystem fileSystem, IProcessRunner processRunner)
{
    private static readonly string[] _mux3DOptionsFiles = ["__MUX_3D_OPTIONS.json", "_CUSTOM_MUX_3D_OPTIONS.json"];

    private static readonly string[] _tags3DFiles = ["__TAGS_3D.xml", "_CUSTOM_TAGS_3D.xml"];

    private static readonly string[] _postProcessFiles = ["__POSTPROCESS_3D.cmd", "_CUSTOM_POSTPROCESS_3D.cmd", "_POSTPROCESS_2D.cmd"];

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
        "--cues",
        "--stereo-mode",
        "--aspect-ratio",
        "--default-duration",
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
    public void ApplyOutputNaming(string projectMkv3dDir, string title)
    {
        string cleanTitle = PathMethods.SanitizeFileName(title);
        foreach (string name in _mux3DOptionsFiles)
        {
            string jsonPath = Path.Combine(projectMkv3dDir, name);
            if (!fileSystem.FileExists(jsonPath))
            {
                continue;
            }
            try
            {
                JsonDocumentOptions options = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
                List<string> args = [];
                using (JsonDocument doc = JsonDocument.Parse(fileSystem.ReadAllText(jsonPath), options))
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
                                string fileName = profile.OutputNameTemplate.Replace("{title}", cleanTitle);
                                if (custom)
                                {
                                    fileName = $"{Path.ChangeExtension(fileName, null)}.custom{Path.GetExtension(fileName)}";
                                }
                                string newPath = Path.GetDirectoryName(args[i + 1]) is { Length: > 0 } dir ? Path.Combine(dir, fileName) : fileName;
                                logger.Log($"[Naming] {Path.GetFileName(args[i + 1])} -> {fileName}");
                                args[i + 1] = newPath;
                            }
                            break;
                        case "--title":
                            args[i + 1] = profile.MkvTitleTemplate.Replace("{title}", title);
                            break;
                    }
                }
                fileSystem.WriteAllText(jsonPath, JsonSerializer.Serialize(args, _writeOptions));
            }
            catch (Exception ex)
            {
                logger.Log($"[Naming] Error renaming output in {name}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Converts a locally demuxed DTS file to E-AC3 5.1. The input is the raw elementary stream
    /// from the project folder, so no data is read from the (network-hosted) source MKV.
    /// </summary>
    public async Task<string?> ConvertAudioToEac3Async(string inputMkv, string outputEac3Path, CancellationToken cancellationToken = default)
    {
        logger.Log($"[Audio] Checking audio streams for {Path.GetFileName(inputMkv)}...");
        string probeArgs = $"-v error -select_streams a:0 -show_entries stream=codec_name,profile,channels -of csv=p=0 \"{inputMkv}\"";
        string ffprobeExe = Path.Combine(Path.GetDirectoryName(settings.FfmpegExecutable) ?? "", "ffprobe.exe");
        if (!fileSystem.FileExists(ffprobeExe))
        {
            ffprobeExe = "ffprobe.exe";
        }
        (int probeCode, string probeData, _) = await processRunner.RunAsync(ffprobeExe, probeArgs, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (probeCode != 0 || string.IsNullOrWhiteSpace(probeData))
        {
            logger.Log("[Audio] Warning: Could not ffprobe audio streams, falling back to 5.1 EAC3 direct copy/downmix.");
        }
        string[] parts = probeData.Trim().ToLower().Split(',');
        string codec = parts is [{ } token, ..] ? token.Trim() : "";
        int channels = parts is [_, _, { } count, ..] && int.TryParse(count.Trim(), out int ch) ? ch : 6;
        logger.Log($"[Audio] Detected audio: Codec={codec}, Channels={channels}");
        List<string> audioArgs = [];
        if (channels == 8)
        {
            logger.Log("[Audio] 7.1 detected -> Converting to EAC3 5.1 with Gain-Safe Fold...");
            string panFilter = "pan=5.1|FL=FL|FR=FR|FC=1.1*FC|LFE=LFE|BL=0.707*SL+0.707*BL|BR=0.707*SR+0.707*BR";
            audioArgs.AddRange(["-af", panFilter, "-c:a", "eac3", "-b:a", "640k"]);
        }
        else if (channels == 7)
        {
            logger.Log("[Audio] 6.1 detected -> Converting to EAC3 5.1 with Rear-Center fold...");
            string panFilter = "pan=5.1|FL=FL|FR=FR|FC=1.1*FC|LFE=LFE|BL=0.707*SL+0.707*BC|BR=0.707*SR+0.707*BC";
            audioArgs.AddRange(["-af", panFilter, "-c:a", "eac3", "-b:a", "640k"]);
        }
        else
        {
            logger.Log("[Audio] Converting to EAC3 5.1 640k...");
            audioArgs.AddRange(["-c:a", "eac3", "-b:a", "640k", "-ac", "6"]);
        }
        string ffmpegArgs = $"-y -i \"{inputMkv}\" -map 0:a:0 {string.Join(" ", audioArgs)} \"{outputEac3Path}\"";
        (int exitCode, _, string stderr) = await processRunner.RunAsync(
            settings.FfmpegExecutable,
            ffmpegArgs,
            onStderr: line =>
            {
                if (line.Contains("size="))
                {
                    logger.Log($"[FFmpeg] {line}");
                }
            },
            cancellationToken: cancellationToken
        ).ConfigureAwait(false);
        if (exitCode == 0 && fileSystem.FileExists(outputEac3Path))
        {
            logger.Log($"[Audio] Successfully converted to EAC3: {Path.GetFileName(outputEac3Path)}");
            return outputEac3Path;
        }
        logger.Log($"[Audio] Error converting audio with FFmpeg: {stderr}");
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
        if (fileSystem.FileExists(jsonPath))
        {
            try
            {
                JsonDocumentOptions options = new()
                {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true
                };
                using JsonDocument doc = JsonDocument.Parse(fileSystem.ReadAllText(jsonPath), options);
                foreach (JsonElement element in doc.RootElement.EnumerateArray())
                {
                    string arg = element.GetString() ?? "";
                    if (arg.EndsWith(".dts", StringComparison.OrdinalIgnoreCase) ||
                        arg.EndsWith(".dtshd", StringComparison.OrdinalIgnoreCase))
                    {
                        string candidate = Path.Combine(projectMkv3dDir, arg);
                        if (fileSystem.FileExists(candidate))
                        {
                            logger.Log($"[Audio] __MUX_3D_OPTIONS.json references demuxed DTS: {arg}");
                            return candidate;
                        }
                        logger.Log($"[Audio] Warning: JSON references '{arg}' but it is not on disk.");
                    }
                }
            }
            catch (Exception ex)
            {
                logger.Log($"[Audio] Could not parse __MUX_3D_OPTIONS.json: {ex.Message}");
            }
        }
        // Local-only fallback (e.g. JSON already patched by a previous run)
        string? scanned = fileSystem.GetFiles(projectMkv3dDir)
            .Where(f => f.EndsWith(".dts", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".dtshd", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(fileSystem.GetFileLength)
            .FirstOrDefault();
        if (scanned != null)
        {
            logger.Log($"[Audio] Using demuxed DTS found in project folder: {Path.GetFileName(scanned)}");
        }
        return scanned;
    }

    public bool InjectAvsTweak(string projectMkv3dDir)
    {
        string avsFile = Path.Combine(projectMkv3dDir, "__ENCODE_3D_MOVIE.avs");
        if (!fileSystem.FileExists(avsFile))
        {
            logger.Log($"[AVS Patch] File not found: {avsFile}");
            return false;
        }
        string content = fileSystem.ReadAllText(avsFile);
        string tweakLine = "Tweak(sat=1.1, cont=1.05)";
        if (content.Contains(tweakLine))
        {
            logger.Log("[AVS Patch] Tweak already exists in AVS file. Skipping.");
            return true;
        }
        string backupFile = avsFile + ".bak";
        fileSystem.CopyFile(avsFile, backupFile, overwrite: true);
        logger.Log($"[AVS Patch] Created backup at {Path.GetFileName(backupFile)}");
        string newContent = AvsEndRegex().Replace(content, $"{tweakLine}\r\nReturn(last)");
        fileSystem.WriteAllText(avsFile, newContent);
        logger.Log($"[AVS Patch] Injected '{tweakLine}' right before Return(last)!");
        return true;
    }

    public void NeutralizePostProcessCmd(string projectMkv3dDir)
    {
        foreach (string name in _postProcessFiles)
        {
            string targetFile = Path.Combine(projectMkv3dDir, name);
            if (!fileSystem.FileExists(targetFile))
            {
                continue;
            }
            // BD3D puts "start "" ." and "pause" at the end of post-process scripts which pops
            // Explorer and blocks exit!
            fileSystem.WriteAllText(targetFile, "@echo off\r\nREM Neutralized Explorer popup and pause\r\nexit /B 0\r\n");
            logger.Log($"[PostProcess] Neutralized {Path.GetFileName(targetFile)} (disabled 'pause' and Explorer popups).");
        }
    }

    /// <summary>
    /// Extracts audio and/or subtitle tracks (preserving languages, track names, and forced/default flags) from a
    /// donor .mkv or .mks into a local .mks file using the bundled mkvmerge.exe in a single pass.
    /// </summary>
    public async Task<string?> ExtractTracksAsync(
        string donorPath,
        string outputMksPath,
        bool includeAudio,
        bool includeSubtitles,
        IReadOnlyList<int>? subtitleTrackIds = null,
        CancellationToken cancellationToken = default)
    {
        if (!includeAudio && !includeSubtitles)
        {
            return null;
        }
        if (!fileSystem.FileExists(donorPath))
        {
            logger.Log($"[Steal] Donor file not found: '{donorPath}'. Skipping track extraction.");
            return null;
        }
        string mkvmergeExe = Path.Combine(settings.ToolsetDirectory, "mkvmerge.exe");
        if (!fileSystem.FileExists(mkvmergeExe))
        {
            mkvmergeExe = "mkvmerge.exe";
        }
        string what = (includeAudio, includeSubtitles) switch
        {
            (true, true) => "audio + subtitle",
            (true, false) => "audio",
            _ => "subtitle"
        };
        logger.Log($"[Steal] Extracting {what} tracks from '{Path.GetFileName(donorPath)}'...");
        string audioFlag = includeAudio ? "" : " --no-audio";
        string subFlag = includeSubtitles
            ? (subtitleTrackIds is { Count: > 0 } ? $" --subtitle-tracks {string.Join(',', subtitleTrackIds)}" : "")
            : " --no-subtitles";
        string args = $"--no-video --no-chapters --no-attachments --no-global-tags --no-track-tags{audioFlag}{subFlag} -o \"{outputMksPath}\" \"{donorPath}\"";
        int nextMilestone = 10;
        (int exitCode, string stdout, string stderr) = await processRunner.RunAsync(
            mkvmergeExe,
            args,
            onStdout: line =>
            {
                if (line.StartsWith("Progress:", StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(line.AsSpan("Progress:".Length).Trim().TrimEnd('%'), out int percent) &&
                    percent >= nextMilestone)
                {
                    logger.Log($"[Steal] Progress: {percent}%");
                    nextMilestone = (percent / 10 + 1) * 10;
                }
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (exitCode <= 1 && fileSystem.FileExists(outputMksPath))
        {
            logger.Log($"[Steal] Extracted {what} tracks to '{Path.GetFileName(outputMksPath)}'.");
            return outputMksPath;
        }
        string detail = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
        logger.Log($"[Steal] mkvmerge failed (exit {exitCode}): {detail.Trim()}");
        return null;
    }

    public bool UpdateMuxJson(
        string projectMkv3dDir,
        string? customEac3File,
        IReadOnlyList<string> companionFiles,
        IReadOnlyList<string>? extraMksFiles = null,
        bool stolenHasAudio = false)
    {
        bool anyUpdated = false;
        foreach (string targetName in _mux3DOptionsFiles)
        {
            string jsonPath = Path.Combine(projectMkv3dDir, targetName);
            if (!fileSystem.FileExists(jsonPath))
            {
                continue;
            }
            if (this.UpdateSingleMuxJson(jsonPath, projectMkv3dDir, customEac3File, companionFiles, extraMksFiles, stolenHasAudio))
            {
                anyUpdated = true;
            }
        }
        return anyUpdated;
    }

    [GeneratedRegex(@"(?m)^Return\(last\)")]
    private static partial Regex AvsEndRegex();

    [GeneratedRegex(@"(@echo on\r?\n)[\s\S]*?(\r?\n@echo off)", RegexOptions.IgnoreCase)]
    private static partial Regex EncodeCmdBlockRegex();

    [GeneratedRegex(@"(<Name>ENCODER</Name>\s*<String>)[\s\S]*?(</String>)", RegexOptions.IgnoreCase)]
    private static partial Regex EncoderTagRegex();

    [GeneratedRegex(@"(<Name>ENCODER_SETTINGS</Name>\s*<String>)[\s\S]*?(</String>)", RegexOptions.IgnoreCase)]
    private static partial Regex EncoderSettingsTagRegex();

    public async Task<bool> ProbeNvencAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            const string args = "-v error -f lavfi -i color=black:s=256x256:d=0.1 -frames:v 1 -pix_fmt p010le -c:v hevc_nvenc -f null -";
            (int exitCode, _, _) = await processRunner.RunAsync(settings.FfmpegExecutable, args, cancellationToken: cancellationToken).ConfigureAwait(false);
            return exitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Rewrites a BD3D2MK3D project generated with <c>use_x265=1</c> to encode 10-bit HEVC via ffmpeg's <c>hevc_nvenc</c>,
    /// and updates <c>__TAGS_3D.xml</c> + the video track name in <c>__MUX_3D_OPTIONS.json</c> so MediaInfo stays accurate.
    /// </summary>
    public void PatchForNvenc(string projectMkv3dDir, int cq)
    {
        string nvencFlags = $"-pix_fmt p010le -c:v hevc_nvenc -preset p7 -tune hq -rc vbr -cq {cq} -b:v 0 -multipass fullres -spatial-aq 1 -aq-strength 7 -temporal-aq 1 -rc-lookahead 32 -bf 4 -b_ref_mode middle";
        string ffmpegCmd = $"\"{settings.FfmpegExecutable}\" -y -i \"__ENCODE_3D_MOVIE.avs\" {nvencFlags} -f hevc \"MKV3D_3D.265\"";
        string encodeCmdPath = Path.Combine(projectMkv3dDir, "__ENCODE_3D.cmd");
        if (fileSystem.FileExists(encodeCmdPath))
        {
            string cmd = fileSystem.ReadAllText(encodeCmdPath);
            string patchedCmd = EncodeCmdBlockRegex().Replace(cmd, $"$1{ffmpegCmd}$2", 1);
            if (!ReferenceEquals(cmd, patchedCmd) && patchedCmd != cmd)
            {
                fileSystem.WriteAllText(encodeCmdPath, patchedCmd);
                logger.Log($"[NVENC] Rewrote __ENCODE_3D.cmd for 10-bit hevc_nvenc (CQ {cq}).");
            }
            else
            {
                logger.Log("[NVENC] Warning: Could not locate encoder block in __ENCODE_3D.cmd.");
            }
        }
        foreach (string tagsName in _tags3DFiles)
        {
            string tagsPath = Path.Combine(projectMkv3dDir, tagsName);
            if (!fileSystem.FileExists(tagsPath))
            {
                continue;
            }
            string xml = fileSystem.ReadAllText(tagsPath);
            string escapedFlags = System.Security.SecurityElement.Escape(nvencFlags) ?? nvencFlags;
            string patchedXml = EncoderTagRegex().Replace(xml, "$1ffmpeg (hevc_nvenc)$2", 1);
            patchedXml = EncoderSettingsTagRegex().Replace(patchedXml, $"$1{escapedFlags}$2", 1);
            if (patchedXml != xml)
            {
                fileSystem.WriteAllText(tagsPath, patchedXml);
            }
        }
        foreach (string muxName in _mux3DOptionsFiles)
        {
            string jsonPath = Path.Combine(projectMkv3dDir, muxName);
            if (!fileSystem.FileExists(jsonPath))
            {
                continue;
            }
            try
            {
                JsonDocumentOptions options = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
                List<string> args = [];
                using (JsonDocument doc = JsonDocument.Parse(fileSystem.ReadAllText(jsonPath), options))
                {
                    args.AddRange(doc.RootElement.EnumerateArray().Select(element => element.GetString() ?? ""));
                }
                int videoIdx = args.FindIndex(a => a.EndsWith(".265", StringComparison.OrdinalIgnoreCase));
                if (videoIdx >= 2)
                {
                    for (int i = videoIdx - 2; i >= 0; i -= 2)
                    {
                        if (!_trackOptionSwitches.Contains(args[i]))
                        {
                            break;
                        }
                        if (args[i].Equals("--track-name", StringComparison.OrdinalIgnoreCase))
                        {
                            args[i + 1] = $"0:3D Half-T&B (HEVC 10-bit NVENC CQ {cq})";
                            break;
                        }
                    }
                    fileSystem.WriteAllText(jsonPath, JsonSerializer.Serialize(args, _writeOptions));
                }
            }
            catch (Exception ex)
            {
                logger.Log($"[NVENC] Warning: Could not update track name in {muxName}: {ex.Message}");
            }
        }
    }

    private bool UpdateSingleMuxJson(
        string jsonPath,
        string projectMkv3dDir,
        string? customEac3File,
        IReadOnlyList<string> companionFiles,
        IReadOnlyList<string>? extraMksFiles,
        bool stolenHasAudio)
    {
        try
        {
            string rawJson = fileSystem.ReadAllText(jsonPath);
            JsonDocumentOptions options = new()
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            };
            using JsonDocument doc = JsonDocument.Parse(rawJson, options);
            List<string> arguments = [.. doc.RootElement.EnumerateArray().Select(element => element.GetString() ?? "")];
            // If we converted DTS to custom EAC3, strip original DTS/DTS-HD audio tracks from arguments
            if (!string.IsNullOrEmpty(customEac3File) && fileSystem.FileExists(customEac3File))
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
                    logger.Log($"[Mux Options] Stripping original DTS track '{arguments[dtsIndex]}' and its switches from {Path.GetFileName(jsonPath)}.");
                    arguments.RemoveRange(startIndex, removeCount);
                }
            }
            // Check if any existing audio tracks remain in JSON (or if we're injecting stolen audio)
            bool hasAudioTrack = stolenHasAudio;
            if (!hasAudioTrack)
            {
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
            }
            // If we converted DTS to custom EAC3, inject it
            if (!string.IsNullOrEmpty(customEac3File) && fileSystem.FileExists(customEac3File))
            {
                string destEac3 = Path.Combine(projectMkv3dDir, Path.GetFileName(customEac3File));
                if (!destEac3.Equals(customEac3File, StringComparison.OrdinalIgnoreCase))
                {
                    fileSystem.CopyFile(customEac3File, destEac3, overwrite: true);
                }
                logger.Log($"[Mux Options] Injecting converted EAC3 audio: {Path.GetFileName(destEac3)}");
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
            // project folder that BD3D2MK3D missed or failed to rename (e.g. *.ec3, *.ac3, *.dts)
            if (!hasAudioTrack)
            {
                List<string> audioCandidates = [];
                foreach (string f in fileSystem.GetFiles(projectMkv3dDir))
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
                    logger.Log($"[Mux Options] Auto-recovered orphaned demuxed audio: {Path.GetFileName(primaryAudio)}");
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
                if (!fileSystem.FileExists(companion))
                {
                    continue;
                }
                string ext = Path.GetExtension(companion).ToLowerInvariant();
                string destCompanion = Path.Combine(projectMkv3dDir, Path.GetFileName(companion));
                if (!destCompanion.Equals(companion, StringComparison.OrdinalIgnoreCase))
                {
                    fileSystem.CopyFile(companion, destCompanion, overwrite: true);
                }
                logger.Log($"[Mux Options] Ingesting companion file into mux: {Path.GetFileName(destCompanion)}");
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
            if (extraMksFiles is not null)
            {
                foreach (string extraMks in extraMksFiles)
                {
                    if (string.IsNullOrEmpty(extraMks) || !fileSystem.FileExists(extraMks))
                    {
                        continue;
                    }
                    string destMks = Path.Combine(projectMkv3dDir, Path.GetFileName(extraMks));
                    if (!destMks.Equals(extraMks, StringComparison.OrdinalIgnoreCase))
                    {
                        fileSystem.CopyFile(extraMks, destMks, overwrite: true);
                    }
                    logger.Log($"[Mux Options] Injecting stolen tracks: {Path.GetFileName(destMks)}");
                    arguments.Add(Path.GetFileName(destMks));
                }
            }
            string outputJson = JsonSerializer.Serialize(arguments, _writeOptions);
            fileSystem.WriteAllText(jsonPath, outputJson);
            logger.Log($"[Mux Options] Successfully updated {Path.GetFileName(jsonPath)} with audio and companion tracks!");
            return true;
        }
        catch (Exception ex)
        {
            logger.Log($"[Mux Options] Error updating JSON options {Path.GetFileName(jsonPath)}: {ex.Message}");
            return false;
        }
    }
}
