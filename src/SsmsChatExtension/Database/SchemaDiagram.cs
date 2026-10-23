using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace SsmsChatExtension.Database
{
    /// <summary>
    /// Builds a Mermaid ER diagram of a database straight from the catalog (no AI involved): tables, columns with
    /// PK / FK markers, and foreign key relationships. Large databases are trimmed so the diagram stays readable.
    /// </summary>
    internal static class SchemaDiagram
    {
        /// <summary>Above this many tables only key columns are drawn.</summary>
        private const int KeyColumnsOnlyAbove = 40;
        private const int MaxTables = 150;
        private const int MaxColumnsPerTable = 25;

        private const string TablesSql =
            "SELECT TOP (@max) t.object_id, s.name AS schema_name, t.name AS table_name " +
            "FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id " +
            "WHERE t.is_ms_shipped = 0 AND (@schema IS NULL OR s.name = @schema) " +
            // Tables with relationships first, so trimming keeps the interesting part.
            "ORDER BY CASE WHEN EXISTS (SELECT 1 FROM sys.foreign_keys f WHERE f.parent_object_id = t.object_id OR f.referenced_object_id = t.object_id) THEN 0 ELSE 1 END, s.name, t.name";

        private const string ColumnsSql =
            "SELECT c.object_id, c.column_id, c.name, TYPE_NAME(c.user_type_id) AS type_name, c.is_nullable, " +
            "  CAST(CASE WHEN EXISTS (SELECT 1 FROM sys.indexes i JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id " +
            "    WHERE i.is_primary_key = 1 AND ic.object_id = c.object_id AND ic.column_id = c.column_id) THEN 1 ELSE 0 END AS bit) AS is_pk, " +
            "  CAST(CASE WHEN EXISTS (SELECT 1 FROM sys.foreign_key_columns fc WHERE fc.parent_object_id = c.object_id AND fc.parent_column_id = c.column_id) THEN 1 ELSE 0 END AS bit) AS is_fk " +
            "FROM sys.columns c JOIN sys.tables t ON t.object_id = c.object_id WHERE t.is_ms_shipped = 0 " +
            "ORDER BY c.object_id, c.column_id";

        private const string ForeignKeysSql =
            "SELECT fk.name, fk.parent_object_id, fk.referenced_object_id, " +
            "  CAST(CASE WHEN EXISTS (SELECT 1 FROM sys.foreign_key_columns fc JOIN sys.columns c ON c.object_id = fc.parent_object_id AND c.column_id = fc.parent_column_id " +
            "    WHERE fc.constraint_object_id = fk.object_id AND c.is_nullable = 1) THEN 1 ELSE 0 END AS bit) AS is_optional " +
            "FROM sys.foreign_keys fk";

        private static readonly Regex NonIdentifier = new Regex(@"[^A-Za-z0-9_]", RegexOptions.Compiled);

        /// <summary>Returns the Mermaid source and a one-line summary.</summary>
        public static (string Mermaid, string Summary) Build(IDbConnection connection, string schema)
        {
            var tables = new List<(int Id, string Schema, string Name)>();
            using (var reader = Query(connection, TablesSql, new Dictionary<string, object>
            {
                ["@max"] = MaxTables + 1,
                ["@schema"] = string.IsNullOrWhiteSpace(schema) ? (object)DBNull.Value : schema.Trim()
            }))
            {
                while (reader.Read()) tables.Add((reader.GetInt32(0), reader.GetString(1), reader.GetString(2)));
            }

            var database = connection.Database;
            if (tables.Count == 0)
            {
                throw new InvalidOperationException("No user tables found in " + database + (string.IsNullOrWhiteSpace(schema) ? "." : " schema " + schema + "."));
            }

            var trimmed = tables.Count > MaxTables;
            if (trimmed) tables = tables.Take(MaxTables).ToList();
            var keyColumnsOnly = tables.Count > KeyColumnsOnlyAbove;
            var included = new HashSet<int>(tables.Select(t => t.Id));

            var columns = new Dictionary<int, List<(string Name, string Type, bool Pk, bool Fk)>>();
            using (var reader = Query(connection, ColumnsSql, null))
            {
                while (reader.Read())
                {
                    var id = reader.GetInt32(0);
                    if (!included.Contains(id)) continue;
                    if (!columns.TryGetValue(id, out var list)) columns[id] = list = new List<(string, string, bool, bool)>();
                    list.Add((reader.GetString(2), reader.IsDBNull(3) ? "unknown" : reader.GetString(3), reader.GetBoolean(5), reader.GetBoolean(6)));
                }
            }

            var relationships = new List<(string Name, int From, int To, bool Optional)>();
            using (var reader = Query(connection, ForeignKeysSql, null))
            {
                while (reader.Read())
                {
                    var from = reader.GetInt32(1);
                    var to = reader.GetInt32(2);
                    if (included.Contains(from) && included.Contains(to)) relationships.Add((reader.GetString(0), from, to, reader.GetBoolean(3)));
                }
            }

            // Mermaid entity ids must be simple identifiers; the alias shows the real schema.table name.
            var ids = new Dictionary<int, string>();
            foreach (var table in tables)
            {
                var id = NonIdentifier.Replace(table.Schema + "_" + table.Name, "_");
                while (ids.ContainsValue(id)) id += "_";
                ids[table.Id] = id;
            }

            var mermaid = new StringBuilder("erDiagram").AppendLine();
            foreach (var table in tables)
            {
                mermaid.Append("  ").Append(ids[table.Id]).Append("[\"").Append(Label(table.Schema + "." + table.Name)).AppendLine("\"] {");
                if (columns.TryGetValue(table.Id, out var list))
                {
                    var shown = keyColumnsOnly ? list.Where(c => c.Pk || c.Fk).ToList() : list;
                    foreach (var column in shown.Take(MaxColumnsPerTable))
                    {
                        mermaid.Append("    ").Append(TypeToken(column.Type)).Append(' ').Append(NameToken(column.Name));
                        var keys = new List<string>();
                        if (column.Pk) keys.Add("PK");
                        if (column.Fk) keys.Add("FK");
                        if (keys.Count > 0) mermaid.Append(' ').Append(string.Join(", ", keys));
                        if (NameToken(column.Name) != column.Name) mermaid.Append(" \"").Append(Label(column.Name)).Append('"');
                        mermaid.AppendLine();
                    }
                    var hidden = list.Count - Math.Min(shown.Count, MaxColumnsPerTable);
                    if (hidden > 0) mermaid.Append("    more ").Append("columns \"").Append(hidden).AppendLine(" more not shown\"");
                }
                mermaid.AppendLine("  }");
            }

            foreach (var fk in relationships)
            {
                // Referenced (parent) table: exactly one; referencing table: zero or more.
                mermaid.Append("  ").Append(ids[fk.To]).Append(fk.Optional ? " |o--o{ " : " ||--o{ ").Append(ids[fk.From])
                       .Append(" : \"").Append(Label(fk.Name)).AppendLine("\"");
            }

            var summary = tables.Count + " tables, " + relationships.Count + " relationships in " + database
                          + (string.IsNullOrWhiteSpace(schema) ? string.Empty : " (schema " + schema.Trim() + ")")
                          + (trimmed ? "; trimmed to the first " + MaxTables + " tables" : string.Empty)
                          + (keyColumnsOnly ? "; only key columns shown" : string.Empty) + ".";
            return (mermaid.ToString(), summary);
        }

        private static IDataReader Query(IDbConnection connection, string sql, IDictionary<string, object> parameters)
        {
            var command = connection.CreateCommand();
            command.CommandText = sql;
            command.CommandTimeout = 60;
            if (parameters != null)
            {
                foreach (var pair in parameters)
                {
                    var parameter = command.CreateParameter();
                    parameter.ParameterName = pair.Key;
                    parameter.Value = pair.Value;
                    command.Parameters.Add(parameter);
                }
            }
            return command.ExecuteReader(CommandBehavior.Default);
        }

        private static string TypeToken(string type)
        {
            var token = NonIdentifier.Replace(type, "_");
            return token.Length == 0 || char.IsDigit(token[0]) ? "t_" + token : token;
        }

        private static string NameToken(string name)
        {
            var token = NonIdentifier.Replace(name, "_");
            return token.Length == 0 || char.IsDigit(token[0]) ? "c_" + token : token;
        }

        private static string Label(string text)
        {
            return text.Replace("\"", "'");
        }
    }
}
