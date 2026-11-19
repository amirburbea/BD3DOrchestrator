using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BD3DOrchestrator;

/// <summary>
/// Shared, machine-independent encoding profile. Replaces whatever happens to be saved in each PC's
/// BD3D2MK3D.cfg so every machine produces the same encode and the same file name.
/// </summary>
public class EncodeProfile
{
    private static readonly char[] _sanitizeChars = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    private readonly List<KeyValuePair<string, string>> _settings = [];

    public string Hash { get; private set; } = "";
    public string MkvTitleTemplate { get; private set; } = "{title}";
    public string OutputNameTemplate { get; private set; } = "{title}.3d.tab.mkv";
    public IReadOnlyList<KeyValuePair<string, string>> Settings => this._settings;
    public string SourcePath { get; private set; } = "";

    public static string ApplyTitle(string template, string title) => template.Replace("{title}", title);

    public static EncodeProfile Load(string path)
    {
        string raw = File.ReadAllText(path);
        JsonDocumentOptions options = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
        using JsonDocument doc = JsonDocument.Parse(raw, options);
        EncodeProfile profile = new() { SourcePath = path };
        JsonElement root = doc.RootElement;
        if (root.TryGetProperty(nameof(OutputNameTemplate), out JsonElement nameElem) && nameElem.GetString() is { Length: > 0 } name)
        {
            profile.OutputNameTemplate = name;
        }
        if (root.TryGetProperty(nameof(MkvTitleTemplate), out JsonElement titleElem) && titleElem.GetString() is { Length: > 0 } title)
        {
            profile.MkvTitleTemplate = title;
        }
        if (root.TryGetProperty(nameof(Settings), out JsonElement settings) && settings.ValueKind is JsonValueKind.Object)
        {
            foreach (JsonProperty prop in settings.EnumerateObject())
            {
                string value = prop.Value.ValueKind switch
                {
                    JsonValueKind.True => "1",
                    JsonValueKind.False => "0",
                    JsonValueKind.Number => prop.Value.GetRawText(),
                    _ => prop.Value.GetString() ?? ""
                };
                profile._settings.Add(new(prop.Name, value));
            }
        }
        // Hash only what affects output, so comments/whitespace edits don't change it.
        StringBuilder canon = new();
        canon.Append(profile.OutputNameTemplate).Append('\n').Append(profile.MkvTitleTemplate).Append('\n');
        foreach ((string key, string value) in profile._settings)
        {
            canon.Append(key).Append('=').Append(value).Append('\n');
        }
        profile.Hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canon.ToString())))[..12].ToLowerInvariant();
        return profile;
    }

    public static string SanitizeFileName(string name)
    {
        foreach (char c in _sanitizeChars)
        {
            name = name.Replace(c.ToString(), "");
        }
        return name.Trim().TrimEnd('.');
    }

    public static string TclQuote(string value)
    {
        StringBuilder sb = new("\"");
        foreach (char c in value)
        {
            if (c is '\\' or '"' or '$' or '[' or ']')
            {
                sb.Append('\\');
            }
            sb.Append(c);
        }
        return sb.Append('"').ToString();
    }

    /// <summary>
    /// Tcl script lines that force every profile setting (both ::config(...) and the live ::var).
    /// </summary>
    public string ToTclBlock()
    {
        StringBuilder sb = new();
        foreach ((string key, string value) in this._settings)
        {
            string v = TclQuote(value);
            sb.Append("set ::config(").Append(key).Append(") ").Append(v).Append('\n');
            sb.Append("set ::").Append(key).Append(' ').Append(v).Append('\n');
        }
        return sb.ToString();
    }
}
