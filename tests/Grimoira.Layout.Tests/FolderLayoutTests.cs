using Xunit;

namespace Grimoira.Layout.Tests;

// RESTRUCTURE.md section 1: "Inside every source project, the same fixed folders: Tools/, Schema/,
// Data/. Nothing else. A layout test fails on a file outside these folders."
public class FolderLayoutTests
{
    [Theory]
    [MemberData(nameof(RepoPaths.SourceProjectsData), MemberType = typeof(RepoPaths))]
    public void FixedFoldersExist(string project)
    {
        string projectDir = Path.Combine(RepoPaths.Root, "src", project);
        Assert.True(Directory.Exists(Path.Combine(projectDir, "Tools")), $"{project} is missing Tools/");
        Assert.True(Directory.Exists(Path.Combine(projectDir, "Schema")), $"{project} is missing Schema/");
        Assert.True(Directory.Exists(Path.Combine(projectDir, "Data")), $"{project} is missing Data/");
    }

    [Theory]
    [MemberData(nameof(RepoPaths.SourceProjectsData), MemberType = typeof(RepoPaths))]
    public void NoFileSitsOutsideTheFixedFolders(string project)
    {
        string projectDir = Path.Combine(RepoPaths.Root, "src", project);
        // Allowed outside Tools/Schema/Data: the .csproj itself, Grimoira.Server's Handover/ folder
        // (RESTRUCTURE.md section 1, the 3 tools that leave for Arcanum later), and Grimoira.Cli's
        // Program.cs entry point (it owns "the grimoira command", not a feature tool).
        HashSet<string> allowedRootFiles = project == "Grimoira.Cli"
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Program.cs" }
            : [];

        foreach (string file in Directory.EnumerateFiles(projectDir, "*.cs", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(projectDir, file);
            string[] parts = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (parts.Contains("obj") || parts.Contains("bin")) continue;
            if (parts.Length == 1 && allowedRootFiles.Contains(parts[0])) continue;
            if (parts[0] is "Tools" or "Schema" or "Data") continue;
            if (project == "Grimoira.Server" && parts[0] == "Handover") continue;

            Assert.Fail($"{project} has a file outside Tools/Schema/Data: {rel}");
        }
    }
}
