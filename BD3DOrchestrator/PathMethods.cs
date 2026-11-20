namespace BD3DOrchestrator;

public static class PathMethods
{
    private static readonly char[] _sanitizeChars = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    public static string SanitizeFileName(string name)
    {
        foreach (char c in _sanitizeChars)
        {
            name = name.Replace(c.ToString(), "");
        }
        return name.Trim().TrimEnd('.');
    }
}
