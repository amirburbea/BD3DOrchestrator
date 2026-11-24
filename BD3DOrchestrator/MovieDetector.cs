using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace BD3DOrchestrator;

public record MovieInfo(string Title, int Year, bool IsAnimation, string Reason);

public partial class MovieDetector(HttpClient httpClient, AppSettings settings, IFileSystem fileSystem)
{
    /// <summary>TMDb's genre id for Animation.</summary>
    private const int TmdbAnimationGenreId = 16;

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

    public async Task<MovieInfo> DetectAsync(string mkvPath, CancellationToken cancellationToken = default)
    {
        string dir = Path.GetDirectoryName(mkvPath) ?? ".";
        string nameCandidate = Path.GetFileName(mkvPath);
        (string title, int year) = ParseTitleAndYear(nameCandidate);
        // If the file name has no "(yyyy)", check the containing folder name (e.g. "Aladdin (2019)\Aladdin.3d.mvc.mkv").
        if (year == 0)
        {
            (_, int folderYear) = ParseTitleAndYear(Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
            year = folderYear;
        }
        // 1. A local .nfo is authoritative (title, year and genre).
        if (this.TryReadNfo(dir) is { } nfoInfo)
        {
            return nfoInfo;
        }
        // 2. Query TMDb for genre and fallback release year.
        TmdbLookup? tmdb = await this.LookUpTmdbAsync(settings.TmdbApiReadAccessToken, title, year, cancellationToken).ConfigureAwait(false);
        // The file name's "(yyyy)" always wins; TMDb's year is only a fallback, for any genre.
        int finalYear = year > 0 ? year : tmdb?.Year ?? 0;
        string yearNote = year == 0 && finalYear > 0 ? $"; year {finalYear} from TMDb" : "";
        if (tmdb is { IsAnimation: true })
        {
            return new(title, finalYear, true, $"TMDb confirmed Animation ({tmdb.MatchedTitle}){yearNote}");
        }
        return new(title, finalYear, false, $"Live action or unconfirmed{yearNote}");
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
                + (year > 0 ? $"&primary_release_year={year}" : "");
            using HttpRequestMessage request = new(HttpMethod.Get, url)
            {
                Headers =
                {
                    Authorization = new("Bearer", token),
                    Accept =
                    {
                        new("application/json"),
                    }
                }
            };
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
            int bestScore = -1;
            foreach (JsonElement item in results.EnumerateArray())
            {
                bool titleMatch = item.TryGetProperty("title", out JsonElement itemTitle) &&
                                  string.Equals(itemTitle.GetString(), title, StringComparison.OrdinalIgnoreCase);
                bool yearMatch = year > 0 &&
                                 item.TryGetProperty("release_date", out JsonElement itemDate) &&
                                 itemDate.GetString() is { Length: >= 4 } rd &&
                                 int.TryParse(rd[..4], out int ry) &&
                                 ry == year;
                int score = (yearMatch ? 2 : 0) + (titleMatch ? 1 : 0);
                if (score > bestScore)
                {
                    best = item;
                    bestScore = score;
                    if (score == (year > 0 ? 3 : 1))
                    {
                        break;
                    }
                }
            }
            string matchedTitle = best.TryGetProperty("title", out JsonElement titleElem) && titleElem.GetString() is { Length: > 0 } titleText ? titleText : title;
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

    private MovieInfo? TryReadNfo(string directory)
    {
        try
        {
            if (!fileSystem.DirectoryExists(directory) || fileSystem.GetFiles(directory, "*.nfo") is not [{ } xmlFile, ..])
            {
                return null;
            }
            string xml = fileSystem.ReadAllText(xmlFile);
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
