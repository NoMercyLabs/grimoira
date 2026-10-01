extern alias cli;

using Xunit;

namespace Grimora.Layout.Tests;

// AITM_* and GRIMORA_* both promote to GRIMOIRA_*; an already-set GRIMOIRA_* wins. Both copies of the shim.
[Collection("EnvironmentMutation")]
public sealed class LegacyEnvironmentPromotionTests : IDisposable
{
    private static readonly string[] Names = ["AITM_PROMO_X", "GRIMORA_PROMO_X", "GRIMOIRA_PROMO_X", "GRIMORA_PROMO_Y"];

    public LegacyEnvironmentPromotionTests() => Clear();

    public void Dispose() => Clear();

    private static void Clear()
    {
        foreach (string n in Names) Environment.SetEnvironmentVariable(n, null);
    }

    public static TheoryData<string> Shims => ["store", "cli"];

    private static void Promote(string shim)
    {
        if (shim == "store") Grimora.Store.Data.LegacyEnvironment.Promote();
        else cli::Grimora.Cli.Tools.LegacyEnvironment.Promote();
    }

    [Theory]
    [MemberData(nameof(Shims))]
    public void AGrimoraVariablePromotesToGrimoira(string shim)
    {
        Environment.SetEnvironmentVariable("GRIMORA_PROMO_Y", "from-grimora");
        Promote(shim);
        Assert.Equal("from-grimora", Environment.GetEnvironmentVariable("GRIMOIRA_PROMO_Y"));
    }

    [Theory]
    [MemberData(nameof(Shims))]
    public void AnAitmVariablePromotesToGrimoira(string shim)
    {
        Environment.SetEnvironmentVariable("AITM_PROMO_X", "from-aitm");
        Promote(shim);
        Assert.Equal("from-aitm", Environment.GetEnvironmentVariable("GRIMOIRA_PROMO_X"));
    }

    [Theory]
    [MemberData(nameof(Shims))]
    public void GrimoiraWinsOverBothOldNames(string shim)
    {
        Environment.SetEnvironmentVariable("AITM_PROMO_X", "from-aitm");
        Environment.SetEnvironmentVariable("GRIMORA_PROMO_X", "from-grimora");
        Environment.SetEnvironmentVariable("GRIMOIRA_PROMO_X", "from-grimoira");
        Promote(shim);
        Assert.Equal("from-grimoira", Environment.GetEnvironmentVariable("GRIMOIRA_PROMO_X"));
    }

    [Theory]
    [MemberData(nameof(Shims))]
    public void GrimoraBeatsAitmWhenGrimoiraIsUnset(string shim)
    {
        Environment.SetEnvironmentVariable("AITM_PROMO_X", "from-aitm");
        Environment.SetEnvironmentVariable("GRIMORA_PROMO_X", "from-grimora");
        Promote(shim);
        Assert.Equal("from-grimora", Environment.GetEnvironmentVariable("GRIMOIRA_PROMO_X"));
    }
}
