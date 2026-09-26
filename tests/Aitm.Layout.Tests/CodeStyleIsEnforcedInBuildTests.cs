using System.Text.RegularExpressions;
using Xunit;

namespace Aitm.Layout.Tests;

// The code style is enforced by the build, not by a hand-run Rider cleanup: each rule below is an
// error-severity IDE diagnostic in the repo-root .editorconfig, and every folder that holds C# projects
// turns on EnforceCodeStyleInBuild, so `dotnet build` fails on a violation. The text checks here pin
// that wiring; the real proof is a seeded violation failing the build (see the commit message).
public class CodeStyleIsEnforcedInBuildTests
{
    public static readonly string[] EnforcedRules =
    [
        "IDE0005", // unnecessary using directive
        "IDE0008", // use explicit type instead of var
        "IDE0028", // collection initializer -> collection expression
        "IDE0090", // target-typed new()
        "IDE0300", // array initializer -> collection expression
        "IDE0301", // empty collection -> []
        "IDE0305", // fluent .ToList()/.ToArray() -> collection expression
        "IDE0370", // unnecessary null-forgiving suppression
    ];

    private static string Read(string relative) => File.ReadAllText(Path.Combine(RepoPaths.Root, relative));

    public static IEnumerable<object[]> RuleData() => EnforcedRules.Select(r => new object[] { r });

    [Theory]
    [MemberData(nameof(RuleData))]
    public void Rule_is_an_error_in_the_repo_root_editorconfig(string rule)
    {
        string text = Read(".editorconfig");
        Assert.Matches(new Regex($@"^dotnet_diagnostic\.{rule}\.severity\s*=\s*error\s*$", RegexOptions.Multiline), text);
    }

    [Fact]
    public void Repo_root_editorconfig_is_the_root_and_covers_cs_files()
    {
        string text = Read(".editorconfig");
        Assert.Matches(new Regex(@"^root\s*=\s*true\s*$", RegexOptions.Multiline), text);
        Assert.Contains("[*.cs]", text);
    }

    [Theory]
    [InlineData("src/Directory.Build.props")]
    [InlineData("tests/Directory.Build.props")]
    public void Folder_turns_on_code_style_analysis_in_build(string props)
    {
        Assert.Matches(new Regex(@"<EnforceCodeStyleInBuild>\s*true\s*</EnforceCodeStyleInBuild>"), Read(props));
    }

    [Fact]
    public void Verify_script_and_ci_gate_on_dotnet_format_style_verify_no_changes()
    {
        foreach (string file in new[] { "verify.ps1", ".github/workflows/ci.yml" })
        {
            string text = Read(file);
            Assert.Contains("dotnet format style", text);
            Assert.Contains("--verify-no-changes", text);
        }
    }
}
