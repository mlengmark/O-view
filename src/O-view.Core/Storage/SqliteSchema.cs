using Microsoft.Data.Sqlite;

namespace OView.Core.Storage;

/// <summary>Schema probes the store and its report both need.</summary>
internal static class SqliteSchema
{
    /// <summary>
    /// Whether <paramref name="table"/> has a column named <paramref name="column"/>, without
    /// assuming a failed query means it does not.
    /// </summary>
    public static bool HasColumn(SqliteConnection connection, string table, string column)
    {
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"PRAGMA table_info({table});";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
        catch (SqliteException)
        {
            return false;
        }
    }
}
