using System.Text.Json;
using Xunit;

namespace Aitm.Layout.Tests;

// A hook runs on every prompt, edit or session end, so its start cost is paid again and again.
// `dotnet run` builds the project before it runs: about 3 s warm, and past the 10 s timeout on a
// fresh plugin install that has no build output yet. A hook command must start a built program.
public class HookCommandTests
{
    [Fact]
    public void NoHookBuildsBeforeItRuns()
    {
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoPaths.Root, "hooks", "hooks.json")));
        List<string> offenders = [];
        foreach (JsonProperty ev in doc.RootElement.GetProperty("hooks").EnumerateObject())
        foreach (JsonElement group in ev.Value.EnumerateArray())
        foreach (JsonElement hook in group.GetProperty("hooks").EnumerateArray())
        {
            string command = hook.TryGetProperty("command", out JsonElement c) ? c.GetString() ?? "" : "";
            List<string> args = hook.TryGetProperty("args", out JsonElement a)
                ? a.EnumerateArray().Select(x => x.GetString() ?? "").ToList()
                : [];
            bool builds = (command == "dotnet" && args.FirstOrDefault() is "run" or "build")
                || command.StartsWith("dotnet run", StringComparison.Ordinal);
            if (builds) offenders.Add($"{ev.Name}: {command} {string.Join(' ', args)}");
        }
        Assert.True(offenders.Count == 0, "hooks that build before they run:\n" + string.Join('\n', offenders));
    }
}
