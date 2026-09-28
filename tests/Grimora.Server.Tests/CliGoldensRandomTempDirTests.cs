using Grimora.TestSupport;
using Xunit;

namespace Grimora.Server.Tests;

/// <summary>
/// Directory.CreateTempSubdirectory's random suffix is not the same shape on every platform: Windows
/// appends a Path.GetRandomFileName()-style 8.3 name (lowercase letters/digits, e.g. "ako1vvd5.xvr"),
/// Linux appends a 6-character mixed-case alphanumeric suffix with no dot (e.g. "ggOPHV"), confirmed from
/// actual CI output (run 36358035833: grimora-hook-verb-ggOPHV, grimora-hook-verb-MLcDme, grimora-hooks-BdkxgC).
/// CliGoldens.Canonical must fold both shapes to the same "grimora-&lt;name&gt;-&lt;RAND&gt;" token, or a golden
/// frozen on one platform never matches a run on the other.
/// </summary>
public class CliGoldensRandomTempDirTests
{
    [Fact]
    public void WindowsAndLinuxShapedHookVerbSuffixesNormalizeIdentically()
    {
        string windowsShaped = "instance ready at /tmp/grimora-hook-verb-ako1vvd5.xvr/test-hook-verb-x/grimora.db";
        string linuxShaped = "instance ready at /tmp/grimora-hook-verb-ggOPHV/test-hook-verb-x/grimora.db";

        string canonicalWindows = CliGoldens.Canonical(windowsShaped);
        string canonicalLinux = CliGoldens.Canonical(linuxShaped);

        Assert.Equal(canonicalWindows, canonicalLinux);
        Assert.Contains("grimora-hook-verb-<RAND>", canonicalWindows);
    }

    [Fact]
    public void WindowsAndLinuxShapedHooksSuffixesNormalizeIdentically()
    {
        string windowsShaped = "instance ready at /tmp/grimora-hooks-xyjl02n4.bbk/test-hooks-x/grimora.db";
        string linuxShaped = "instance ready at /tmp/grimora-hooks-BdkxgC/test-hooks-x/grimora.db";

        string canonicalWindows = CliGoldens.Canonical(windowsShaped);
        string canonicalLinux = CliGoldens.Canonical(linuxShaped);

        Assert.Equal(canonicalWindows, canonicalLinux);
        Assert.Contains("grimora-hooks-<RAND>", canonicalWindows);
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
