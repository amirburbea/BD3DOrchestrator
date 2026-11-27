using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SkiaSharp;

namespace Sub3DConverter;

public sealed record ConversionOptions(
    string InputXml,
    string OutputXml,
    bool SideBySide,
    int LeftFirst,
    int AddDepth,
    string Filter,
    bool OnlyForced,
    bool VerifySubs,
    string AppName = "BD3D2MK3D",
    string AppVersion = "1.40",
    int MaxThreads = 0);

public sealed record ConversionResult(
    int NumConverted,
    int NumErrors,
    int NumWarnings,
    string Message,
    string LogDetails);

public static class SubtitleConverter
{
    public static ConversionResult Convert(ConversionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        string? outDir = Path.GetDirectoryName(Path.GetFullPath(options.OutputXml));
        if (string.IsNullOrEmpty(outDir))
        {
            return new ConversionResult(0, 1, 0, "Invalid output directory!", string.Empty);
        }

        Directory.CreateDirectory(outDir);

        if (!File.Exists(options.InputXml))
        {
            return new ConversionResult(0, 1, 0, $"Can't open \"{Path.GetFileName(options.InputXml)}\" for read!", string.Empty);
        }

        string inDir = Path.GetDirectoryName(Path.GetFullPath(options.InputXml))!;
        string[] lines = File.ReadAllLines(options.InputXml, Encoding.UTF8);

        int frameWidth = 1920;
        int frameHeight = 1080;
        int numSubs = 0;
        int numConverted = 0;
        bool inUnforced = false;

        List<OutputSegment> segments = new(lines.Length);
        List<GraphicJob> jobs = [];
        StringBuilder currentEventPrefix = new();
        bool inEvent = false;

        foreach (string line in lines)
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith("<Event ", StringComparison.Ordinal))
            {
                inEvent = true;
                inUnforced = false;
                if (options.OnlyForced)
                {
                    string forced = GetAttribute(trimmed, "Forced").ToLowerInvariant();
                    if (forced is "true" or "1")
                    {
                        currentEventPrefix.Clear();
                        currentEventPrefix.Append(line).Append('\n');
                    }
                    else
                    {
                        inUnforced = true;
                    }
                }
                else
                {
                    currentEventPrefix.Clear();
                    currentEventPrefix.Append(line).Append('\n');
                }
            }
            else if (trimmed.StartsWith("<Graphic ", StringComparison.Ordinal))
            {
                numSubs++;
                if (inUnforced)
                {
                    continue;
                }

                numConverted++;
                int width = ParseIntAttr(trimmed, "Width");
                int height = ParseIntAttr(trimmed, "Height");
                int x = ParseIntAttr(trimmed, "X");
                int y = ParseIntAttr(trimmed, "Y");
                string depthStr = GetAttribute(trimmed, "Depth");
                int origDepth = string.IsNullOrEmpty(depthStr) ? 0 : int.Parse(depthStr, CultureInfo.InvariantCulture);

                if (width == frameWidth && x < 0) x = 0;
                if (height == frameHeight && y < 0) y = 0;

                int gt = trimmed.IndexOf('>');
                int endTag = trimmed.IndexOf("</Graphic>", StringComparison.Ordinal);
                string pngFn = trimmed.Substring(gt + 1, endTag - gt - 1);

                int depth = (origDepth + options.AddDepth) * options.LeftFirst;
                int localWarnings = 0;
                string? depthWarning = null;
                if (width < frameWidth && width + (Math.Abs(depth) / 2) > frameWidth)
                {
                    int newDepth = (frameWidth - width) * 2;
                    if (depth < 0) newDepth *= -1;
                    depthWarning = $"Warning: Subtitle #{numSubs}: The depth ({depth}) is too great for the width of the subtitle ({width}).  Depth reduced to {newDepth}.";
                    localWarnings++;
                }

                int newWidth, newHeight, finalWidth, finalHeight, newX, newY;
                int x1, y1, x2, y2;

                if (options.SideBySide)
                {
                    newWidth = width + frameWidth - depth;
                    newHeight = height;
                    x1 = 0;
                    y1 = 0;
                    x2 = newWidth - width;
                    y2 = 0;
                    finalWidth = (newWidth + 1) / 2;
                    finalHeight = height;
                    newX = (int)Math.Round((x + (depth / 2.0)) / 2.0, MidpointRounding.AwayFromZero);
                    newY = y;
                }
                else
                {
                    newWidth = width + Math.Abs(depth);
                    newHeight = height + frameHeight;
                    if (depth < 0)
                    {
                        x1 = 0;
                        y1 = 0;
                        x2 = newWidth - width;
                        y2 = newHeight - height;
                    }
                    else
                    {
                        x1 = newWidth - width;
                        y1 = 0;
                        x2 = 0;
                        y2 = newHeight - height;
                    }
                    finalWidth = newWidth;
                    finalHeight = (newHeight + 1) / 2;
                    newX = x - (Math.Abs(depth) / 2);
                    newY = y / 2;
                }

                int resizeWidth = finalWidth;
                int resizeHeight = finalHeight;
                int cropX = 0;
                if (newX < 0)
                {
                    cropX = -newX;
                    finalWidth += newX;
                    newX = 0;
                    if (finalWidth > frameWidth)
                    {
                        finalWidth = frameWidth;
                    }
                }

                string outPng = $"sub_{numSubs:D4}.png";
                string graphicLine = $"      <Graphic Width=\"{finalWidth}\" Height=\"{finalHeight}\" X=\"{newX}\" Y=\"{newY}\" OrigDepth=\"{origDepth}\">{outPng}</Graphic>\n";

                GraphicJob job = new(
                    Index: numSubs,
                    InputPngPath: Path.Combine(inDir, pngFn),
                    OutputPngPath: Path.Combine(outDir, outPng),
                    InputFileName: pngFn,
                    NewWidth: newWidth,
                    NewHeight: newHeight,
                    X1: x1,
                    Y1: y1,
                    X2: x2,
                    Y2: y2,
                    ResizeWidth: resizeWidth,
                    ResizeHeight: resizeHeight,
                    CropX: cropX,
                    FinalWidth: finalWidth,
                    FinalHeight: finalHeight,
                    InitialWarnings: localWarnings,
                    DepthWarning: depthWarning);

                jobs.Add(job);
                if (inEvent)
                {
                    currentEventPrefix.Append(graphicLine);
                }
                else
                {
                    segments.Add(new OutputSegment(graphicLine, null));
                }
            }
            else if (trimmed == "</Event>")
            {
                inEvent = false;
                if (inUnforced)
                {
                    continue;
                }

                currentEventPrefix.Append(line).Append('\n');
                GraphicJob? lastJob = jobs.Count > 0 ? jobs[^1] : null;
                segments.Add(new OutputSegment(currentEventPrefix.ToString(), lastJob));
                currentEventPrefix.Clear();
            }
            else if (trimmed.StartsWith("<Format ", StringComparison.Ordinal))
            {
                string videoFormat = GetAttribute(trimmed, "VideoFormat");
                if (videoFormat is "1080p" or "1080i")
                {
                    frameWidth = 1920;
                    frameHeight = 1080;
                }
                else if (videoFormat is "720p" or "720i")
                {
                    frameWidth = 1280;
                    frameHeight = 720;
                }
                segments.Add(new OutputSegment(line + "\n", null));
            }
            else
            {
                if (inEvent && !inUnforced)
                {
                    currentEventPrefix.Append(line).Append('\n');
                }
                else if (!inEvent)
                {
                    segments.Add(new OutputSegment(line + "\n", null));
                }
            }
        }

        SKSamplingOptions sampling = ResolveSampling(options.Filter);
        int totalWarnings = 0;

        ParallelOptions parallelOptions = new()
        {
            MaxDegreeOfParallelism = options.MaxThreads > 0 ? options.MaxThreads : Environment.ProcessorCount
        };

        Parallel.ForEach(jobs, parallelOptions, job =>
        {
            int w = job.InitialWarnings;
            bool ok = ProcessGraphic(job, sampling, options.VerifySubs, out string? errorMessage);
            if (!ok)
            {
                w++;
                job.ErrorMessage = errorMessage;
            }
            job.Success = ok;
            Interlocked.Add(ref totalWarnings, w);
        });

        StringBuilder outBuilder = new(lines.Length * 48);
        StringBuilder logBuilder = new();

        foreach (GraphicJob job in jobs)
        {
            if (job.DepthWarning is not null)
            {
                logBuilder.AppendLine(job.DepthWarning);
            }
            if (job.ErrorMessage is not null)
            {
                logBuilder.AppendLine(job.ErrorMessage);
                logBuilder.AppendLine("The output stereoscopic PNG image may have been created, but it is not included in the XML file.");
            }
        }

        foreach (OutputSegment seg in segments)
        {
            if (seg.AssociatedJob is { Success: false })
            {
                outBuilder.Append("   <!-- ERROR\n");
                outBuilder.Append(seg.Text);
                outBuilder.Append("   -->\n");
            }
            else
            {
                outBuilder.Append(seg.Text);
            }
        }

        string summaryBlock =
            $"Input file:  {Path.GetFileName(options.InputXml)}\n" +
            $"Stereoscopy mode:  {(options.SideBySide ? "Side by Side" : "Top & Bottom")} ({(options.LeftFirst == 1 ? "left" : "right")} view first)\n" +
            $"Additional or fixed depth:  {options.AddDepth}\n" +
            $"Resize filter:  {options.Filter}\n" +
            $"Number of subtitles processed:  {numConverted}";

        logBuilder.AppendLine(summaryBlock);

        outBuilder.Append("<!--\n");
        outBuilder.Append($"Subtitles converted to 3D by {options.AppName} v{options.AppVersion}\n");
        outBuilder.Append(summaryBlock);
        outBuilder.Append("\n-->\n");

        File.WriteAllText(options.OutputXml, outBuilder.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        int numErrors = (numConverted == 0 && options.OnlyForced) ? 1 : 0;
        string msg;
        if (numErrors == 0)
        {
            string forcedText = options.OnlyForced ? "forced " : string.Empty;
            string modeText = options.SideBySide ? "SBS" : "T&B";
            string warnText = totalWarnings > 0 ? $" with {totalWarnings} warnings!" : ".  No errors.  :-)";
            msg = $"Converted {numConverted} {forcedText}subtitles to 3D-{modeText}{warnText}";
        }
        else
        {
            msg = $"{numErrors} ERRORS converting {numConverted} subtitles to 3D!";
        }

        return new ConversionResult(numConverted, numErrors, totalWarnings, msg, logBuilder.ToString());
    }

    private static bool ProcessGraphic(GraphicJob job, SKSamplingOptions sampling, bool verifySubs, out string? errorMessage)
    {
        errorMessage = null;
        try
        {
            using SKBitmap? src = SKBitmap.Decode(job.InputPngPath);
            if (src is null)
            {
                errorMessage = $"Error converting \"{job.InputFileName}\" to 3D: Failed to decode PNG.";
                return false;
            }

            if (verifySubs && IsMonochromeOverBlack(src))
            {
                errorMessage = $"Error verifying input PNG \"{job.InputFileName}\": Image is monochrome.";
                return false;
            }

            SKImageInfo compInfo = new(job.NewWidth, job.NewHeight, SKColorType.Bgra8888, SKAlphaType.Premul);
            using SKSurface compSurface = SKSurface.Create(compInfo);
            compSurface.Canvas.Clear(SKColors.Transparent);
            compSurface.Canvas.DrawBitmap(src, job.X1, job.Y1);
            compSurface.Canvas.DrawBitmap(src, job.X2, job.Y2);
            using SKImage compImage = compSurface.Snapshot();

            SKImageInfo resizeInfo = new(job.ResizeWidth, job.ResizeHeight, SKColorType.Bgra8888, SKAlphaType.Premul);
            using SKSurface resizeSurface = SKSurface.Create(resizeInfo);
            resizeSurface.Canvas.Clear(SKColors.Transparent);
            using SKPaint paint = new() { BlendMode = SKBlendMode.Src };
            resizeSurface.Canvas.DrawImage(compImage, new SKRect(0, 0, job.ResizeWidth, job.ResizeHeight), sampling, paint);
            using SKImage resizedImage = resizeSurface.Snapshot();

            using SKImage finalImage = job.CropX > 0
                ? resizedImage.Subset(new SKRectI(job.CropX, 0, job.CropX + job.FinalWidth, job.FinalHeight))!
                : resizedImage;

            using SKData encoded = finalImage.Encode(SKEncodedImageFormat.Png, 100);
            using FileStream fs = File.Create(job.OutputPngPath);
            encoded.SaveTo(fs);
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = $"Error converting \"{job.InputFileName}\" to 3D: {ex.Message}";
            return false;
        }
    }

    private static bool IsMonochromeOverBlack(SKBitmap bitmap)
    {
        uint? firstRgb = null;
        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                SKColor c = bitmap.GetPixel(x, y);
                byte r = (byte)((c.Red * c.Alpha + 127) / 255);
                byte g = (byte)((c.Green * c.Alpha + 127) / 255);
                byte b = (byte)((c.Blue * c.Alpha + 127) / 255);
                uint rgb = ((uint)r << 16) | ((uint)g << 8) | b;
                if (firstRgb is null)
                {
                    firstRgb = rgb;
                }
                else if (firstRgb.Value != rgb)
                {
                    return false;
                }
            }
        }
        return true;
    }

    public static SKSamplingOptions ResolveSampling(string filter) => filter.Trim().ToLowerInvariant() switch
    {
        "catrom" or "bicubic" or "cubic" => new SKSamplingOptions(SKCubicResampler.CatmullRom),
        "bilinear" or "triangle" or "linear" => new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None),
        "point" or "nearest" or "box" => new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None),
        _ => new SKSamplingOptions(SKCubicResampler.Mitchell)
    };

    private static string GetAttribute(string line, string attrName)
    {
        string token = attrName + "=\"";
        int start = line.IndexOf(token, StringComparison.Ordinal);
        if (start < 0) return string.Empty;
        start += token.Length;
        int end = line.IndexOf('"', start);
        return end < 0 ? string.Empty : line.Substring(start, end - start);
    }

    private static int ParseIntAttr(string line, string attrName)
    {
        string val = GetAttribute(line, attrName);
        return int.Parse(val, CultureInfo.InvariantCulture);
    }

    private sealed record OutputSegment(string Text, GraphicJob? AssociatedJob);

    private sealed record GraphicJob(
        int Index,
        string InputPngPath,
        string OutputPngPath,
        string InputFileName,
        int NewWidth,
        int NewHeight,
        int X1,
        int Y1,
        int X2,
        int Y2,
        int ResizeWidth,
        int ResizeHeight,
        int CropX,
        int FinalWidth,
        int FinalHeight,
        int InitialWarnings,
        string? DepthWarning)
    {
        public bool Success { get; set; }
        public string? ErrorMessage { get; set; }
    }
}
