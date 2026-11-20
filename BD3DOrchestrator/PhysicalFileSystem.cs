using System;
using System.IO;

namespace BD3DOrchestrator;

/// <summary>
/// Production <see cref="IFileSystem"/> backed by <see cref="System.IO"/>.
/// </summary>
public sealed class PhysicalFileSystem : IFileSystem
{
    public bool FileExists(string path) => File.Exists(path);
    public string ReadAllText(string path) => File.ReadAllText(path);
    public void WriteAllText(string path, string contents) => File.WriteAllText(path, contents);
    public void CopyFile(string sourceFileName, string destFileName, bool overwrite = false) => File.Copy(sourceFileName, destFileName, overwrite);
    public long GetFileLength(string path) => new FileInfo(path).Length;

    public bool DirectoryExists(string path) => Directory.Exists(path);
    public void CreateDirectory(string path) => Directory.CreateDirectory(path);
    public void DeleteDirectory(string path, bool recursive = false) => Directory.Delete(path, recursive);
    public string[] GetFiles(string path, string? searchPattern = null) => searchPattern is null ? Directory.GetFiles(path) : Directory.GetFiles(path, searchPattern);
    public string[] GetDirectories(string path) => Directory.GetDirectories(path);
    public string? GetParentDirectory(string path) => Directory.GetParent(path)?.FullName;
    public DateTime GetDirectoryCreationTime(string path) => Directory.GetCreationTime(path);
}
