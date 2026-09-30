using Grimora.Store.Data;
using System.Text.RegularExpressions;
using Xunit;

namespace Grimora.Layout.Tests;

// JsonElement.TryGetProperty throws InvalidOperationException when the element is not an object, and a
// transcript, an API payload or a file written by another tool is allowed to put a string, an array or
// null where an object was expected (a real transcript did: toolUseResult was a string 1,167 times and an
// array 1,962 times in 49,697 values). CompactBriefTool fixed that once with a guarded read; this guard
// keeps every read under src/ on that one helper (Grimora.Store.Data.JsonShape) so the same crash cannot
// come back in another file. Comments and doc-comments do not count; only code that calls it.
public partial class JsonPropertyReadsGoThroughJsonShapeTests
{
    private const string TheOneHelper = "Grimora.Store/Data/JsonShape.cs";

    [Fact]
    public void OnlyJsonShapeCallsTryGetPropertyDirectly()
    {
        string srcRoot = Path.Combine(RepoPaths.Root, "src");
        List<string> offenders = [];
        foreach (string path in Directory.EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(srcRoot, path).Replace('\\', '/');
            if (relative == TheOneHelper) continue;
            string[] lines = File.ReadAllLines(path);
            for (int i = 0; i < lines.Length; i++)
            {
                string code = lines[i];
                int comment = code.IndexOf("//", StringComparison.Ordinal);
                if (comment >= 0) code = code[..comment];
                if (RawTryGetProperty().IsMatchOrFalse(code)) offenders.Add($"{relative}:{i + 1}");
            }
        }

        Assert.True(offenders.Count == 0,
            "raw JsonElement.TryGetProperty call(s) under src/ outside JsonShape (use JsonShape.TryGetObjectProperty "
            + "or JsonShape.GetString: they refuse to read a property of anything but an object):\n"
            + string.Join("\n", offenders));
    }

    [Fact]
    public void TheOneHelperExistsAndIsTheOnlyPlaceThatCallsIt()
    {
        string full = Path.Combine(RepoPaths.Root, "src", TheOneHelper.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(full), $"expected the shared JSON guard at src/{TheOneHelper}");
        Assert.True(RawTryGetProperty().IsMatchOrFalse(File.ReadAllText(full)), "JsonShape must be the one place that calls TryGetProperty");
    }

    [GeneratedRegex(@"\.TryGetProperty\s*\(", RegexOptions.None, RegexTimeout.Milliseconds)]
    private static partial Regex RawTryGetProperty();
}
