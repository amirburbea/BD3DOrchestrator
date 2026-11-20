namespace BD3DOrchestrator;

/// <summary>
/// TMDb credentials, bound from the "Tmdb" section in appsettings.json.
/// Without an <see cref="ApiReadAccessToken"/>, the online lookup is skipped.
/// </summary>
public sealed class TmdbOptions
{
    /// <summary>The short v3 key. Not used: the read access token is sent in a header instead, so no key ends up in a URL.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>The long v4 "API Read Access Token", sent as a Bearer token.</summary>
    public string ApiReadAccessToken { get; set; } = string.Empty;
}
