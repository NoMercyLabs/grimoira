namespace Grimora.Cli.Tools;

/// <summary>Compat shim (slice 32c): the env vars were <c>AITM_*</c>, then <c>GRIMORA_*</c>; they are <c>GRIMOIRA_*</c> now.
/// For one release an old variable still counts when its new twin is unset (a <c>GRIMORA_*</c> one beats an <c>AITM_*</c> one). Call once, first thing in a process.
/// The CLI cannot reference Grimora.Store, so it keeps its own copy.</summary>
public static class LegacyEnvironment
{
    private static readonly string[] OldPrefixes = ["GRIMORA_", "AITM_"];
    private const string NewPrefix = "GRIMOIRA_";

    public static void Promote()
    {
        foreach (string oldPrefix in OldPrefixes)
        {
            foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
            {
                if (entry.Key is not string name || !name.StartsWith(oldPrefix, StringComparison.OrdinalIgnoreCase))
                    continue;
                string twin = NewPrefix + name[oldPrefix.Length..];
                if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(twin)))
                    Environment.SetEnvironmentVariable(twin, entry.Value as string);
            }
        }
    }
}
