using Grimoira.Brain.Tools;
using Grimoira.Store.Data;
using Grimoira.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Grimoira.Brain.Tests;

/// <summary>
/// Reviewer finding (chatgpt/codex/gemini/vscode-chat, 2026-09-27): <c>IndexChatTool</c> scrubs
/// token-shaped secrets (<see cref="SecretScrubber"/>) before a chat message reaches the store, but
/// <c>brain_stage</c>/<c>brain_learn</c> did not — a pasted token in a staged or learned node/triple/slot
/// went straight into SQLite. <see cref="Grimoira.Brain.Data.BrainWriters"/> is the one place every brain
/// write (a direct <c>brain_learn</c>, and a staged <c>brain_stage</c> entry replayed by
/// <c>brain_flush</c>) reaches SQLite, so scrubbing there closes both paths — and the "fact"/"rule" node
/// kinds too, since they are ordinary nodes written through the same writer.
/// </summary>
public class SecretScrubbingOnBrainWritesTests
{
    private const string PastedGithubToken = "ghp_1234567890abcdefghij1234567890abcdef";

    [Fact]
    public void ADirectBrainLearnNeverStoresAPastedSecretInLabelOrGloss()
    {
        string instance = GrimoiraCliRunner.NewTestInstance("scrub-learn-node");
        try
        {
            GrimoiraCliRunner.Seed($"init --instance {instance}");
            using SqliteConnection connection = StoreConnection.Open(GrimoiraCliRunner.InstanceDbPath(instance));

            new BrainLearnTool().ExecuteMcp(connection, "node", "scrub-learn-node-k", "fact",
                $"a token leaked here: {PastedGithubToken}", $"gloss also carries {PastedGithubToken}");

            using SqliteCommand read = connection.CreateCommand();
            read.CommandText = "SELECT label, gloss FROM node WHERE k='scrub-learn-node-k'";
            using SqliteDataReader reader = read.ExecuteReader();
            Assert.True(reader.Read());
            string label = reader.GetString(0);
            string gloss = reader.GetString(1);

            Assert.DoesNotContain(PastedGithubToken, label);
            Assert.DoesNotContain(PastedGithubToken, gloss);
            Assert.Contains("[redacted:github]", label);
            Assert.Contains("[redacted:github]", gloss);
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(instance);
        }
    }

    [Fact]
    public void AStagedEntryFlushedIntoTheBrainNeverStoresAPastedSecretEither()
    {
        // This is the path the reviewers named explicitly: brain_stage today, committed by brain_flush —
        // a secret pasted while staging must never survive to the row brain_flush writes.
        string instance = GrimoiraCliRunner.NewTestInstance("scrub-stage-flush");
        try
        {
            GrimoiraCliRunner.Seed($"init --instance {instance}");
            using SqliteConnection connection = StoreConnection.Open(GrimoiraCliRunner.InstanceDbPath(instance));

            new BrainStageTool().ExecuteMcp(connection, "node", "scrub-stage-node-k", "fact",
                $"staged label with a secret: {PastedGithubToken}", sessionId: "scrub-test-session");
            string result = new BrainFlushTool().ExecuteMcp(connection, sessionId: "scrub-test-session");
            Assert.StartsWith("flushed 1 learning", result);

            using SqliteCommand read = connection.CreateCommand();
            read.CommandText = "SELECT label FROM node WHERE k='scrub-stage-node-k'";
            string label = (string)read.ExecuteScalar()!;

            Assert.DoesNotContain(PastedGithubToken, label);
            Assert.Contains("[redacted:github]", label);
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(instance);
        }
    }

    [Fact]
    public void ATriplesBecauseTextIsScrubbedTooBeforeItReachesSqlite()
    {
        string instance = GrimoiraCliRunner.NewTestInstance("scrub-triple-because");
        try
        {
            GrimoiraCliRunner.Seed($"init --instance {instance}");
            using SqliteConnection connection = StoreConnection.Open(GrimoiraCliRunner.InstanceDbPath(instance));
            // A triple's subject/object must both already exist as nodes (a dangling-ref guard rejects
            // otherwise), so seed them first — the scrub test only cares about "because".
            new BrainLearnTool().ExecuteMcp(connection, "node", "scrub-triple-s", "concept", "subject node");
            new BrainLearnTool().ExecuteMcp(connection, "node", "scrub-triple-o", "concept", "object node");
            string learnResult = new BrainLearnTool().ExecuteMcp(connection, "triple", "scrub-triple-s", "related", "scrub-triple-o",
                because: $"because a token leaked: {PastedGithubToken}");
            Assert.Equal("triple learned.", learnResult);

            using SqliteCommand read = connection.CreateCommand();
            read.CommandText = "SELECT because FROM triple ORDER BY id DESC LIMIT 1";
            string because = (string)read.ExecuteScalar()!;

            Assert.DoesNotContain(PastedGithubToken, because);
            Assert.Contains("[redacted:github]", because);
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(instance);
        }
    }
}
