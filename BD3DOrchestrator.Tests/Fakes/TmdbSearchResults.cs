using System.Net.Http;
using System.Text.Json;

namespace BD3DOrchestrator.Tests.Fakes;

/// <summary>A TMDb /search/movie response body. TMDb uses snake_case names (release_date, genre_ids).</summary>
public sealed record TmdbSearchResults(params TmdbMovie[] Results)
{
    private static readonly JsonSerializerOptions _snakeCase = new(JsonSerializerOptions.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public HttpResponseMessage ToResponse() => FakeHttpMessageHandler.JsonResponse(this, _snakeCase);
}

public readonly record struct TmdbMovie(string Title, string? ReleaseDate, params int[] GenreIds);
