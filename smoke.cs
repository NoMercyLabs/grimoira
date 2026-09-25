#:package Microsoft.Data.Sqlite@9.0.0
// Substrate smoke test for the AITM foundation: prove file-based C# + NuGet + SQLite.
// The FTS5 check that used to live here moved to
// Aitm.Store.Tests.SchemaRunnerFts5Tests.Fts5WorksThroughTheRunner (RESTRUCTURE.md slice 3c), which
// proves FTS5 on the schema the runner itself creates, not an ad hoc table.
using Microsoft.Data.Sqlite;

using SqliteConnection con = new("Data Source=:memory:");
con.Open();

object? Scalar(string sql)
{
    using SqliteCommand cmd = con.CreateCommand();
    cmd.CommandText = sql;
    return cmd.ExecuteScalar();
}

Console.WriteLine($"file-based C# + NuGet OK. sqlite {Scalar("select sqlite_version()")}");
