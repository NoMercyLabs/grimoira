using Microsoft.Data.Sqlite;

namespace Aitm.Store.Schema;

/// <summary>
/// Store owns the runner; every project only ever hands it an <see cref="ISchemaProvider"/>, so a
/// reference the wrong way round shows up as a project reaching for the runner's internals rather
/// than as Store importing another project's schema (RESTRUCTURE.md section 1).
/// </summary>
public static class SchemaRunner
{
    /// <summary>Applies every provider's statements, in the given order, on the given connection.</summary>
    public static void Apply(SqliteConnection connection, IEnumerable<ISchemaProvider> providers)
    {
        foreach (ISchemaProvider provider in providers)
        {
            foreach (string statement in provider.Statements)
            {
                using SqliteCommand command = connection.CreateCommand();
                command.CommandText = statement;
                command.ExecuteNonQuery();
            }
        }
    }
}
