using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Enumeration;
using System.Linq;

namespace BD3DOrchestrator.Tests.Fakes;

public sealed class FakeFileSystem : IFileSystem
{
    private readonly Dictionary<string, string> _files = [with(StringComparer.OrdinalIgnoreCase)];
    private readonly Dictionary<string, long> _fileLengths = [with(StringComparer.OrdinalIgnoreCase)];
    private readonly Dictionary<string, DateTime> _directories = [with(StringComparer.OrdinalIgnoreCase)];

    public FakeFileSystem AddDirectory(string path, DateTime creationTime = default)
    {
        string normalized = Normalize(path);
        this._directories.TryAdd(normalized, creationTime);
        string? parent = Path.GetDirectoryName(normalized);
        while (!string.IsNullOrEmpty(parent))
        {
            this._directories.TryAdd(parent, creationTime);
            parent = Path.GetDirectoryName(parent);
        }
        return this;
    }

    public FakeFileSystem AddFile(string path, string content = "", long? length = null)
    {
        string normalized = Normalize(path);
        if (Path.GetDirectoryName(normalized) is { Length: > 0 } dir)
        {
            this.AddDirectory(dir);
        }
        this._files[normalized] = content;
        if (length is { } explicitLength)
        {
            this._fileLengths[normalized] = explicitLength;
        }
        else
        {
            this._fileLengths.Remove(normalized);
        }
        return this;
    }

    public bool FileExists(string path) => this._files.ContainsKey(Normalize(path));

    public string ReadAllText(string path) =>
        this._files.TryGetValue(Normalize(path), out string? content)
            ? content
            : throw new FileNotFoundException($"File not found: '{path}'.", path);

    public void WriteAllText(string path, string contents) => this.AddFile(path, contents);

    public void CopyFile(string sourceFileName, string destFileName, bool overwrite = false)
    {
        string src = Normalize(sourceFileName);
        string dst = Normalize(destFileName);
        if (!this._files.TryGetValue(src, out string? content))
        {
            throw new FileNotFoundException($"Source file not found: '{sourceFileName}'.", sourceFileName);
        }
        if (!overwrite && this._files.ContainsKey(dst))
        {
            throw new IOException($"Destination file already exists: '{destFileName}'.");
        }
        long? len = this._fileLengths.TryGetValue(src, out long explicitLen) ? explicitLen : null;
        this.AddFile(dst, content, len);
    }

    public long GetFileLength(string path)
    {
        string normalized = Normalize(path);
        if (this._fileLengths.TryGetValue(normalized, out long explicitLen))
        {
            return explicitLen;
        }
        return this._files.TryGetValue(normalized, out string? content)
            ? content.Length
            : throw new FileNotFoundException($"File not found: '{path}'.", path);
    }

    public bool DirectoryExists(string path) => this._directories.ContainsKey(Normalize(path));

    public void CreateDirectory(string path) => this.AddDirectory(path);

    public void DeleteDirectory(string path, bool recursive = false)
    {
        string normalized = Normalize(path);
        if (!this._directories.ContainsKey(normalized))
        {
            throw new DirectoryNotFoundException($"Directory not found: '{path}'.");
        }
        string prefix = normalized.EndsWith(Path.DirectorySeparatorChar)
            ? normalized
            : normalized + Path.DirectorySeparatorChar;
        List<string> childFiles = [.. this._files.Keys.Where(f => f.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))];
        List<string> childDirs = [.. this._directories.Keys.Where(d => d.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))];
        if (!recursive && (childFiles.Count > 0 || childDirs.Count > 0))
        {
            throw new IOException($"Directory is not empty: '{path}'.");
        }
        foreach (string f in childFiles)
        {
            this._files.Remove(f);
            this._fileLengths.Remove(f);
        }
        foreach (string d in childDirs)
        {
            this._directories.Remove(d);
        }
        this._directories.Remove(normalized);
    }

    public string[] GetFiles(string path, string? searchPattern = null)
    {
        string normalized = Normalize(path);
        if (!this._directories.ContainsKey(normalized))
        {
            throw new DirectoryNotFoundException($"Directory not found: '{path}'.");
        }
        return
        [
            .. this._files.Keys.Where(f =>
                string.Equals(Normalize(Path.GetDirectoryName(f) ?? ""), normalized, StringComparison.OrdinalIgnoreCase) &&
                (searchPattern is null || FileSystemName.MatchesSimpleExpression(searchPattern, Path.GetFileName(f), ignoreCase: true)))
        ];
    }

    public string[] GetDirectories(string path)
    {
        string normalized = Normalize(path);
        if (!this._directories.ContainsKey(normalized))
        {
            throw new DirectoryNotFoundException($"Directory not found: '{path}'.");
        }
        return
        [
            .. this._directories.Keys.Where(d =>
                !string.Equals(d, normalized, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(Normalize(Path.GetDirectoryName(d) ?? ""), normalized, StringComparison.OrdinalIgnoreCase))
        ];
    }

    public string? GetParentDirectory(string path) => Path.GetDirectoryName(Normalize(path));

    public DateTime GetDirectoryCreationTime(string path) => this._directories.GetValueOrDefault(Normalize(path));

    private static string Normalize(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return "";
        }
        string replaced = path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        string? root = Path.GetPathRoot(replaced);
        return string.Equals(replaced, root, StringComparison.OrdinalIgnoreCase)
            ? replaced
            : replaced.TrimEnd(Path.DirectorySeparatorChar);
    }
}
