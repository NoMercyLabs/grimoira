using Microsoft.Data.Sqlite;

namespace Aitm.Brain.Data;

/// <summary>
/// Resolves a project short alias (e.g. "web") to its node key (e.g. "proj:web"), or passes a key
/// straight through. Copied verbatim from aitm.cs's <c>NormalizeProject</c> (aitm.cs:1363) and mcp.cs's
/// <c>NormProj</c> (mcp.cs:568) — the two are byte-for-byte the same lookup, so <c>brain scope</c> and
/// <c>brain common</c> (CLI and MCP) share this one copy rather than duplicating it twice more.
/// </summary>
public static class BrainProjects
{
    public static string Normalize(SqliteConnection connection, string input)
    {
        string t = input.Trim();
        if (t.Contains(':')) return t;
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT k FROM proj_alias WHERE short=$s";
        command.Parameters.AddWithValue("$s", t);
        return command.ExecuteScalar() is string k ? k : "proj:" + t;
    }

    /// <summary>Splits a space/comma/semicolon-separated project list. Copied verbatim from mcp.cs's
    /// <c>SplitArgs</c> (mcp.cs:565).</summary>
    public static List<string> SplitArgs(string s) =>
        s.Split([' ', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
}
