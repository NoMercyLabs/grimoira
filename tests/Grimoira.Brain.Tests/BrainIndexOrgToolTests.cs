using Grimoira.Brain.Data;
using Grimoira.Brain.Tools;
using Grimoira.Store.Data;
using Grimoira.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Grimoira.Brain.Tests;

/// <summary>
/// index-org.mjs (RESTRUCTURE.md slice 19, part 3) against a fake <see cref="IProcessRunner"/> — never
/// the real <c>gh</c> CLI or the network. Oracle is the script's own gloss and node shape
/// (index-org.mjs:76-118).
/// </summary>
public class BrainIndexOrgToolTests
{
    private sealed class FakeRunner : IProcessRunner
    {
        public string GhJson = "[]";
        public string GitRemoteUrl = "";
        public int GhExit = 0;
        public string GhStderr = "";

        public (string Stdout, string Stderr, int ExitCode) Run(string fileName, IReadOnlyList<string> args, string? workingDirectory = null, TimeSpan? timeout = null)
        {
            if (fileName == "gh") return (GhJson, GhStderr, GhExit);
            if (fileName == "git" && args.Contains("remote")) return (GitRemoteUrl, "", GitRemoteUrl.Length > 0 ? 0 : 1);
            return ("", "unexpected command", 1);
        }
    }

    [Fact]
    public void IndexesAnOrgAndMatchesALocalCloneByRemoteUrl()
    {
        string instance = GrimoiraCliRunner.NewTestInstance("index-org");
        string tmp = Path.Combine(Path.GetTempPath(), $"grimoira-org-test-{Guid.NewGuid():N}");
        string cloneDir = Path.Combine(tmp, "nomercy-app-web");
        Directory.CreateDirectory(Path.Combine(cloneDir, ".git"));
        try
        {
            GrimoiraCliRunner.Run($"init --instance {instance}");
            using SqliteConnection connection = StoreConnection.Open(GrimoiraCliRunner.InstanceDbPath(instance));

            FakeRunner runner = new()
            {
                GhJson = """
                    [{"name":"nomercy-app-web","description":"the web client","primaryLanguage":{"name":"TypeScript"},
                      "isPrivate":false,"isArchived":false,"isFork":false,
                      "defaultBranchRef":{"name":"master"},"pushedAt":"2026-01-02T00:00:00Z",
                      "url":"https://github.com/NoMercy-Entertainment/nomercy-app-web","repositoryTopics":[]}]
                    """,
                GitRemoteUrl = "https://github.com/NoMercy-Entertainment/nomercy-app-web",
            };

            string outPath = Path.Combine(tmp, "org.json");
            string result = new BrainIndexOrgTool().ExecuteCli(
                connection, ["NoMercy-Entertainment"], [tmp], outPath, dry: false, runner);

            Assert.Contains("indexing 1 org(s)", result);
            Assert.Contains("found 1 local clone(s)", result);
            Assert.Contains("NoMercy-Entertainment: 1 repo(s)", result);
            Assert.Contains("1 repo(s): 1 cloned, 0 archived, 0 fork(s)", result);
            Assert.Contains("imported spine: 2 nodes, 0 slots, 1 links, 0 alias(es), 0 edge(s).", result);

            using SqliteCommand cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT gloss FROM node WHERE k = 'repo:NoMercy-Entertainment/nomercy-app-web' AND valid_to IS NULL";
            string gloss = (string)cmd.ExecuteScalar()!;
            Assert.Contains("the web client", gloss);
            Assert.Contains($"cloned at {cloneDir.Replace('\\', '/')}", gloss);
            Assert.Contains("language TypeScript", gloss);
            Assert.Contains("public", gloss);
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(instance);
            Directory.Delete(tmp, recursive: true);
        }
    }

    [Fact]
    public void DryRunWritesTheFragmentButNeverImports()
    {
        string instance = GrimoiraCliRunner.NewTestInstance("index-org-dry");
        string tmp = Path.Combine(Path.GetTempPath(), $"grimoira-org-dry-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        try
        {
            GrimoiraCliRunner.Run($"init --instance {instance}");
            using SqliteConnection connection = StoreConnection.Open(GrimoiraCliRunner.InstanceDbPath(instance));
            FakeRunner runner = new() { GhJson = "[]" };

            string outPath = Path.Combine(tmp, "org.json");
            string result = new BrainIndexOrgTool().ExecuteCli(
                connection, ["EmptyOrg"], [tmp], outPath, dry: true, runner);

            Assert.Contains("(dry run — not imported)", result);
            Assert.True(File.Exists(outPath));

            using SqliteCommand cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT count(*) FROM node WHERE valid_to IS NULL";
            Assert.Equal(0L, (long)cmd.ExecuteScalar()!);
        }
        finally
        {
            GrimoiraCliRunner.DeleteInstance(instance);
            Directory.Delete(tmp, recursive: true);
        }
    }

    [Fact]
    public void AFailedGhCallIsReportedWithItsStderrAndWritesAndImportsNothing()
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"grimoira-org-fail-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        try
        {
            // The store is left without a schema on purpose: an import attempt would throw.
            using SqliteConnection connection = StoreConnection.Open(Path.Combine(tmp, "empty.db"));
            FakeRunner runner = new() { GhJson = "", GhExit = 1, GhStderr = "gh: not logged in" };

            string outPath = Path.Combine(tmp, "org.json");
            string result = new BrainIndexOrgTool().ExecuteCli(
                connection, ["SomeOrg"], [tmp], outPath, dry: false, runner);

            Assert.Contains("error: SomeOrg: gh repo list failed (exit 1): gh: not logged in", result);
            Assert.DoesNotContain("0 repo(s)", result);
            Assert.False(File.Exists(outPath));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(tmp, recursive: true);
        }
    }

    [Fact]
    public void AListingThatHitsTheLimitIsFlaggedAsTruncated()
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"grimoira-org-limit-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        try
        {
            using SqliteConnection connection = StoreConnection.Open(Path.Combine(tmp, "empty.db"));
            string items = string.Join(",", Enumerable.Range(0, 500).Select(i => $"{{\"name\":\"r{i}\",\"url\":\"https://github.com/Big/r{i}\"}}"));
            FakeRunner runner = new() { GhJson = $"[{items}]" };

            string result = new BrainIndexOrgTool().ExecuteCli(
                connection, ["Big"], [tmp], Path.Combine(tmp, "org.json"), dry: true, runner);

            Assert.Contains("warning: Big: 500 repo(s) is the listing limit, the org may have more", result);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(tmp, recursive: true);
        }
    }
}
