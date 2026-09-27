using Grimora.Docs.Tools;
using Grimora.Store.Data;
using Grimora.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Grimora.Docs.Tests;

/// <summary>
/// Reviewer finding (chatgpt/codex/gemini/vscode-chat, 2026-09-27): <c>IndexChatTool</c> scrubs
/// token-shaped secrets before a chat message reaches the store, but <c>IndexDocsTool</c> did not — a doc
/// with a pasted token (a committed example, a leaked credential in a note) went straight into SQLite.
/// </summary>
public class SecretScrubbingOnDocIndexingTests
{
    private const string PastedGithubToken = "ghp_1234567890abcdefghij1234567890abcdef";

    [Fact]
    public void APastedSecretInAnIndexedDocIsScrubbedBeforeItReachesSqlite()
    {
        string instance = GrimoraCliRunner.NewTestInstance("scrub-doc-index");
        string dir = Path.Combine(Path.GetTempPath(), $"scrub-doc-fixture-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            GrimoraCliRunner.Seed($"init --instance {instance}");
            File.WriteAllText(Path.Combine(dir, "note.md"),
                $"# A real heading\n\nEnough body text to survive the compaction filter, with a leaked token: {PastedGithubToken} right here.\n");

            using SqliteConnection connection = StoreConnection.Open(GrimoraCliRunner.InstanceDbPath(instance));
            new IndexDocsTool().Execute(connection, dir, "doc");

            using SqliteCommand read = connection.CreateCommand();
            read.CommandText = "SELECT content FROM docs";
            using SqliteDataReader reader = read.ExecuteReader();
            Assert.True(reader.Read(), "expected at least one indexed doc chunk");
            string content = reader.GetString(0);

            Assert.DoesNotContain(PastedGithubToken, content);
            Assert.Contains("[redacted:github]", content);
        }
        finally
        {
            GrimoraCliRunner.DeleteInstance(instance);
            Directory.Delete(dir, recursive: true);
        }
    }
}
