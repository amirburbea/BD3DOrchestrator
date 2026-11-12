using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace BD3DOrchestrator;

/// <summary>
/// Runs a <see cref="Job"/>: inspects and detects the input, drives BD3D2MK3D, patches the project and starts the encode.
/// Every await here uses ConfigureAwait(false): none of this code touches the UI directly (logging is marshalled by the
/// supplied logger), so only the window that kicks a job off awaits on the UI context.
/// </summary>
public class JobRunner(AppSettings settings, Action<string> logger)
{
    /// <summary>Inspects the input and fills in the job's title, detection result and suggested options.</summary>
    public async Task LoadAsync(Job job, string path, CancellationToken cancellationToken = default)
    {
        job.InputPath = path;
        logger($"Analyzing: {path}...");
        MediaInspector inspector = new(settings.ToolsetDirectory);
        MediaInspectionResult inspection = await inspector.InspectAsync(path, cancellationToken).ConfigureAwait(false);
        MovieInfo detected = await MovieDetector.DetectAsync(path).ConfigureAwait(false);
        job.Inspection = inspection;
        string displayName = !string.IsNullOrEmpty(detected.Title) ? detected.Title : inspection.MovieTitle;
        if (detected.Year > 0 && !displayName.Contains(detected.Year.ToString()))
        {
            displayName = $"{displayName} ({detected.Year})";
        }
        job.MovieTitle = displayName;
        job.DetectionReason = $"Detection: {detected.Reason}";
        // Tweak rule: Pre-2022 and Animation
        if (detected.IsAnimation && detected.Year is < 2022 and not 0)
        {
            job.ApplyAvsTweak = true;
            logger($"★ Animation pre-2022 detected ({detected.Year}) -> Auto-enabled AVS OLED Tweak!");
        }
        else if (detected.IsAnimation && detected.Year >= 2022)
        {
            job.ApplyAvsTweak = false;
            logger($"Animation from {detected.Year} detected -> Kept AVS OLED Tweak OFF (newer HDR/high contrast master).");
        }
        else
        {
            job.ApplyAvsTweak = false;
        }
        if (inspection.HasDtsAudio)
        {
            logger("Detected DTS audio stream in MKV -> Will convert to EAC3 5.1.");
        }
        else if (!inspection.HasDolbyAudio && inspection.CompanionFiles.Count > 0)
        {
            logger("MKV has no embedded audio, but companion tracks were found (e.g. .ec3/.mka).");
        }
        if (inspection.CompanionFiles.Count > 0)
        {
            logger($"Found {inspection.CompanionFiles.Count} matching companion file(s) in folder:");
            foreach (string file in inspection.CompanionFiles)
            {
                logger($"  - {Path.GetFileName(file)}");
            }
        }
        logger("Ready. Click 'Auto-Run Pipeline' to perform the complete workflow unattended.");
    }

    /// <summary>Runs the full pipeline. Returns true only when the encode launcher was started.</summary>
    public async Task<bool> RunAsync(Job job, CancellationToken cancellationToken)
    {
        if (job.Inspection is not { } inspection || string.IsNullOrEmpty(inspection.MkvPath))
        {
            logger("[Error] No movie loaded; nothing to run.");
            return false;
        }
        // Snapshot: later edits to the job (or a new job replacing it) can't change a run in progress.
        string titleToUse = job.EffectiveTitle;
        bool convertDts = job.ConvertDtsToEac3;
        bool applyAvsTweak = job.ApplyAvsTweak;
        bool ingestCompanions = job.IngestCompanions;
        bool killMkvinfoHangs = job.KillMkvinfoHangs;
        EncodeProfile profile;
        try
        {
            string profilePath = settings.ResolvedProfilePath;
            profile = EncodeProfile.Load(profilePath);
            logger($"[Profile] {profilePath} (hash {profile.Hash}, {profile.Settings.Count} settings) -> output '{profile.OutputNameTemplate}'");
        }
        catch (Exception ex)
        {
            logger($"[Error] Cannot load encoding profile, aborting before anything runs: {ex.Message}");
            return false;
        }
        string? resolvedProject = null;
        // DataHandler an isolated temp job sandbox so BD3D2MK3D's original install folder is never touched or modified.
        // This guarantees complete isolation between multiple parallel instances or manual runs.
        string jobTempDir = Path.Combine(Path.GetTempPath(), "BD3DOrchestrator", $"Job_{Guid.NewGuid():N}");
        Directory.CreateDirectory(jobTempDir);
        this.CreatePluginTclHook(jobTempDir, inspection.MkvPath, titleToUse, profile);
        try
        {
            logger($"[Step 1] Launching BD3D2MK3D for '{titleToUse}' (Sandbox: {Path.GetFileName(jobTempDir)})...");
            if (Process.Start(startInfo: new(settings.BD3DExecutable) { WorkingDirectory = jobTempDir, UseShellExecute = true }) is not { Id: int pid } bd3dProcess)
            {
                logger("Failed to start BD3D2MK3D.");
                return false;
            }
            logger($"[Step 1] Demuxing streams via BD3D2MK3D (PID: {pid}). Monitoring for completion...");
            while (!bd3dProcess.HasExited && !cancellationToken.IsCancellationRequested)
            {
                foreach (Process proc in Process.GetProcessesByName("mkvinfo").Where((proc) => DateTime.Now - proc.StartTime > TimeSpan.FromSeconds(10)))
                {
                    try
                    {
                        proc.Kill();
                        logger("⚡ [Watchdog] Terminated hung mkvinfo.exe process to let BD3D2MK3D proceed.");
                    }
                    catch { }
                }
                string resolvedFile = Path.Combine(jobTempDir, "resolved.txt");
                if (resolvedProject is null && File.Exists(resolvedFile))
                {
                    try
                    {
                        resolvedProject = File.ReadAllText(resolvedFile).Trim();
                    }
                    catch { }
                }
                // Check if BD3D finished generating the project files
                string projFolder = resolvedProject ?? this.FindProjectMkv3dFolder(titleToUse);
                if (!string.IsNullOrEmpty(projFolder))
                {
                    string logFile = Path.Combine(projFolder, "BD3D2MK3D.log");
                    if (File.Exists(logFile))
                    {
                        try
                        {
                            string logText = File.ReadAllText(logFile);
                            if (logText.Contains("Successfully done", StringComparison.OrdinalIgnoreCase))
                            {
                                logger("[Step 1] BD3D2MK3D demux & project generation completed successfully!");
                                try
                                {
                                    if (!bd3dProcess.HasExited)
                                    {
                                        bd3dProcess.Kill();
                                        logger($"[Step 1] Cleanly terminated own BD3D2MK3D instance (PID: {pid}).");
                                    }
                                }
                                catch { }
                                break;
                            }
                        }
                        catch
                        {
                        }
                    }
                }
                await Task.Delay(2000, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            try
            {
                if (Directory.Exists(jobTempDir))
                {
                    Directory.Delete(jobTempDir, recursive: true);
                }
            }
            catch
            {
            }
        }
        string projectFolder = resolvedProject ?? this.FindProjectMkv3dFolder(titleToUse);
        if (string.IsNullOrEmpty(projectFolder) || !Directory.Exists(projectFolder))
        {
            logger($"[Error] Could not find generated project folder for '{titleToUse}'. Aborting pipeline.");
            return false;
        }
        logger($"[Step 2] Ingesting Audio & Patching Project in: {projectFolder}");
        OrchestratorPipeline pipeline = new(settings.FfmpegExecutable, logger);
        // Convert audio if DTS detected
        string? customEac3 = null;
        if (convertDts && inspection.HasDtsAudio)
        {
            if (pipeline.FindDemuxedDtsFile(projectFolder) is not { } localDts)
            {
                logger("[Audio] No demuxed DTS file found in project folder; skipping conversion (original track left in mux).");
            }
            else
            {
                string outEac3 = Path.Combine(projectFolder, $"{titleToUse}_converted.ec3");
                customEac3 = await pipeline.ConvertAudioToEac3Async(localDts, outEac3, cancellationToken).ConfigureAwait(false);
            }
        }
        pipeline.ApplyOutputNaming(projectFolder, titleToUse, profile);
        try
        {
            File.WriteAllText(Path.Combine(projectFolder, "encode_profile.txt"), $"hash={profile.Hash}{Environment.NewLine}source={profile.SourcePath}{Environment.NewLine}");
        }
        catch { }
        // AVS Script OLED contrast/saturation injection
        if (applyAvsTweak)
        {
            pipeline.InjectAvsTweak(projectFolder);
        }
        // Neutralize pause and start "" in __POSTPROCESS_3D.cmd
        pipeline.NeutralizePostProcessCmd(projectFolder);
        // Update JSON mux options with companion files & EAC3
        if (ingestCompanions)
        {
            pipeline.UpdateMuxJson(projectFolder, customEac3, inspection.CompanionFiles);
        }
        // 3. Start Encoding & Muxing
        logger("[Step 3] Launching background x264/x265 encode and final MKV mux...");
        string launcherCmd = Path.Combine(projectFolder, "__ENCODE_3D_LAUNCHER.cmd");
        if (!File.Exists(launcherCmd))
        {
            launcherCmd = Path.Combine(projectFolder, "__ENCODE_3D.cmd");
        }
        if (File.Exists(launcherCmd))
        {
            ProcessStartInfo encodePsi = new()
            {
                FileName = launcherCmd,
                WorkingDirectory = projectFolder,
                UseShellExecute = true
            };
            Process.Start(encodePsi);
            logger("🎉 Pipeline complete! Encode is running in low CPU priority background window.");
            return true;
        }
        else
        {
            logger($"[Error] Batch launcher not found in {projectFolder}");
            return false;
        }
    }

    private void CreatePluginTclHook(string jobTempDir, string mkvPath, string movieTitle, EncodeProfile profile)
    {
        try
        {
            string normalizedMkv = mkvPath.Replace("\\", "/");
            string projQ = EncodeProfile.TclQuote(settings.ProjectDirectory.Replace("\\", "/"));
            string outQ = EncodeProfile.TclQuote(settings.OutputDirectory.Replace("\\", "/"));
            int outUsed = string.IsNullOrWhiteSpace(settings.OutputDirectory) ? 0 : 1;
            string escapedTitle = movieTitle.Replace("\"", "\\\"").Replace("[", "\\[").Replace("]", "\\]");
            string hook = $@"
# BD3DOrchestrator Auto-Run Plugin Hook
proc ::GetMkvWritingApplication {{mkvfile}} {{
    return ""MakeMKV v1.17.8 win(x64-release)""
}}

if {{[info commands ::orig_tk_messageBox] == """" && [info commands ::tk_messageBox] != """" && [info commands ::orig_tk_messageBox] != ""::tk_messageBox""}} {{
    rename ::tk_messageBox ::orig_tk_messageBox
    proc ::tk_messageBox {{args}} {{
        set str [string tolower [join $args "" ""]]
        if {{[string first ""retrycancel"" $str] != -1}} {{
            return ""cancel""
        }}
        if {{[string first ""abortretryignore"" $str] != -1 || [string first ""free space"" $str] != -1 || [string first ""disc space"" $str] != -1}} {{
            return ""ignore""
        }}
        if {{[string first ""yesno"" $str] != -1}} {{
            return ""yes""
        }}
        if {{[string first ""okcancel"" $str] != -1}} {{
            return ""ok""
        }}
        return ""ok""
    }}
}}

if {{[info commands ::orig_exec] == """" && [info commands ::exec] != """" && [info commands ::orig_exec] != ""::exec""}} {{
    rename ::exec ::orig_exec
    proc ::exec {{args}} {{
        set str [join $args "" ""]
        if {{[string first ""cmd /c start"" $str] != -1}} {{
            return """"
        }}
        return [uplevel 1 [list ::orig_exec {{*}}$args]]
    }}
}}

set ::config(endaction) ""close command prompt""
set ::endaction ""close command prompt""

# --- Shared encoding profile (hash {profile.Hash}) ---
{profile.ToTclBlock()}
# --- Per-machine paths ---
set ::projdir {projQ}
catch {{file mkdir $::projdir}}
set ::config(projdir) $::projdir
set ::mkvdir {outQ}
set ::mkvdirused {outUsed}
set ::mkvdirusedsession {outUsed}
if {{{outUsed} == 1}} {{ catch {{file mkdir $::mkvdir}} }}

after 150 {{
    set res [catch {{OpenMKV ""{normalizedMkv}""}} err]
    if {{$res != 0}} {{
        puts stderr ""OpenMKV failed: $err""
        return
    }}
    set ::moviename ""{escapedTitle}""
    .nbf3.f1.p configure -values [list ""{escapedTitle}""]
    .nbf3.f1.p set ""{escapedTitle}""
    update
    after 400 {{
        set ::moviename ""{escapedTitle}""
        GenerateAvs
        catch {{
            set fh [open [file join [pwd] resolved.txt] w]
            puts $fh [file nativename [file join $::projdir [string map $::ILLEGALCHARSMAP $::moviename] MKV3D]]
            close $fh
        }}
        after 1000 {{
            exit
        }}
    }}
}}
";
            string targetPath = Path.Combine(jobTempDir, "plugin.tcl");
            File.WriteAllText(targetPath, hook);
            logger($"Created isolated sandbox hook: Auto-opens '{Path.GetFileName(mkvPath)}', sets Title = '{movieTitle}', suppresses prompts & Explorer popup.");
        }
        catch (Exception ex)
        {
            logger($"Warning: Could not create sandbox hook: {ex.Message}");
        }
    }

    public static void CleanupStaleTempDirectories()
    {
        try
        {
            string baseTemp = Path.Combine(Path.GetTempPath(), "BD3DOrchestrator");
            if (Directory.Exists(baseTemp))
            {
                DirectoryInfo dirInfo = new(baseTemp);
                foreach (DirectoryInfo subDir in dirInfo.GetDirectories())
                {
                    // Clean up any stale temp directories older than 12 hours (e.g. from previous crashes)
                    if (DateTime.Now - subDir.CreationTime > TimeSpan.FromHours(12))
                    {
                        try
                        {
                            subDir.Delete(recursive: true);
                        }
                        catch
                        {
                        }
                    }
                }
            }
        }
        catch
        {
        }
    }

    private string FindProjectMkv3dFolder(string movieTitle)
    {
        // Only folder names BD3D2MK3D could have produced for exactly this title. Never a wildcard match: a short title
        // like "Up" would match another movie's project, and we would then patch and encode the wrong one.
        foreach (string name in new[] { movieTitle, EncodeProfile.SanitizeFileName(movieTitle) })
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }
            string candidate = Path.Combine(settings.ProjectDirectory, name, "MKV3D");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }
        return string.Empty;
    }
}
