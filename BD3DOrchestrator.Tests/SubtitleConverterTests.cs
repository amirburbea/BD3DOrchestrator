using System;
using System.IO;
using System.Threading.Tasks;
using SkiaSharp;
using Sub3DConverter;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace BD3DOrchestrator.Tests;

public sealed class SubtitleConverterTests
{
    [Test]
    public async Task Convert_SideBySide_ProducesExpectedDimensionsAndXml()
    {
        string tempDir = CreateTempDir();
        try
        {
            string inDir = Path.Combine(tempDir, "in");
            string outDir = Path.Combine(tempDir, "out");
            Directory.CreateDirectory(inDir);

            CreateTestPng(Path.Combine(inDir, "0001.png"), 200, 60, SKColors.White, SKColors.Black);
            string inXml = Path.Combine(inDir, "in.xml");
            string outXml = Path.Combine(outDir, "out.xml");
            File.WriteAllText(inXml, """
                <?xml version="1.0" encoding="UTF-8"?>
                <BDN Version="0.93" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
                  <Description>
                    <Format VideoFormat="1080p" FrameRate="23.976" DropFrame="False"/>
                  </Description>
                  <Events NumberofEvents="1">
                    <Event InTC="00:00:01:00" OutTC="00:00:03:00" Forced="False">
                      <Graphic Width="200" Height="60" X="860" Y="900" Depth="10">0001.png</Graphic>
                    </Event>
                  </Events>
                </BDN>
                """);

            ConversionResult result = SubtitleConverter.Convert(new ConversionOptions(
                InputXml: inXml,
                OutputXml: outXml,
                SideBySide: true,
                LeftFirst: 1,
                AddDepth: 0,
                Filter: "Mitchell",
                OnlyForced: false,
                VerifySubs: true));

            await Assert.That(result.NumConverted).IsEqualTo(1);
            await Assert.That(result.NumErrors).IsEqualTo(0);
            await Assert.That(result.NumWarnings).IsEqualTo(0);

            // newWidth = 200 + 1920 - 10 = 2110; finalWidth = (2110 + 1) / 2 = 1055
            // newX = round((860 + 5.0) / 2.0) = 433; newY = 900
            string xmlOut = await File.ReadAllTextAsync(outXml);
            await Assert.That(xmlOut).Contains("Width=\"1055\" Height=\"60\" X=\"433\" Y=\"900\" OrigDepth=\"10\">sub_0001.png</Graphic>");

            using SKBitmap outBmp = SKBitmap.Decode(Path.Combine(outDir, "sub_0001.png"));
            await Assert.That(outBmp.Width).IsEqualTo(1055);
            await Assert.That(outBmp.Height).IsEqualTo(60);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Test]
    public async Task Convert_TopAndBottom_WithNegativeNewX_CropsCorrectly()
    {
        string tempDir = CreateTempDir();
        try
        {
            string inDir = Path.Combine(tempDir, "in");
            string outDir = Path.Combine(tempDir, "out");
            Directory.CreateDirectory(inDir);

            CreateTestPng(Path.Combine(inDir, "0001.png"), 200, 60, SKColors.White, SKColors.Black);
            string inXml = Path.Combine(inDir, "in.xml");
            string outXml = Path.Combine(outDir, "out.xml");
            File.WriteAllText(inXml, """
                <BDN>
                  <Description><Format VideoFormat="1080p"/></Description>
                  <Events NumberofEvents="1">
                    <Event InTC="00:00:01:00" OutTC="00:00:02:00" Forced="False">
                      <Graphic Width="200" Height="60" X="2" Y="900" Depth="20">0001.png</Graphic>
                    </Event>
                  </Events>
                </BDN>
                """);

            ConversionResult result = SubtitleConverter.Convert(new ConversionOptions(
                InputXml: inXml,
                OutputXml: outXml,
                SideBySide: false,
                LeftFirst: 1,
                AddDepth: 0,
                Filter: "Mitchell",
                OnlyForced: false,
                VerifySubs: false));

            await Assert.That(result.NumConverted).IsEqualTo(1);
            await Assert.That(result.NumErrors).IsEqualTo(0);

            // T&B: newWidth = 220, newHeight = 1140, finalHeight = (1140 + 1)/2 = 570
            // newX = 2 - 10 = -8 -> cropx = 8, finalWidth = 220 - 8 = 212, newX = 0, newY = 450
            string xmlOut = await File.ReadAllTextAsync(outXml);
            await Assert.That(xmlOut).Contains("Width=\"212\" Height=\"570\" X=\"0\" Y=\"450\" OrigDepth=\"20\">sub_0001.png</Graphic>");

            using SKBitmap outBmp = SKBitmap.Decode(Path.Combine(outDir, "sub_0001.png"));
            await Assert.That(outBmp.Width).IsEqualTo(212);
            await Assert.That(outBmp.Height).IsEqualTo(570);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Test]
    public async Task Convert_OnlyForcedAndVerifySubs_BehaveLikeBd3d2mk3d()
    {
        string tempDir = CreateTempDir();
        try
        {
            string inDir = Path.Combine(tempDir, "in");
            string outDir = Path.Combine(tempDir, "out");
            Directory.CreateDirectory(inDir);

            CreateTestPng(Path.Combine(inDir, "0001.png"), 100, 40, SKColors.White, SKColors.Black);
            // Monochrome black PNG should trigger VerifySubs warning and ERROR comment
            CreateTestPng(Path.Combine(inDir, "0002.png"), 100, 40, SKColors.Black, SKColors.Black);

            string inXml = Path.Combine(inDir, "in.xml");
            string outXml = Path.Combine(outDir, "out.xml");
            File.WriteAllText(inXml, """
                <BDN>
                  <Description><Format VideoFormat="1080p"/></Description>
                  <Events NumberofEvents="2">
                    <Event InTC="00:00:01:00" OutTC="00:00:02:00" Forced="False">
                      <Graphic Width="100" Height="40" X="100" Y="900" Depth="0">0001.png</Graphic>
                    </Event>
                    <Event InTC="00:00:03:00" OutTC="00:00:04:00" Forced="True">
                      <Graphic Width="100" Height="40" X="100" Y="900" Depth="0">0002.png</Graphic>
                    </Event>
                  </Events>
                </BDN>
                """);

            ConversionResult result = SubtitleConverter.Convert(new ConversionOptions(
                InputXml: inXml,
                OutputXml: outXml,
                SideBySide: true,
                LeftFirst: 1,
                AddDepth: 0,
                Filter: "Mitchell",
                OnlyForced: true,
                VerifySubs: true));

            await Assert.That(result.NumConverted).IsEqualTo(1);
            await Assert.That(result.NumWarnings).IsEqualTo(1);

            string xmlOut = await File.ReadAllTextAsync(outXml);
            await Assert.That(xmlOut).Contains("<!-- ERROR");
            await Assert.That(xmlOut).Contains("sub_0002.png");
            await Assert.That(File.Exists(Path.Combine(outDir, "sub_0001.png"))).IsFalse();
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    private static string CreateTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "Sub3DConverterTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void CreateTestPng(string path, int width, int height, SKColor fg, SKColor bg)
    {
        using SKBitmap bmp = new(width, height);
        using SKCanvas canvas = new(bmp);
        canvas.Clear(bg);
        if (fg != bg)
        {
            using SKPaint paint = new() { Color = fg };
            canvas.DrawRect(10, 10, width - 20, height - 20, paint);
        }
        using SKData data = bmp.Encode(SKEncodedImageFormat.Png, 100);
        using FileStream fs = File.Create(path);
        data.SaveTo(fs);
    }
}
