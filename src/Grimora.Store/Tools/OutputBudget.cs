namespace Grimora.Store.Tools;

/// <summary>
/// The hard ceiling on a whole tool answer (RESTRUCTURE.md section 5, "every answer has an output
/// budget"). Copied verbatim from mcp.cs's <c>Budget</c> (mcp.cs:128): trims at a line boundary and
/// says so, so a flooded result reads as "narrow the query", never as "that was everything".
/// </summary>
public static class OutputBudget
{
    public const int DefaultCap = 1800;

    public static string Clip(string text, int cap = DefaultCap)
    {
        if (text.Length <= cap) return text;
        int cut = text.LastIndexOf('\n', cap);
        if (cut < cap / 2) cut = cap;
        int dropped = text[cut..].Count(ch => ch == '\n');
        return text[..cut] + $"\n(+{dropped} more line(s) trimmed — narrow the query)";
    }
}
