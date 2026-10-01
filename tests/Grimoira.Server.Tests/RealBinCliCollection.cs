using Xunit;

namespace Grimoira.Server.Tests;

/// <summary>
/// Serializes every test class whose no-golden path spawns a real <c>dotnet bin-cli/grimoira.dll</c>
/// process (<see cref="Grimoira.TestSupport.OldVsNewCli.BinCliDll"/>'s live fallback): that binary forwards
/// to the one shared grimoira server keyed by a single named pipe per data dir
/// (<c>src/Grimoira.Server/Data/Program.cs</c>), so two such classes racing to start or reach it at once is
/// a real collision, not a flake to retry away.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class RealBinCliCollection
{
    public const string Name = "real bin-cli process";
}
