using Aitm.Hooks.Data;
using Aitm.Hooks.Tools;
using Aitm.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Aitm.Hooks.Tests;

/// <summary>
/// RESTRUCTURE.md slice 22 ("Hooks, part 3"): pattern-watch.mjs stops printing a nudge and only
/// records (docs/RESTRUCTURE.md:268); <see cref="PatternsTool"/> is the new read tool that lists what
/// crossed the threshold, so what pattern-watch records stays visible on request.
/// </summary>
public class PatternsToolTests
{
    [Fact]
    public void ListsARecordedPatternThatCrossedTheThreshold()
    {
        string instance = AitmCliRunner.NewTestInstance("patterns");
        try
        {
            AitmCliRunner.Run($"init --instance {instance}");
            using (SqliteConnection connection = new($"Data Source={HookPaths.DbPath(instance)}"))
            {
                connection.Open();
                using SqliteCommand create = connection.CreateCommand();
                create.CommandText = "CREATE TABLE IF NOT EXISTS patterns(" +
                    "sig TEXT PRIMARY KEY, sample TEXT, count INTEGER NOT NULL DEFAULT 1, " +
                    "first_ts TEXT, last_ts TEXT, promoted INTEGER NOT NULL DEFAULT 0, " +
                    "kind TEXT NOT NULL DEFAULT 'command')";
                create.ExecuteNonQuery();
                using SqliteCommand insert = connection.CreateCommand();
                insert.CommandText = "INSERT INTO patterns(sig, sample, count, first_ts, last_ts, promoted, kind) " +
                    "VALUES('dotnet build', 'dotnet build aitm.cs', 5, 't', 't', 0, 'command')";
                insert.ExecuteNonQuery();
            }

            using SqliteConnection read = new($"Data Source={HookPaths.DbPath(instance)}");
            read.Open();
            string result = new PatternsTool().ExecuteCli(read);

            Assert.Contains("dotnet build", result);
            Assert.Contains("worth codifying", result);
        }
        finally
        {
            AitmCliRunner.DeleteInstance(instance);
        }
    }

    [Fact]
    public void NothingRecordedYetIsReportedPlainly()
    {
        string instance = AitmCliRunner.NewTestInstance("patterns-empty");
        try
        {
            AitmCliRunner.Run($"init --instance {instance}");
            using SqliteConnection read = new($"Data Source={HookPaths.DbPath(instance)}");
            read.Open();
            string result = new PatternsTool().ExecuteCli(read);

            Assert.Equal("nothing recorded yet.", result);
        }
        finally
        {
            AitmCliRunner.DeleteInstance(instance);
        }
    }
}
