using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BD3DOrchestrator;

public interface IEncodeProfile
{
    string Hash { get; }
    string MkvTitleTemplate { get; }
    string OutputNameTemplate { get; }
    IReadOnlyList<KeyValuePair<string, string>> Settings { get; }
    string SourcePath { get; }
    /// <summary>
    /// Tcl script lines that force every profile setting (both ::config(...) and the live ::var).
    /// </summary>
    string TclBlock { get; }
}

/// <summary>
/// Shared, machine-independent encoding profile. Replaces whatever happens to be saved in each PC's
/// BD3D2MK3D.cfg so every machine produces the same encode and the same file name.
/// </summary>
public sealed class EncodeProfile : IEncodeProfile
{
    private static readonly char[] _sanitizeChars = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    private readonly List<KeyValuePair<string, string>> _settings = [];

    public string Hash { get; }
    public string MkvTitleTemplate { get; } = "{title}";
    public string OutputNameTemplate { get; } = "{title}.3d.tab.mkv";
    public IReadOnlyList<KeyValuePair<string, string>> Settings => this._settings;
    public string SourcePath { get; }
    public string TclBlock { get; }

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
                this._settings.Add(new(prop.Name, value));
            }
        }
        // Hash only what affects output, so comments/whitespace edits don't change it.
        StringBuilder canon = new();
        canon.Append(this.OutputNameTemplate).Append('\n').Append(this.MkvTitleTemplate).Append('\n');
        foreach ((string key, string value) in this._settings)
        {
            canon.Append(key).Append('=').Append(value).Append('\n');
        }
        this.Hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canon.ToString())))[..12].ToLowerInvariant();
        this.TclBlock = this.BuildTclBlock();
    }

    public static string ApplyTitle(string template, string title) => template.Replace("{title}", title);

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


    private string BuildTclBlock()
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
