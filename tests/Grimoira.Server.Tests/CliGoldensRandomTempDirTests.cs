using Grimoira.TestSupport;
using Xunit;

namespace Grimoira.Server.Tests;

/// <summary>
/// Directory.CreateTempSubdirectory's random suffix is not the same shape on every platform: Windows
/// appends a Path.GetRandomFileName()-style 8.3 name (lowercase letters/digits, e.g. "ako1vvd5.xvr"),
/// Linux appends a 6-character mixed-case alphanumeric suffix with no dot (e.g. "ggOPHV"), confirmed from
/// actual CI output (run 36358035833: grimoira-hook-verb-ggOPHV, grimoira-hook-verb-MLcDme, grimoira-hooks-BdkxgC).
/// CliGoldens.Canonical must fold both shapes to the same "grimoira-&lt;name&gt;-&lt;RAND&gt;" token, or a golden
/// frozen on one platform never matches a run on the other.
/// </summary>
public class CliGoldensRandomTempDirTests
{
    [Fact]
    public void WindowsAndLinuxShapedHookVerbSuffixesNormalizeIdentically()
    {
        string windowsShaped = "instance ready at /tmp/grimoira-hook-verb-ako1vvd5.xvr/test-hook-verb-x/grimoira.db";
        string linuxShaped = "instance ready at /tmp/grimoira-hook-verb-ggOPHV/test-hook-verb-x/grimoira.db";

        string canonicalWindows = CliGoldens.Canonical(windowsShaped);
        string canonicalLinux = CliGoldens.Canonical(linuxShaped);

        Assert.Equal(canonicalWindows, canonicalLinux);
        Assert.Contains("grimoira-hook-verb-<RAND>", canonicalWindows);
    }

    [Fact]
    public void WindowsAndLinuxShapedHooksSuffixesNormalizeIdentically()
    {
        string windowsShaped = "instance ready at /tmp/grimoira-hooks-xyjl02n4.bbk/test-hooks-x/grimoira.db";
        string linuxShaped = "instance ready at /tmp/grimoira-hooks-BdkxgC/test-hooks-x/grimoira.db";

        string canonicalWindows = CliGoldens.Canonical(windowsShaped);
        string canonicalLinux = CliGoldens.Canonical(linuxShaped);

        Assert.Equal(canonicalWindows, canonicalLinux);
        Assert.Contains("grimoira-hooks-<RAND>", canonicalWindows);
    }

    [Fact]
    public void GapDisplayDateDoesNotPinGoldenToFreezeDay()
    {
        string frozen = "  1x [query] missing  (last 2026-09-27)";
        string today = "  1x [query] missing  (last 2026-09-28)";

        Assert.Equal(CliGoldens.Canonical(frozen), CliGoldens.Canonical(today));
        Assert.Contains("(last <DATE>)", CliGoldens.Canonical(today));
    }
}
