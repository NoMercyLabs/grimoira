extern alias cli;

using Xunit;

namespace Grimoira.Layout.Tests;

// Reviewer finding (chatgpt/codex/gemini/vscode-chat, 2026-09-27): hooks hardcoded ~/.grimoira
// (Grimoira.Hooks.Data.HookPaths), ignoring GRIMOIRA_DATA_DIR, so a hook run and a CLI/MCP call in the very
// same session could land in two different data directories. This test starts hooks and the CLI/server
// with an identical GRIMOIRA_DATA_DIR and asserts they resolve to the identical instance directory.
//
// GRIMOIRA_INSTANCE is deliberately NOT part of that guarantee (a follow-up fix to the first version of
// this change, which did make HookPaths honour it and broke SessionEnd/PostToolUse/Stop for real: those
// handlers run inside the ONE shared Grimoira.Server process, so reading GRIMOIRA_INSTANCE from "the"
// environment there reads whichever session's env the server happened to inherit at ITS OWN startup, not
// the requesting session's — silently misrouting that request's writes to a wrong instance under the
// server's normal multi-project operation. RequestProjectResolver already avoids this exact mistake for
// the HTTP-facing entry points ("It never reads the server process's own environment"); HookPaths must not
// reintroduce it. See GrimoiraInstanceEnvVarIsIgnoredByHookPathsOnPurpose below.
[Collection("EnvironmentVariables")]
public sealed class HooksResolveTheSameInstanceAsCliAndServerTests
{
    [Fact]
    public void GrimoiraInstanceEnvVarIsIgnoredByHookPathsOnPurpose()
    {
        using EnvVarScope scope = new(("GRIMOIRA_INSTANCE", "some-other-project"), ("GRIMOIRA_DATA_DIR", null));

        // Unlike the CLI/MCP path (Grimoira.Store.Data.StoreConnection.ResolveInstance, which does honour
        // GRIMOIRA_INSTANCE), HookPaths always resolves from cwd/projectDir — see the class comment above.
        string hookInstance = Grimoira.Hooks.Data.HookPaths.ResolveInstance(cwd: "C:/Projects/unrelated-folder");
        string cliLinkedHookInstance = cli::Grimoira.Hooks.Data.HookPaths.ResolveInstance(cwd: "C:/Projects/unrelated-folder");

        Assert.Equal("unrelated-folder", hookInstance);
        Assert.Equal(hookInstance, cliLinkedHookInstance);
    }

    [Fact]
    public void GrimoiraDataDirEnvVarPutsTheHookInstanceDirInTheSamePlaceAsCliAndServer()
    {
        string customDataDir = Path.Combine(Path.GetTempPath(), "grimoira-test-data-dir-" + Guid.NewGuid());
        using EnvVarScope scope = new(("GRIMOIRA_INSTANCE", "acme"), ("GRIMOIRA_DATA_DIR", customDataDir));

        string hookInstanceDir = Grimoira.Hooks.Data.HookPaths.InstanceDir("acme");
        string cliLinkedHookInstanceDir = cli::Grimoira.Hooks.Data.HookPaths.InstanceDir("acme");
        string cliInstanceDir = Path.Combine(cli::Grimoira.Cli.Tools.ServerAddress.ResolveDataDir(), "acme");
        string serverInstanceDir = Path.Combine(Grimoira.Server.Data.ServerAddress.ResolveDataDir(), "acme");

        Assert.Equal(cliInstanceDir, hookInstanceDir);
        Assert.Equal(serverInstanceDir, hookInstanceDir);
        Assert.Equal(cliLinkedHookInstanceDir, hookInstanceDir);
        Assert.StartsWith(customDataDir, hookInstanceDir);
    }

    /// <summary>Sets one or more environment variables for the lifetime of the scope and restores whatever
    /// they were before, even a null (unset). Env vars are process-global, so tests using this must run in
    /// the "EnvironmentVariables" collection to never interleave with another test that also touches them.</summary>
    private sealed class EnvVarScope : IDisposable
    {
        private readonly (string Name, string? Previous)[] _previous;

        public EnvVarScope(params (string Name, string? Value)[] vars)
        {
            _previous = [.. vars.Select(v => (v.Name, Environment.GetEnvironmentVariable(v.Name)))];
            foreach ((string name, string? value) in vars) Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose()
        {
            foreach ((string name, string? previous) in _previous) Environment.SetEnvironmentVariable(name, previous);
        }
    }
}

[CollectionDefinition("EnvironmentVariables", DisableParallelization = true)]
public sealed class EnvironmentVariablesCollection;
