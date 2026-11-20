using System.Text;

namespace BD3DOrchestrator;

public static class TclMethods
{
    public static string Quote(string value)
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
}
