extern alias cli;

using Xunit;

namespace Grimora.Layout.Tests;

// Reviewer finding (chatgpt/codex/gemini/vscode-chat, 2026-09-27): instance resolution is inconsistent
// across entry points. The CLI/MCP path (Grimora.Cli.Tools.ServerAddress/ServerAutoStart) and the shared
// server (Grimora.Server.Data.RequestProjectResolver -> Grimora.Store.Data.StoreConnection.ResolveInstance)
// both honour GRIMORA_INSTANCE and GRIMORA_DATA_DIR. Grimora.Hooks.Data.HookPaths used to derive the
// instance a third way (ignoring GRIMORA_INSTANCE entirely) and hardcode ~/.grimora (ignoring
// GRIMORA_DATA_DIR), so a hook run and a CLI/MCP call in the very same session could land in two
// different data directories. This test starts hooks and the CLI/server with identical env vars and
// asserts they resolve to the identical instance name and instance directory.
[Collection("EnvironmentVariables")]
public sealed class HooksResolveTheSameInstanceAsCliAndServerTests
{
    [Fact]
    public void GrimoraInstanceEnvVarResolvesIdenticallyEverywhere()
    {
        using EnvVarScope scope = new(("GRIMORA_INSTANCE", "some-other-project"), ("GRIMORA_DATA_DIR", null));

        string hookInstance = Grimora.Hooks.Data.HookPaths.ResolveInstance(cwd: "C:/Projects/unrelated-folder");
        string cliLinkedHookInstance = cli::Grimora.Hooks.Data.HookPaths.ResolveInstance(cwd: "C:/Projects/unrelated-folder");
        string storeInstance = Grimora.Store.Data.StoreConnection.ResolveInstance();

        Assert.Equal("some-other-project", hookInstance);
        Assert.Equal(storeInstance, hookInstance);
        Assert.Equal(cliLinkedHookInstance, hookInstance);
    }

    [Fact]
    public void GrimoraDataDirEnvVarPutsTheHookInstanceDirInTheSamePlaceAsCliAndServer()
    {
        string customDataDir = Path.Combine(Path.GetTempPath(), "grimora-test-data-dir-" + Guid.NewGuid());
        using EnvVarScope scope = new(("GRIMORA_INSTANCE", "acme"), ("GRIMORA_DATA_DIR", customDataDir));

        string hookInstanceDir = Grimora.Hooks.Data.HookPaths.InstanceDir("acme");
        string cliLinkedHookInstanceDir = cli::Grimora.Hooks.Data.HookPaths.InstanceDir("acme");
        string cliInstanceDir = Path.Combine(cli::Grimora.Cli.Tools.ServerAddress.ResolveDataDir(), "acme");
        string serverInstanceDir = Path.Combine(Grimora.Server.Data.ServerAddress.ResolveDataDir(), "acme");

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
