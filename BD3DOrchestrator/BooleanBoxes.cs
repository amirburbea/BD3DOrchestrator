namespace BD3DOrchestrator;

internal static class BooleanBoxes
{
    /// <summary>Cached boxed <c>true</c>.</summary>
    internal static readonly object True = true;

    /// <summary>Cached boxed <c>false</c>.</summary>
    internal static readonly object False = false;

    internal static object Box(bool value) => value ? True : False;
}
