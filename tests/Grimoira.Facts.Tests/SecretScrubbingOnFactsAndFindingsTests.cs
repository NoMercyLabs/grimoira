using Grimoira.Facts.Tools;
using Grimoira.Store.Data;
using Grimoira.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Grimoira.Facts.Tests;

/// <summary>
/// Reviewer finding (chatgpt/codex/gemini/vscode-chat, 2026-09-27): <c>IndexChatTool</c> scrubs
/// token-shaped secrets before a chat message reaches the store, but recording a fact (<c>AddTool</c>) or
/// a finding (<c>FindingTool</c>) did not — a pasted token in either went straight into SQLite.
/// </summary>
public class SecretScrubbingOnFactsAndFindingsTests
{
    private const string PastedGithubToken = "ghp_1234567890abcdefghij1234567890abcdef";

    [Fact]
    public void APastedSecretInAFactsValueIsScrubbedBeforeItReachesSqlite()
    {
        string instance = GrimoiraCliRunner.NewTestInstance("scrub-add-fact");
        try
        {
            GrimoiraCliRunner.Seed($"init --instance {instance}");
            using SqliteConnection connection = StoreConnection.Open(GrimoiraCliRunner.InstanceDbPath(instance));

            new AddTool().Execute(connection, "scrub-add-fact-term", "", "misc",
                $"a value with a leaked token: {PastedGithubToken}", "test", "", "stated");

            using SqliteCommand read = connection.CreateCommand();
            read.CommandText = "SELECT value FROM facts WHERE k='scrub-add-fact-term'";
            string value = (string)read.ExecuteScalar()!;

            Assert.DoesNotContain(PastedGithubToken, value);
            Assert.Contains("[redacted:github]", value);
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(instance);
        }
    }

    [Fact]
    public void APastedSecretInAFindingsDetailIsScrubbedBeforeItReachesSqlite()
    {
        string instance = GrimoiraCliRunner.NewTestInstance("scrub-finding");
        try
        {
            GrimoiraCliRunner.Seed($"init --instance {instance}");
            using SqliteConnection connection = StoreConnection.Open(GrimoiraCliRunner.InstanceDbPath(instance));

            new FindingTool().ExecuteMcp(connection, "a finding title",
                $"detail with a leaked token: {PastedGithubToken}", "test");

            using SqliteCommand read = connection.CreateCommand();
            read.CommandText = "SELECT detail FROM findings ORDER BY id DESC LIMIT 1";
            string detail = (string)read.ExecuteScalar()!;

            Assert.DoesNotContain(PastedGithubToken, detail);
            Assert.Contains("[redacted:github]", detail);
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(instance);
        }
    }
}
