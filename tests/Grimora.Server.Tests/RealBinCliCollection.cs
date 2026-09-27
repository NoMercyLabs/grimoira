using Xunit;

namespace Grimora.Server.Tests;

/// <summary>
/// Serializes every test class whose no-golden path spawns a real <c>dotnet bin-cli/grimora.dll</c>
/// process (<see cref="Grimora.TestSupport.OldVsNewCli.BinCliDll"/>'s live fallback): that binary forwards
/// to the one shared grimora server keyed by a single named pipe per data dir
/// (<c>src/Grimora.Server/Data/Program.cs</c>), so two such classes racing to start or reach it at once is
/// a real collision, not a flake to retry away.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class RealBinCliCollection
{
    public const string Name = "real bin-cli process";
}
