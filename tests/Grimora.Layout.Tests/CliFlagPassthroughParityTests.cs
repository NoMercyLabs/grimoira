using System.Globalization;
using Grimora.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Grimora.Layout.Tests;

// RESTRUCTURE.md slice 24 dispatch audit: a flag the old inline grimora.cs case read is not always passed
// on to the new tool-class call, so it is silently ignored (found twice: `add --why`, fixed in 4fc7387,
// and this one). Case: `brain learn slot` — oracle's BrainLearn (grimora.cs:1698 at commit
// OldVsNewCli.OracleCommit) reads GetFlag("--facet") ?? "text" and threads it into AddSlot. Today's
// dispatch (grimora.cs's BrainCmd "learn" case) hardcodes the literal "text" instead of GetFlag("--facet"),
// so a non-default --facet value is silently dropped. `--facet text` (the default) never exercises this,
// which is why the earlier parity test for this verb (BrainWriteCliParityTests) missed it — this
// fixture deliberately uses a non-default facet.
public class CliFlagPassthroughParityTests
{
    private static (string oldInstance, string newInstance) RunParity(string[] setup, string command)
    {
        string oldInstance = GrimoraCliRunner.NewTestInstance("p24fo");
        string newInstance = GrimoraCliRunner.NewTestInstance("p24fn");
        string oldDll = OldVsNewCli.OracleDll();
        string newDll = OldVsNewCli.BinCliDll();
        foreach (string s in setup)
        {
            OldVsNewCli.Run(oldDll, oldInstance, s);
            OldVsNewCli.Run(newDll, newInstance, s);
        }

        OldVsNewCli.Result oldResult = OldVsNewCli.Run(oldDll, oldInstance, command);
        OldVsNewCli.Result newResult = OldVsNewCli.Run(newDll, newInstance, command);

        Assert.Equal(oldResult.Stdout, newResult.Stdout);
        Assert.Equal(oldResult.Stderr, newResult.Stderr);
        Assert.Equal(oldResult.ExitCode, newResult.ExitCode);

        return (oldInstance, newInstance);
    }

    private static List<string> SelectRows(string dbPath, string sql)
    {
        List<string> rows = [];
        using SqliteConnection connection = new($"Data Source={dbPath};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            List<string> cols = [];
            for (int i = 0; i < reader.FieldCount; i++)
                cols.Add(reader.IsDBNull(i) ? "\x1f" : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture) ?? "");
            rows.Add(string.Join("|", cols));
        }
        rows.Sort(StringComparer.Ordinal);
        return rows;
    }

    private const string SlotFacetRowsSql = "SELECT frame_k,name,value,facet FROM slot_now ORDER BY frame_k,name";

    // brain learn slot --facet <non-default> must reach the slot row exactly like the oracle: the old
    // CLI threads GetFlag("--facet") straight into AddSlot, so a non-default facet lands in slot.facet.
    [Fact]
    public void LearnSlotWritesTheFacetFlagLikeTheOldCli()
    {
        string[] setup = ["init", "brain learn node p24f:frame fact \"frame label\""];
        (string oldInstance, string newInstance) = RunParity(setup, "brain learn slot p24f:frame name value --facet code");
        try
        {
            List<string> oldRows = SelectRows(GrimoraCliRunner.InstanceDbPath(oldInstance), SlotFacetRowsSql);
            List<string> newRows = SelectRows(GrimoraCliRunner.InstanceDbPath(newInstance), SlotFacetRowsSql);

            // Pin the oracle's own behaviour first: if this ever stops being "code", the fixture no
            // longer proves anything and the test would pass for the wrong reason.
            Assert.Equal(["p24f:frame|name|value|code"], oldRows);
            Assert.Equal(oldRows, newRows);
        }
        finally
        {
            GrimoraCliRunner.DeleteInstance(oldInstance);
            GrimoraCliRunner.DeleteInstance(newInstance);
        }
    }

    // Same shape via `brain place`, which prints the slot's facet column to stdout — an end-to-end
    // observable symptom of the same bug, not just a row read straight from the DB.
    [Fact]
    public void BrainPlaceShowsTheLearnedFacetLikeTheOldCli()
    {
        string[] setup =
        [
            "init",
            "brain learn node kind:p24fplace fact \"frame label\"",
            "brain learn slot kind:p24fplace name value --facet code",
        ];
        (string oldInstance, string newInstance) = RunParity(setup, "brain place p24fplace");
        // RunParity already asserted stdout/stderr/exit-code parity, which is the assertion this test
        // needs: `brain place` prints the slot's facet column, so a hardcoded facet would show up there.
        GrimoraCliRunner.DeleteInstance(oldInstance);
        GrimoraCliRunner.DeleteInstance(newInstance);
    }
}
