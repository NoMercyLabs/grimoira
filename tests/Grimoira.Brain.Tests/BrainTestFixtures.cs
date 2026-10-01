using Grimoira.Store.Data;
using Microsoft.Data.Sqlite;

namespace Grimoira.Brain.Tests;

/// <summary>Seeds the brain tables (created by <c>grimoira init</c>, BrainSchema.cs) with the minimum rows a
/// brain read needs, so a pinned-output test never runs against an empty, meaningless store.</summary>
internal static class BrainTestFixtures
{
    public static void InsertHardNode(string dbPath, string k, string kind, string label, string gloss)
    {
        using SqliteConnection connection = Open(dbPath);
        using SqliteCommand insert = connection.CreateCommand();
        insert.CommandText = "INSERT INTO node(k,kind,label,gloss,hard) VALUES($k,$kind,$label,$gloss,1)";
        insert.Parameters.AddWithValue("$k", k);
        insert.Parameters.AddWithValue("$kind", kind);
        insert.Parameters.AddWithValue("$label", label);
        insert.Parameters.AddWithValue("$gloss", gloss);
        insert.ExecuteNonQuery();
    }

    public static void InsertNode(string dbPath, string k, string kind, string label, string gloss)
    {
        using SqliteConnection connection = Open(dbPath);
        using SqliteCommand insert = connection.CreateCommand();
        insert.CommandText = "INSERT INTO node(k,kind,label,gloss) VALUES($k,$kind,$label,$gloss)";
        insert.Parameters.AddWithValue("$k", k);
        insert.Parameters.AddWithValue("$kind", kind);
        insert.Parameters.AddWithValue("$label", label);
        insert.Parameters.AddWithValue("$gloss", gloss);
        insert.ExecuteNonQuery();
    }

    /// <summary>Inserts a node whose valid_from is an explicit number of days in the past, so an age test never
    /// depends on the wall clock ticking between the insert and the query (a Linux clock tick is too coarse).</summary>
    public static void InsertNodeAged(string dbPath, string k, string kind, string label, string gloss, int daysAgo)
    {
        using SqliteConnection connection = Open(dbPath);
        using SqliteCommand insert = connection.CreateCommand();
        insert.CommandText = "INSERT INTO node(k,kind,label,gloss,valid_from) VALUES($k,$kind,$label,$gloss,strftime('%Y-%m-%dT%H:%M:%fZ','now',$age))";
        insert.Parameters.AddWithValue("$k", k);
        insert.Parameters.AddWithValue("$kind", kind);
        insert.Parameters.AddWithValue("$label", label);
        insert.Parameters.AddWithValue("$gloss", gloss);
        insert.Parameters.AddWithValue("$age", $"-{daysAgo} days");
        insert.ExecuteNonQuery();
    }

    public static void InsertSharingTriple(string dbPath, string subject, string @object)
    {
        using SqliteConnection connection = Open(dbPath);
        using SqliteCommand insert = connection.CreateCommand();
        insert.CommandText = "INSERT INTO triple(s,p,o,o_is_literal) VALUES($s,'consumes',$o,0)";
        insert.Parameters.AddWithValue("$s", subject);
        insert.Parameters.AddWithValue("$o", @object);
        insert.ExecuteNonQuery();
    }

    public static void InsertSlot(string dbPath, string frameK, string name, string value, string facet, string because)
    {
        using SqliteConnection connection = Open(dbPath);
        using SqliteCommand insert = connection.CreateCommand();
        insert.CommandText = "INSERT INTO slot(frame_k,name,value,facet,because) VALUES($f,$n,$v,$facet,$because)";
        insert.Parameters.AddWithValue("$f", frameK);
        insert.Parameters.AddWithValue("$n", name);
        insert.Parameters.AddWithValue("$v", value);
        insert.Parameters.AddWithValue("$facet", facet);
        insert.Parameters.AddWithValue("$because", because);
        insert.ExecuteNonQuery();
    }

    public static void InsertRef(string dbPath, string nodeK, string channel, string payloadK)
    {
        using SqliteConnection connection = Open(dbPath);
        using SqliteCommand insert = connection.CreateCommand();
        insert.CommandText = "INSERT INTO ref(node_k,channel,payload_k) VALUES($n,$c,$p)";
        insert.Parameters.AddWithValue("$n", nodeK);
        insert.Parameters.AddWithValue("$c", channel);
        insert.Parameters.AddWithValue("$p", payloadK);
        insert.ExecuteNonQuery();
    }

    private static SqliteConnection Open(string dbPath) => StoreConnection.Open(dbPath);
}
