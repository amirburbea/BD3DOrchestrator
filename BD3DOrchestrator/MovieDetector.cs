using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;

namespace BD3DOrchestrator;

public record MovieInfo(string Title, int Year, bool IsAnimation, string Reason);

public partial class MovieDetector(HttpClient httpClient, IOptions<TmdbOptions> tmdbOptions)
{
    /// <summary>TMDb's genre id for Animation.</summary>
    private const int TmdbAnimationGenreId = 16;

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
        // Only "(yyyy)" is a year; a bare number is part of the title ("Blade Runner 2049 (2017)" is "Blade Runner 2049", 2017).
        if (TitleAndYearRegex().Match(raw) is { Success: true } match && int.TryParse(match.Groups[2].Value, out int parsedYear))
        {
            return (match.Groups[1].Value.Trim(), parsedYear);
        }
        return (raw.Trim(), 0);
    }

    public async Task<MovieInfo> DetectAsync(string path, CancellationToken cancellationToken = default)
    {
        string dir = Directory.Exists(path) ? path : (Path.GetDirectoryName(path) ?? ".");
        string nameCandidate = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        (string title, int year) = ParseTitleAndYear(nameCandidate);
        // 1. A local .nfo is authoritative (title, year and genre).
        if (TryReadNfo(dir) is { } nfoInfo)
        {
            return nfoInfo;
        }
        // 2. The offline list says whether it's animation; TMDb is still asked when the file name has no year.
        string? knownAnimation = FindKnownAnimation(title);
        string token = tmdbOptions.Value.ApiReadAccessToken;
        TmdbLookup? tmdb = !string.IsNullOrWhiteSpace(token) && (knownAnimation is null || year == 0)
            ? await this.LookUpTmdbAsync(token, title, year, cancellationToken).ConfigureAwait(false)
            : null;
        // The file name's "(yyyy)" always wins; TMDb's year is only a fallback, for any genre.
        int finalYear = year > 0 ? year : tmdb?.Year ?? 0;
        string notes = (year == 0 && finalYear > 0 ? $"; year {finalYear} from TMDb" : "")
            + (string.IsNullOrWhiteSpace(token) ? "; online lookup off (no tmdb-keys.json)" : "");
        if (knownAnimation is not null)
        {
            return new(title, finalYear, true, $"Matched 3D Animation database ({knownAnimation}){notes}");
        }
        if (tmdb is { IsAnimation: true })
        {
            return new(title, finalYear, true, $"TMDb confirmed Animation ({tmdb.MatchedTitle}){notes}");
        }
        return new(title, finalYear, false, $"Live action or unconfirmed{notes}");
    }

    private static string? FindKnownAnimation(string title)
    {
        if (_known3DAnimations.Contains(title))
        {
            return title;
        }
        foreach (string known in _known3DAnimations)
        {
            if (title.StartsWith(known, StringComparison.OrdinalIgnoreCase) || known.StartsWith(title, StringComparison.OrdinalIgnoreCase))
            {
                return known;
            }
        }
        return null;
    }

    private sealed record TmdbLookup(string MatchedTitle, int Year, bool IsAnimation);

    /// <summary>
    /// Searches TMDb's movie catalogue, passing the file name's year (when there is one) to narrow the match. A result whose
    /// title matches exactly is preferred, otherwise TMDb's top result is used. Null when nothing is found or the lookup fails.
    /// The token goes in the Authorization header, never in the URL.
    /// </summary>
    private async Task<TmdbLookup?> LookUpTmdbAsync(string token, string title, int year, CancellationToken cancellationToken)
    {
        try
        {
            string url = $"https://api.themoviedb.org/3/search/movie?query={Uri.EscapeDataString(title)}&include_adult=false"
                + (year > 0 ? $"&year={year}" : "");
            using HttpRequestMessage request = new(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }
            string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using JsonDocument doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("results", out JsonElement results) || results.GetArrayLength() == 0)
            {
                return null;
            }
            JsonElement best = results[0];
            foreach (JsonElement item in results.EnumerateArray())
            {
                if (item.TryGetProperty("title", out JsonElement itemTitle) && string.Equals(itemTitle.GetString(), title, StringComparison.OrdinalIgnoreCase))
                {
                    best = item;
                    break;
                }
            }
            string matchedTitle = best.TryGetProperty("title", out JsonElement titleElem) && titleElem.GetString() is { Length: > 0 } t ? t : title;
            int matchedYear = best.TryGetProperty("release_date", out JsonElement dateElem) && dateElem.GetString() is { Length: >= 4 } releaseDate && int.TryParse(releaseDate[..4], out int y) ? y : 0;
            bool isAnimation = false;
            if (best.TryGetProperty("genre_ids", out JsonElement genreIds) && genreIds.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement genreId in genreIds.EnumerateArray())
                {
                    isAnimation |= genreId.TryGetInt32(out int id) && id == TmdbAnimationGenreId;
                }
            }
            return new(matchedTitle, matchedYear, isAnimation);
        }
        catch
        {
            // Network lookup failed or returned something unexpected; detection carries on without it.
            return null;
        }
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

    /// <summary>"Title (yyyy)": greedy, so the year is the last one in parentheses and any digits in the title stay in the title.</summary>
    [GeneratedRegex(@"^(.*\S)\s*\((\d{4})\)")]
    private static partial Regex TitleAndYearRegex();
}
