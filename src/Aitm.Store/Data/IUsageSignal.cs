using Microsoft.Data.Sqlite;

namespace Aitm.Store.Data;

/// <summary>
/// The learning signal every channel bumps when a read surfaces one of its rows (RESTRUCTURE.md
/// section 1, usage signal). Store defines the seam so a channel never has to depend on Brain, which
/// owns the <c>node</c>/<c>ref</c> tables the signal actually writes through (section 1: "Instead,
/// Store defines an <c>IUsageSignal</c>. A channel calls it. Brain implements it. The host wires them
/// together."). <see cref="UsageSignal"/> is today's behaviour (mcp.cs's <c>ReinforceChannel</c>); a
/// Brain-aware implementation arrives once Brain exists as a project.
/// </summary>
public interface IUsageSignal
{
    void Reinforce(SqliteConnection connection, string channel, IEnumerable<string> payloadKeys);
}
