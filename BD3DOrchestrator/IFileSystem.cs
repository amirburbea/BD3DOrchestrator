using System;

namespace BD3DOrchestrator;

/// <summary>
/// Minimal file system abstraction so core logic can be tested without touching the real disk.
/// </summary>
public interface IFileSystem
{
    bool FileExists(string path);
    string ReadAllText(string path);
    void WriteAllText(string path, string contents);
    void CopyFile(string sourceFileName, string destFileName, bool overwrite = false);
    void MoveFile(string sourceFileName, string destFileName, bool overwrite = false);
    long GetFileLength(string path);
    bool DirectoryExists(string path);
    void CreateDirectory(string path);
    void DeleteDirectory(string path, bool recursive = false);
    string[] GetFiles(string path, string searchPattern = "*");
    string[] GetDirectories(string path);
    string? GetParentDirectory(string path);
    DateTime GetDirectoryCreationTime(string path);
}
