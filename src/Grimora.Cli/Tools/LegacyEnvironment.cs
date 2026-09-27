namespace Grimora.Cli.Tools;

/// <summary>Compat shim (slice 32c): the env vars were <c>AITM_*</c>; they are <c>GRIMORA_*</c> now. For one
/// release an old variable still counts when its new twin is unset. Call once, first thing in a process.
/// The CLI cannot reference Grimora.Store, so it keeps its own copy.</summary>
public static class LegacyEnvironment
{
    private const string OldPrefix = "AITM_";
    private const string NewPrefix = "GRIMORA_";

    public static void Promote()
    {
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is not string name || !name.StartsWith(OldPrefix, StringComparison.OrdinalIgnoreCase))
                continue;
            string twin = NewPrefix + name[OldPrefix.Length..];
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(twin)))
                Environment.SetEnvironmentVariable(twin, entry.Value as string);
        }
    }
}
