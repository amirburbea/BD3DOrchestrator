using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BD3DOrchestrator;

public interface IEncodeProfile
{
    string Hash { get; }
    string MkvTitleTemplate { get; }
    string OutputNameTemplate { get; }
    /// <summary>BD3D2MK3D settings by name, in profile.json order. A name listed twice keeps its last value.</summary>
    IReadOnlyDictionary<string, string> Settings { get; }
    string SourcePath { get; }
    /// <summary>
    /// Tcl script lines that force every profile setting (both ::config(...) and the live ::var).
    /// </summary>
    string TclBlock { get; }

    /// <summary>
    /// The x264 CRF this profile encodes at, or null when it doesn't encode x264 in CRF mode (then there is no CRF to adjust).
    /// </summary>
    decimal? Crf { get; }

    /// <summary>
    /// The CRF for the "extra quality" option: one step below the profile's (lower CRF = higher quality), never below 0.
    /// Null when there is nothing lower to go to (see <see cref="ExtraQualityUnavailableReason"/>).
    /// </summary>
    decimal? ExtraQualityCrf => this.Crf is { } crf && crf > 0 ? Math.Max(0, crf - 1) : null;

    /// <summary>Why the "extra quality" option can't be used with this profile, or null when it can.</summary>
    string? ExtraQualityUnavailableReason => this.Crf switch
    {
        null => "the profile does not encode x264 in CRF mode",
        <= 0 => "the profile is already at CRF 0, the lowest",
        _ => null
    };
}

/// <summary>
/// Shared, machine-independent encoding profile. Replaces whatever happens to be saved in each PC's
/// BD3D2MK3D.cfg so every machine produces the same encode and the same file name.
/// </summary>
public sealed class EncodeProfile : IEncodeProfile
{
    // Ordered only so TclBlock reads like profile.json; Tcl variable names are case-sensitive, hence Ordinal.
    private readonly OrderedDictionary<string, string> _settings = [with(StringComparer.Ordinal)];

    public string Hash { get; }
    public string MkvTitleTemplate { get; } = "{title}";
    public string OutputNameTemplate { get; } = "{title}.3d.tab.mkv";
    public IReadOnlyDictionary<string, string> Settings => this._settings;
    public string SourcePath { get; }
    public string TclBlock { get; }
    public decimal? Crf { get; }

    public EncodeProfile(AppSettings settings, IFileSystem fileSystem)
    {
        string path = settings.ResolvedProfilePath;
        this.SourcePath = path;
        string raw = fileSystem.ReadAllText(path);
        JsonDocumentOptions options = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
        using JsonDocument doc = JsonDocument.Parse(raw, options);
        JsonElement root = doc.RootElement;
        if (root.TryGetProperty(nameof(this.OutputNameTemplate), out JsonElement nameElem) && nameElem.GetString() is { Length: > 0 } name)
        {
            this.OutputNameTemplate = name;
        }
        if (root.TryGetProperty(nameof(this.MkvTitleTemplate), out JsonElement titleElem) && titleElem.GetString() is { Length: > 0 } title)
        {
            this.MkvTitleTemplate = title;
        }
        if (root.TryGetProperty(nameof(this.Settings), out JsonElement settingsElem) && settingsElem.ValueKind is JsonValueKind.Object)
        {
            foreach (JsonProperty prop in settingsElem.EnumerateObject())
            {
                string value = prop.Value.ValueKind switch
                {
                    JsonValueKind.True => "1",
                    JsonValueKind.False => "0",
                    JsonValueKind.Number => prop.Value.GetRawText(),
                    _ => prop.Value.GetString() ?? ""
                };
                this._settings[prop.Name] = value; // last one wins, as it would in Tcl
            }
        }
        // Hash only what affects output, so comments, whitespace and the order of the settings don't change it.
        StringBuilder canon = new();
        canon.Append(this.OutputNameTemplate).Append('\n').Append(this.MkvTitleTemplate).Append('\n');
        foreach ((string key, string value) in this._settings.OrderBy(setting => setting.Key, StringComparer.Ordinal))
        {
            canon.Append(key).Append('=').Append(value).Append('\n');
        }
        this.Hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canon.ToString())))[..12].ToLowerInvariant();
        this.TclBlock = this.BuildTclBlock();
        this.Crf = this.ReadCrf();
    }

    /// <summary>"18", "17.5": CRF values as x264 and the UI show them.</summary>
    public static string FormatCrf(decimal crf) => crf.ToString("0.##", CultureInfo.InvariantCulture);

    private decimal? ReadCrf()
    {
        if (this._settings.GetValueOrDefault("use_x265") == "1" || !"CRF".Equals(this._settings.GetValueOrDefault("x264mode"), StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        return decimal.TryParse(this._settings.GetValueOrDefault("x264crf"), NumberStyles.Number, CultureInfo.InvariantCulture, out decimal crf) ? crf : null;
    }

    private string BuildTclBlock()
    {
        StringBuilder sb = new();
        foreach ((string key, string value) in this._settings)
        {
            string v = TclMethods.Quote(value);
            sb.Append("set ::config(").Append(key).Append(") ").Append(v).Append('\n');
            sb.Append("set ::").Append(key).Append(' ').Append(v).Append('\n');
        }
        return sb.ToString();
    }
}
