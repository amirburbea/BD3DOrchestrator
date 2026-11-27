using System;
using System.Diagnostics;
using System.Globalization;

namespace Sub3DConverter;

public static class Program
{
    public static int Main(string[] args)
    {
        string? inXml = null;
        string? outXml = null;
        bool sbs = true;
        int leftFirst = 1;
        int addDepth = 0;
        string filter = "Mitchell";
        bool onlyForced = false;
        bool verifySubs = false;
        string appName = "BD3D2MK3D";
        string appVersion = "1.40";
        int threads = 0;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (i + 1 >= args.Length) break;
            switch (arg)
            {
                case "--inxml": inXml = args[++i]; break;
                case "--outxml": outXml = args[++i]; break;
                case "--sbs": sbs = args[++i] is "1" or "true" or "True"; break;
                case "--leftfirst": leftFirst = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--adddepth": addDepth = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--filter": filter = args[++i]; break;
                case "--onlyforced": onlyForced = args[++i] is "1" or "true" or "True"; break;
                case "--verifysubs": verifySubs = args[++i] is "1" or "true" or "True"; break;
                case "--appname": appName = args[++i]; break;
                case "--appversion": appVersion = args[++i]; break;
                case "--threads": threads = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
            }
        }

        if (string.IsNullOrEmpty(inXml) || string.IsNullOrEmpty(outXml))
        {
            Console.Error.WriteLine("Usage: Sub3DConverter --inxml <path> --outxml <path> --sbs <0|1> --leftfirst <1|-1> --adddepth <int> --filter <name> --onlyforced <0|1> --verifysubs <0|1>");
            return 2;
        }

        Stopwatch sw = Stopwatch.StartNew();
        ConversionResult result = SubtitleConverter.Convert(new ConversionOptions(
            InputXml: inXml,
            OutputXml: outXml,
            SideBySide: sbs,
            LeftFirst: leftFirst,
            AddDepth: addDepth,
            Filter: filter,
            OnlyForced: onlyForced,
            VerifySubs: verifySubs,
            AppName: appName,
            AppVersion: appVersion,
            MaxThreads: threads));
        sw.Stop();

        if (!string.IsNullOrWhiteSpace(result.LogDetails))
        {
            Console.WriteLine(result.LogDetails.TrimEnd());
        }
        Console.WriteLine($"ELAPSED_MS={sw.ElapsedMilliseconds}");
        Console.WriteLine($"RESULT|{result.NumConverted}|{result.NumErrors}|{result.NumWarnings}|{result.Message}");
        return result.NumErrors > 0 ? 1 : 0;
    }
}
