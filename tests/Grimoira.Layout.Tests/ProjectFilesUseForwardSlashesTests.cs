using Xunit;

namespace Grimoira.Layout.Tests;

public class ProjectFilesUseForwardSlashesTests
{
    [Fact]
    public void NoProjectFilePathUsesABackslash()
    {
        string root = RepoPaths.Root;
        List<string> offenders = [];
        foreach (string file in Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") || file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
                continue;
            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                bool pathLine = lines[i].Contains("Include=\"") || lines[i].Contains("Update=\"") || lines[i].Contains("Remove=\"") || lines[i].Contains("<Link>");
                if (pathLine && lines[i].Contains((char)92))
                    offenders.Add($"{Path.GetRelativePath(root, file)}:{i + 1}");
            }
        }

        Assert.True(offenders.Count == 0, $"a backslash in a project path breaks Linux: {string.Join(", ", offenders)}");
    }
}
