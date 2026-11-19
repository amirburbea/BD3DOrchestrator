using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace BD3DOrchestrator;

public record MovieInfo(string Title, int Year, bool IsAnimation, string Reason);

public partial class MovieDetector(HttpClient httpClient)
{
    private static readonly HashSet<string> _known3DAnimations = [
        with(StringComparer.OrdinalIgnoreCase),
        "Toy Story", "Toy Story 2", "Toy Story 3", "Toy Story 4",
        "Finding Nemo", "Finding Dory", "Monsters, Inc.", "Monsters Inc", "Monsters University",
        "The Incredibles", "Incredibles 2", "Cars", "Cars 2", "Cars 3",
        "Ratatouille", "WALL-E", "WALL E", "Up", "Brave", "Inside Out", "The Good Dinosaur",
        "Coco", "Onward", "Soul", "Luca", "Turning Red", "Lightyear",
        "Tangled", "Wreck-It Ralph", "Frozen", "Frozen II", "Big Hero 6",
        "Zootopia", "Moana", "Ralph Breaks the Internet", "Raya and the Last Dragon", "Encanto",
        "Shrek", "Shrek 2", "Shrek the Third", "Shrek Forever After",
        "Madagascar", "Madagascar: Escape 2 Africa", "Madagascar 3: Europe's Most Wanted",
        "Kung Fu Panda", "Kung Fu Panda 2", "Kung Fu Panda 3", "Kung Fu Panda 4",
        "How to Train Your Dragon", "How to Train Your Dragon 2", "How to Train Your Dragon: The Hidden World",
        "Megamind", "Rise of the Guardians", "The Croods", "The Croods: A New Age",
        "Turbo", "Mr. Peabody & Sherman", "Home", "Trolls", "Trolls World Tour", "The Boss Baby",
        "Captain Underpants", "Abominable", "The Bad Guys",
        "Despicable Me", "Despicable Me 2", "Despicable Me 3", "Despicable Me 4",
        "Minions", "Minions: The Rise of Gru", "The Secret Life of Pets", "The Secret Life of Pets 2",
        "Sing", "Sing 2", "The Grinch", "The Lorax",
        "Ice Age", "Ice Age 2", "Ice Age 3", "Ice Age 4", "Ice Age 5",
        "Ice Age: The Meltdown", "Ice Age: Dawn of the Dinosaurs", "Ice Age 3 - Dawn of the Dinosaurs",
        "Ice Age: Continental Drift", "Ice Age 4 - Continental Drift", "Ice Age: Collision Course", "Ice Age - Collision Course",
        "Rio", "Rio 2", "Epic", "The Peanuts Movie", "Ferdinand", "Spies in Disguise",
        "Cloudy with a Chance of Meatballs", "Cloudy with a Chance of Meatballs 2",
        "Hotel Transylvania", "Hotel Transylvania 2", "Hotel Transylvania 3: Summer Vacation",
        "Arthur Christmas", "The Pirates! Band of Misfits", "The Smurfs", "The Smurfs 2", "Smurfs: The Lost Village",
        "Spider-Man: Into the Spider-Verse", "Spider-Man: Across the Spider-Verse",
        "The Lego Movie", "The Lego Batman Movie", "The Lego Ninjago Movie", "The Lego Movie 2: The Second Part",
        "Coraline", "ParaNorman", "The Boxtrolls", "Kubo and the Two Strings", "Puss in Boots",
        "The Nightmare Before Christmas", "Frankenweenie", "Monster House", "Beowulf", "The Polar Express",
        "A Christmas Carol", "Mars Needs Moms", "The Adventures of Tintin", "Legend of the Guardians",
    ];

    public static (string Title, int Year) ParseTitleAndYear(string input)
    {
        string raw = Path.GetFileNameWithoutExtension(input);
        if (raw.EndsWith(".3d.mvc", StringComparison.OrdinalIgnoreCase))
        {
            raw = raw[..^7];
        }
        if (TitleAndYearRegex().Match(raw) is { Success: true, Groups: { } groups })
        {
            string cleanTitle = groups[1].Value.Trim();
            string yearStr = string.IsNullOrEmpty(groups[2].Value) ? groups[3].Value : groups[2].Value;
            if (int.TryParse(yearStr, out int parsedYear))
            {
                return (cleanTitle, parsedYear);
            }
        }
        return (raw.Trim(), 0);
    }

    public async Task<MovieInfo> DetectAsync(string path, CancellationToken cancellationToken = default)
    {
        string dir = Directory.Exists(path) ? path : (Path.GetDirectoryName(path) ?? ".");
        string nameCandidate = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        (string title, int year) = ParseTitleAndYear(nameCandidate);
        // 1. Check local .nfo if available in the folder
        if (TryReadNfo(dir) is { } nfoInfo)
        {
            return nfoInfo;
        }
        // 2. Check offline known 3D animations list
        if (_known3DAnimations.Contains(title))
        {
            return new(title, year, true, $"Matched 3D Animation database ({title})");
        }
        // Partial match against known animation titles
        foreach (string known in _known3DAnimations)
        {
            if (title.StartsWith(known, StringComparison.OrdinalIgnoreCase) || known.StartsWith(title, StringComparison.OrdinalIgnoreCase))
            {
                return new(title, year, true, $"Matched 3D Animation database ({known})");
            }
        }
        // 3. Fallback: Query iTunes search API (No API key needed)
        try
        {
            string queryUrl = $"https://itunes.apple.com/search?term={Uri.EscapeDataString(title)}&entity=movie&limit=3";
            using HttpResponseMessage response = await httpClient.GetAsync(queryUrl, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                string jsonString = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                using JsonDocument doc = JsonDocument.Parse(jsonString);
                if (doc.RootElement.TryGetProperty("results", out JsonElement results) && results.GetArrayLength() > 0)
                {
                    foreach (JsonElement item in results.EnumerateArray())
                    {
                        string genre = item.TryGetProperty("primaryGenreName", out JsonElement genreElem) ? (genreElem.GetString() ?? "") : "";
                        string releaseDate = item.TryGetProperty("releaseDate", out JsonElement dateElem) ? (dateElem.GetString() ?? "") : "";
                        int parsedYear = year;
                        if (parsedYear == 0 && releaseDate.Length >= 4 && int.TryParse(releaseDate[..4], out int y))
                        {
                            parsedYear = y;
                        }
                        if (genre.Equals("Animation", StringComparison.OrdinalIgnoreCase))
                        {
                            return new(title, parsedYear, true, "Apple Movie API confirmed Animation");
                        }
                    }
                }
            }
        }
        catch
        {
            // Network lookup failed, fallback gracefully
        }
        return new(title, year, false, "Live action or unconfirmed");
    }

    private static MovieInfo? TryReadNfo(string directory)
    {
        try
        {
            if (!Directory.Exists(directory) || Directory.GetFiles(directory, "*.nfo") is not [{ } xmlFile, ..])
            {
                return null;
            }
            string xml = File.ReadAllText(xmlFile);
            Match genreMatch = GenreRegex().Match(xml);
            Match yearMatch = YearRegex().Match(xml);
            Match titleMatch = TitleRegex().Match(xml);
            string title = titleMatch.Success ? titleMatch.Groups[1].Value : "";
            int year = yearMatch.Success && int.TryParse(yearMatch.Groups[1].Value, out int y) ? y : 0;
            if (genreMatch.Success && genreMatch.Groups[1].Value.Contains("Animation", StringComparison.OrdinalIgnoreCase))
            {
                return new(title, year, true, "Detected from local .nfo metadata");
            }
        }
        catch
        {
        }
        return null;
    }

    [GeneratedRegex(@"<genre>(.*?)</genre>", RegexOptions.IgnoreCase, "en-US")]
    private static partial Regex GenreRegex();

    [GeneratedRegex(@"<year>(\d{4})</year>", RegexOptions.IgnoreCase, "en-US")]
    private static partial Regex YearRegex();

    [GeneratedRegex(@"<title>(.*?)</title>", RegexOptions.IgnoreCase, "en-US")]
    private static partial Regex TitleRegex();

    [GeneratedRegex(@"^(.*?)(?:\s*\((\d{4})\)|\s+(\d{4}))")]
    private static partial Regex TitleAndYearRegex();
}
