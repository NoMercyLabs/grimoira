using Aitm.Graph.Tools;
using Aitm.Store.Data;
using Aitm.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Aitm.Graph.Tests;

public class ProjectToolTests
{
    [Fact]
    public void MatchesTodaysCliOutputAndRegistersTheProject()
    {
        string oldInstance = AitmCliRunner.NewTestInstance("project-old");
        string newInstance = AitmCliRunner.NewTestInstance("project-new");
        try
        {
            // Oracle: today's aitm.cs case "project" (aitm.cs:160-166).
            AitmCliRunner.Run($"init --instance {oldInstance}");
            (string stdout, int exitCode) = AitmCliRunner.Run(
                $"project --instance {oldInstance} --name web --root /repo/web --lang ts --globs \"*.ts,*.tsx\"");
            Assert.Equal(0, exitCode);
            string expected = stdout.Trim();

            // New: ProjectTool.
            AitmCliRunner.Run($"init --instance {newInstance}");
            string dbPath = AitmCliRunner.InstanceDbPath(newInstance);
            string actual;
            using (SqliteConnection connection = StoreConnection.Open(dbPath))
            {
                actual = new ProjectTool().Execute(connection, "web", "/repo/web", "ts", "*.ts,*.tsx").Trim();
            }

            Assert.Equal(expected, actual);
            Assert.Equal("project 'web' registered.", actual);

            using SqliteConnection check = new($"Data Source={dbPath};Mode=ReadOnly");
            check.Open();
            using SqliteCommand read = check.CreateCommand();
            read.CommandText = "SELECT root,lang,globs FROM projects WHERE name='web'";
            using SqliteDataReader reader = read.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal("/repo/web", reader.GetString(0));
            Assert.Equal("ts", reader.GetString(1));
            Assert.Equal("*.ts,*.tsx", reader.GetString(2));
        }
        finally
        {
            AitmCliRunner.DeleteInstance(oldInstance);
            AitmCliRunner.DeleteInstance(newInstance);
        }
    }

    [Fact]
    public void RegisteringTheSameNameTwiceUpdatesInPlace()
    {
        string instance = AitmCliRunner.NewTestInstance("project-update");
        try
        {
            AitmCliRunner.Run($"init --instance {instance}");
            string dbPath = AitmCliRunner.InstanceDbPath(instance);
            using SqliteConnection connection = StoreConnection.Open(dbPath);

            new ProjectTool().Execute(connection, "web", "/repo/web", "ts", "*.ts");
            new ProjectTool().Execute(connection, "web", "/repo/web-v2", "ts", "*.ts,*.tsx");

            using SqliteCommand count = connection.CreateCommand();
            count.CommandText = "SELECT count(*) FROM projects";
            Assert.Equal(1L, (long)(count.ExecuteScalar() ?? 0L));

            using SqliteCommand read = connection.CreateCommand();
            read.CommandText = "SELECT root,globs FROM projects WHERE name='web'";
            using SqliteDataReader reader = read.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal("/repo/web-v2", reader.GetString(0));
            Assert.Equal("*.ts,*.tsx", reader.GetString(1));
        }
        finally
        {
            AitmCliRunner.DeleteInstance(instance);
        }
    }
}
