#:package Microsoft.Data.Sqlite@9.0.0
// Substrate smoke test for the AITM foundation: prove file-based C# + NuGet + SQLite,
// and whether the bundled SQLite has FTS5 (ranked full-text search Node lacked).
using System.Diagnostics;
using Microsoft.Data.Sqlite;

using SqliteConnection con = new("Data Source=:memory:");
con.Open();

void Exec(string sql)
{
    using SqliteCommand cmd = con.CreateCommand();
    cmd.CommandText = sql;
    cmd.ExecuteNonQuery();
}

object? Scalar(string sql)
{
    using SqliteCommand cmd = con.CreateCommand();
    cmd.CommandText = sql;
    return cmd.ExecuteScalar();
}

Console.WriteLine($"file-based C# + NuGet OK. sqlite {Scalar("select sqlite_version()")}");

try
{
    Exec("CREATE VIRTUAL TABLE f USING fts5(term, value)");
    Exec("INSERT INTO f(term,value) VALUES ('media base url','https://raw.githubusercontent.com/NoMercy-Entertainment/nomercy-media/master')");
    Stopwatch sw = Stopwatch.StartNew();
    object? hit = Scalar("SELECT value FROM f WHERE f MATCH 'media url' ORDER BY bm25(f) LIMIT 1");
    sw.Stop();
    Console.WriteLine($"FTS5: AVAILABLE. ranked match in {sw.Elapsed.TotalMilliseconds:F2}ms -> {hit}");
}
catch (Exception ex)
{
    Console.WriteLine($"FTS5: NOT available ({ex.Message}) — fall back to LIKE + JS-style ranking");
}
