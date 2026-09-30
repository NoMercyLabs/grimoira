using System.Collections.Concurrent;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Grimora.Store.Data;

namespace Grimora.TestSupport;

/// <summary>
/// The frozen answers of an oracle (the old file-based CLI, or a pinned commit's build). A test class that
/// used to compare against a running oracle now compares against <c>Goldens/&lt;TestFile&gt;.jsonl</c> beside
/// its source: one JSON line per oracle call, keyed by test method, the normalised arguments and the call's
/// occurrence. The first line says who wrote it and never the new code. GrimoraCliRunner.Run only ever
/// replays now (no oracle process runs); OldVsNewCli and CliGoldens.Frozen can still freeze a new golden
/// with GRIMORA_FREEZE=1 from their own pinned-commit oracle.
/// </summary>
public static partial class CliGoldens
{
    public sealed record Entry(string Member, string Args, string Stdout, string Stderr, int ExitCode, string RawArgs = "");

    public sealed class MismatchException(string message) : Exception(message);

    public static bool FreezeMode { get; } = Environment.GetEnvironmentVariable("GRIMORA_FREEZE") == "1";

    private const string Header =
        "GOLDEN written by the oracle bin-cli-old (grimora.cs at commit 0b2bb70, the last file-based build); never by the new code";

    private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static readonly ConcurrentDictionary<string, List<Entry>> Loaded = new();
    private static readonly ConcurrentDictionary<string, int> Seen = new();
    private static readonly Lock WriteLock = new();

    /// <summary>True once the class has been frozen. Until then a class still runs the live oracle.</summary>
    public static bool HasGolden(string callerFile) => File.Exists(GoldenPath(callerFile));

    public static string GoldenPath(string callerFile) =>
        Path.Combine(Path.GetDirectoryName(callerFile)!, "Goldens", Path.GetFileNameWithoutExtension(callerFile) + ".jsonl");

    /// <summary>Splits a command line the way a shell would for the double-quoted arguments the tests use.</summary>
    public static string[] SplitArguments(string arguments)
    {
        List<string> parts = [];
        StringBuilder current = new();
        bool inQuotes = false, started = false;
        foreach (char c in arguments)
        {
            if (c == '"') { inQuotes = !inQuotes; started = true; }
            else if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (started) { parts.Add(current.ToString()); current.Clear(); started = false; }
            }
            else { current.Append(c); started = true; }
        }
        if (started) parts.Add(current.ToString());
        return [.. parts];
    }

    private const string Crlf = "\r\n";
    private const char Backslash = (char)92;

    private static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static readonly string HomeForward = Home.Replace(Path.DirectorySeparatorChar, (char)47);

    /// <summary>What is written to a golden: the oracle's own text, with only the machine's own habits
    /// (home folder, path separator, line ending, decimal mark) swapped for markers, so it can be read back on
    /// any machine. The old product name becomes the new one, as the old comparison did.</summary>
    public static string Portable(string text)
    {
        string result = text.Replace("aitm", "grimora", StringComparison.Ordinal);
        if (Home.Length > 0)
        {
            result = result.Replace(Home, "<HOME>", StringComparison.OrdinalIgnoreCase)
                .Replace(HomeForward, "<HOMEF>", StringComparison.OrdinalIgnoreCase);
        }
        result = Decimal().Replace(result, "$1<DEC>$2");
        if (Environment.NewLine == Crlf) result = result.Replace(Crlf, "<NL>", StringComparison.Ordinal);
        if (Path.DirectorySeparatorChar == Backslash) result = result.Replace(Backslash.ToString(), "<SEP>", StringComparison.Ordinal);
        return result;
    }

    /// <summary>The golden as this machine would have printed it.</summary>
    public static string Expand(string portable) => portable
        .Replace("<HOME>", Home, StringComparison.Ordinal)
        .Replace("<HOMEF>", HomeForward, StringComparison.Ordinal)
        .Replace("<SEP>", Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
        .Replace("<NL>", Environment.NewLine, StringComparison.Ordinal)
        .Replace("<DEC>", System.Globalization.CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator, StringComparison.Ordinal);

    /// <summary>The comparison form: what may differ between runs or machines (random ids, timestamps,
    /// elapsed milliseconds, home folder, separators, line endings, decimal mark) is made equal.</summary>
    // The OS temp folder is not derived from the home folder on every platform (Linux's is `/tmp`, unrelated
    // to `$HOME`; Windows nests it under the home folder as `<HOME>/AppData/Local/Temp`), so a golden frozen
    // on one platform still names the other platform's shape literally unless both collapse to one token.
    // This runs on the forward-slash form, after every backslash is already turned into a slash below.
    private static readonly string WindowsTempUnderHome = "<HOME>/AppData/Local/Temp";
    private static readonly string TempDirForward = Path.GetTempPath()
        .TrimEnd(Path.DirectorySeparatorChar).Replace(Path.DirectorySeparatorChar, '/');

    public static string Canonical(string text)
    {
        string result = Expand(Portable(text)).Replace(Crlf, "\n", StringComparison.Ordinal);
        result = result.Replace(Home, "<HOME>", StringComparison.OrdinalIgnoreCase)
            .Replace(HomeForward, "<HOME>", StringComparison.OrdinalIgnoreCase);
        result = InstanceId().Replace(result, "<GUID>");
        result = Elapsed().Replace(result, "<MS>ms");
        result = Timestamp().Replace(result, "<TS>");
        result = CompactTimestamp().Replace(result, "<TS>");
        // A gap's display date is the UTC day on which its test fixture was seeded. Frozen goldens
        // and live runs can be on different days, even while retaining the same output shape.
        result = GapLastDate().Replace(result, "(last <DATE>)");
        result = RandomTempDir().Replace(result, "grimora-$1-<RAND>");
        result = result.Replace(Backslash.ToString(), "/", StringComparison.Ordinal);
        if (TempDirForward.Length > 0)
            result = result.Replace(TempDirForward, "<TEMP>", StringComparison.OrdinalIgnoreCase);
        result = result.Replace(WindowsTempUnderHome, "<TEMP>", StringComparison.OrdinalIgnoreCase);
        // Path.GetFullPath resolves a rooted Unix-style input path ("/no/such/dir") against the current
        // drive on Windows ("C:/no/such/dir") but leaves it as is on Linux/macOS: the same input, two
        // equally correct answers. A drive letter never means anything else in this CLI's own output.
        return WindowsDriveLetter().Replace(result, "/");
    }

    /// <summary>The same drive-letter fix <see cref="Canonical"/> applies, exposed standalone for
    /// OldVsNewCli.Run: its parity tests diff a live run's stdout/stderr directly against a replayed
    /// golden (frozen on whichever OS ran GRIMORA_FREEZE=1), never through Canonical() itself, so a
    /// "/no/such/dir" argument that Windows resolved to "C:/no/such/dir" at freeze time would otherwise
    /// still carry that drive letter when replayed against a Linux run's driveless "/no/such/dir".
    /// Applying it to both sides of every such comparison is safe: it is a no-op unless the spurious
    /// drive-letter shape is actually present.</summary>
    public static string StripDriveLetter(string text) => WindowsDriveLetter().Replace(text, "/");

    /// <summary>For a test whose oracle is a function (an mcp.dll call), not a CLI line: in freeze mode runs
    /// <paramref name="oracle"/> and writes its result as the golden for <paramref name="key"/>; otherwise
    /// returns that golden without running any oracle.</summary>
    public static string Frozen(string key, Func<string> oracle,
        [System.Runtime.CompilerServices.CallerFilePath] string callerFile = "",
        [System.Runtime.CompilerServices.CallerMemberName] string callerMember = "")
    {
        if (!FreezeMode && !HasGolden(callerFile)) return oracle();
        if (FreezeMode)
        {
            string value = oracle();
            Record(callerFile, callerMember, key, value, "", 0);
            return Expand(Portable(value));
        }
        return Expand(Take(callerFile, callerMember, key).Stdout);
    }

    public const string InstanceMarker = "<INSTANCE>";

    /// <summary>The golden's text as this run should see it: the frozen run's random arguments (fixture
    /// folders, ids) are swapped for the arguments this run passed, the frozen instance name for this instance.</summary>
    public static string ForThisRun(Entry golden, string text, string arguments, string? instance, string callerFile, string callerMember)
    {
        string result = Expand(text);
        if (instance is not null) result = result.Replace(InstanceMarker, instance, StringComparison.Ordinal);
        string[] frozen = SplitArguments(Expand(golden.RawArgs));
        string[] current = SplitArguments(arguments);
        // The swaps belong to one test: a folder named by an earlier call of the same test (project --root)
        // shows up in a later call's output, so every swap that test learned so far applies, longest first.
        // Test classes run in parallel, so each test keeps its own map (a swap learned by another test must
        // not rewrite this one's output), and the map is snapshotted before sorting: sorting the live
        // dictionary while another thread adds to it threw ArgumentException out of Enumerable.ToArray.
        ConcurrentDictionary<string, string> swaps = Replacements.GetOrAdd($"{callerFile}|{callerMember}", _ => new());
        if (frozen.Length == current.Length)
        {
            foreach ((string was, string now) in frozen.Zip(current).Where(p => p.First != p.Second && p.First.Length > 3))
            {
                swaps[was] = now;
                swaps[was.Replace(Path.DirectorySeparatorChar, (char)47)] = now.Replace(Path.DirectorySeparatorChar, (char)47);
            }
        }
        foreach (KeyValuePair<string, string> swap in swaps.ToArray().OrderByDescending(p => p.Key.Length))
            result = result.Replace(swap.Key, swap.Value, StringComparison.Ordinal);
        return result;
    }

    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, string>> Replacements = new();

    public static void Record(string callerFile, string member, string arguments, string stdout, string stderr, int exitCode,
        string? instance = null, string? header = null)
    {
        string Mark(string text) => Portable(instance is null ? text : text.Replace(instance, InstanceMarker, StringComparison.Ordinal));
        Entry entry = new(member, Canonical(arguments), Mark(stdout), Mark(stderr), exitCode, Portable(arguments));
        string path = GoldenPath(callerFile);
        lock (WriteLock)
        {
            List<Entry> entries = Loaded.GetOrAdd(path, Read);
            entries.Add(entry);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            StringBuilder text = new();
            text.Append(JsonSerializer.Serialize(new { header = header ?? Header }, Json)).Append('\n');
            foreach (Entry e in entries) text.Append(JsonSerializer.Serialize(e, Json)).Append('\n');
            File.WriteAllText(path, text.ToString(), new UTF8Encoding(false));
        }
    }

    public static Entry Take(string callerFile, string member, string arguments)
    {
        string path = GoldenPath(callerFile);
        List<Entry> entries = Loaded.GetOrAdd(path, Read);
        string args = Canonical(arguments);
        string key = $"{path}|{member}|{args}";
        int occurrence = Seen.AddOrUpdate(key, 0, (_, n) => n + 1);
        Entry? entry = entries.Where(e => e.Member == member && e.Args == args).ElementAtOrDefault(occurrence);
        return entry ?? throw new InvalidOperationException(
            $"no golden #{occurrence} for {member}: {args} in {path}");
    }

    public static void AssertMatches(Entry golden, string arguments, string stdout, string stderr, int exitCode)
    {
        string actualOut = Canonical(stdout);
        string actualErr = Canonical(stderr);
        if (actualOut == Canonical(golden.Stdout) && actualErr == Canonical(golden.Stderr) && exitCode == golden.ExitCode) return;
        throw new MismatchException(
            $"{golden.Member}: '{arguments}' differs from its golden.\nGOLDEN exit {golden.ExitCode}\n{golden.Stdout}\nGOLDEN STDERR\n{golden.Stderr}\n" +
            $"ACTUAL exit {exitCode}\n{actualOut}\nACTUAL STDERR\n{actualErr}");
    }

    private static List<Entry> Read(string path)
    {
        if (!File.Exists(path)) return [];
        return [.. File.ReadLines(path).Skip(1).Where(l => l.Length > 0).Select(l => JsonSerializer.Deserialize<Entry>(l)!)];
    }

    [GeneratedRegex("[0-9a-f]{32}", RegexOptions.None, RegexTimeout.Milliseconds)]
    private static partial Regex InstanceId();

    [GeneratedRegex(@"\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}:\d{2}(\.\d+)?(Z|[+-]\d{2}:\d{2})?", RegexOptions.None, RegexTimeout.Milliseconds)]
    private static partial Regex Timestamp();

    // A backup file name's compact stamp (BackupTool: yyyyMMdd-HHmmssfff), never equal between a golden
    // freeze and a later replay.
    [GeneratedRegex(@"\d{8}-\d{9}", RegexOptions.None, RegexTimeout.Milliseconds)]
    private static partial Regex CompactTimestamp();

    [GeneratedRegex(@"\(last \d{4}-\d{2}-\d{2}\)", RegexOptions.None, RegexTimeout.Milliseconds)]
    private static partial Regex GapLastDate();

    // Directory.CreateTempSubdirectory("grimora-<name>-") appends its own random suffix, never equal
    // between a golden freeze and a later replay (e.g. a fixture folder Server.Tests seeds a CLI oracle
    // call from). Matches the fixed prefix every caller uses, keeping the descriptive name it chose.
    // The random suffix's shape is NOT the same on every platform: on Windows, CreateTempSubdirectory
    // appends a Path.GetRandomFileName()-style "XXXXXXXX.XXX" (8.3, lowercase letters and digits 0-5,
    // e.g. "ako1vvd5.xvr"); on Linux, confirmed from actual CI output (run 36358035833), it appends a
    // 6-character mixed-case alphanumeric suffix with no dot (e.g. "ggOPHV", "MLcDme", "BdkxgC") — a
    // different generator, not just a shorter random name. A golden frozen on Windows (lowercase-only,
    // dotted) previously left a Linux run's uppercase letters unmatched by the [0-9a-z] class, so the
    // suffix stayed literal after Canonical() and mismatched every golden that carries one of these paths.
    // The class below accepts letters of both cases plus digits, with or without the dot extension, so it
    // normalizes identically regardless of which platform produced the path.
    [GeneratedRegex(@"grimora-([a-z-]+?)-[0-9A-Za-z]{6,10}(?:\.[0-9A-Za-z]{1,4})?(?=[/""\\.]|$)", RegexOptions.None, RegexTimeout.Milliseconds)]
    private static partial Regex RandomTempDir();

    [GeneratedRegex("(?<![A-Za-z0-9])[A-Za-z]:/", RegexOptions.None, RegexTimeout.Milliseconds)]
    private static partial Regex WindowsDriveLetter();

    [GeneratedRegex(@"(?<![\w.])\d+([.,]\d+)?ms", RegexOptions.None, RegexTimeout.Milliseconds)]
    private static partial Regex Elapsed();

    // A decimal point that follows the machine's culture (nl-NL prints "0,00") is written with a point in every golden.
    [GeneratedRegex(@"(score -?\d+),(\d+)", RegexOptions.None, RegexTimeout.Milliseconds)]
    private static partial Regex Decimal();
}
