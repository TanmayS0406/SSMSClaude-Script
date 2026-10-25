using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace SsmsChatExtension.Database
{
    /// <summary>What to put in a database diagram.</summary>
    internal sealed class DiagramOptions
    {
        /// <summary>Databases to include. Empty = the connection's current database.</summary>
        public IReadOnlyList<string> Databases { get; set; } = Array.Empty<string>();

        /// <summary>Only tables in this schema (in every selected database). Empty = all schemas.</summary>
        public string Schema { get; set; }

        /// <summary>Add dashed links guessed from column names and types (also across databases).</summary>
        public bool InferRelationships { get; set; } = true;

        /// <summary>List views / procedures / functions that reference other databases.</summary>
        public bool IncludeCodeReferences { get; set; } = true;
    }

    /// <summary>A finished diagram: Mermaid source plus an HTML report shown under it.</summary>
    internal sealed class DiagramResult
    {
        public string Title { get; set; }
        public string Mermaid { get; set; }
        public string Summary { get; set; }
        public string DetailsHtml { get; set; }
    }

    /// <summary>
    /// Builds a Mermaid ER diagram of one or more databases straight from the catalog (no AI involved):
    /// tables, key columns, declared foreign keys (solid lines) and, optionally, relationships inferred from
    /// matching column names and types (dashed lines) - including across databases, where SQL Server cannot
    /// declare foreign keys. Each database gets its own colour. Large diagrams are trimmed to stay readable.
    /// </summary>
    internal static class SchemaDiagram
    {
        private const int MaxTables = 200;
        private const int KeyColumnsOnlyAbove = 40;
        private const int MaxColumnsPerTable = 25;
        private const int MaxCandidatesPerColumn = 3;

        private static readonly string[] Palette =
        {
            "fill:#dbeafe,stroke:#2563eb", "fill:#dcfce7,stroke:#16a34a", "fill:#fef3c7,stroke:#d97706",
            "fill:#fce7f3,stroke:#db2777", "fill:#ede9fe,stroke:#7c3aed", "fill:#cffafe,stroke:#0891b2",
            "fill:#fee2e2,stroke:#dc2626", "fill:#e5e7eb,stroke:#4b5563"
        };

        private const string TablesSql =
            "SELECT t.object_id, s.name, t.name FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id " +
            "WHERE t.is_ms_shipped = 0 AND (@schema IS NULL OR s.name = @schema)";

        private const string ColumnsSql =
            "SELECT c.object_id, c.name, TYPE_NAME(c.user_type_id), c.is_nullable, " +
            "  CAST(CASE WHEN EXISTS (SELECT 1 FROM sys.indexes i JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id " +
            "    WHERE i.is_primary_key = 1 AND ic.object_id = c.object_id AND ic.column_id = c.column_id) THEN 1 ELSE 0 END AS bit) " +
            "FROM sys.columns c JOIN sys.tables t ON t.object_id = c.object_id WHERE t.is_ms_shipped = 0 ORDER BY c.object_id, c.column_id";

        private const string ForeignKeysSql =
            "SELECT fk.object_id, fk.name, fk.parent_object_id, fk.referenced_object_id, " +
            "  COL_NAME(fkc.parent_object_id, fkc.parent_column_id), c.is_nullable " +
            "FROM sys.foreign_keys fk JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id " +
            "JOIN sys.columns c ON c.object_id = fkc.parent_object_id AND c.column_id = fkc.parent_column_id " +
            "ORDER BY fk.object_id, fkc.constraint_column_id";

        private const string CodeReferencesSql =
            "SELECT DISTINCT OBJECT_SCHEMA_NAME(d.referencing_id), OBJECT_NAME(d.referencing_id), o.type_desc, " +
            "  d.referenced_server_name, d.referenced_database_name, ISNULL(d.referenced_schema_name, 'dbo'), d.referenced_entity_name " +
            "FROM sys.sql_expression_dependencies d JOIN sys.objects o ON o.object_id = d.referencing_id " +
            "WHERE d.referenced_database_name IS NOT NULL AND d.referenced_database_name <> DB_NAME()";

        private static readonly Regex NonIdentifier = new Regex(@"[^A-Za-z0-9_]", RegexOptions.Compiled);
        private static readonly Regex TablePrefix = new Regex(@"^(tbl_?|tb_|t_)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly HashSet<string> IntegerTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "tinyint", "smallint", "int", "bigint" };

        // ---- model ----

        private sealed class Table
        {
            public string Database;
            public string Schema;
            public string Name;
            public string Key;          // database|object_id
            public string EntityId;     // Mermaid identifier
            public readonly List<Column> Columns = new List<Column>();
            public Column[] PrimaryKey => Columns.Where(c => c.IsPk).ToArray();
            public string FullName => Database + "." + Schema + "." + Name;
        }

        private sealed class Column
        {
            public string Name;
            public string Type;
            public bool Nullable;
            public bool IsPk;
            public bool IsFk;
            public bool IsInferredFk;
        }

        private sealed class Relationship
        {
            public string Name;
            public Table From;          // referencing table (many side)
            public Table To;            // referenced table (one side)
            public string[] FromColumns;
            public string ToColumn;
            public bool Optional;
            public bool Inferred;
            public string Reason;
        }

        private sealed class CodeReference
        {
            public string Database;
            public string Object;
            public string Type;
            public string Target;
            public Table TargetTable;
        }

        // ---- public API ----

        /// <summary>Databases the login can open on this server (online, accessible, not system databases).</summary>
        public static IReadOnlyList<(string Name, bool IsCurrent)> ListDatabases(IDbConnection connection)
        {
            var result = new List<(string, bool)>();
            using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    "SELECT name, CAST(CASE WHEN name = DB_NAME() THEN 1 ELSE 0 END AS bit) FROM sys.databases " +
                    "WHERE state = 0 AND HAS_DBACCESS(name) = 1 AND (database_id > 4 OR name = DB_NAME()) ORDER BY name";
                command.CommandTimeout = 30;
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read()) result.Add((reader.GetString(0), reader.GetBoolean(1)));
                }
            }
            return result;
        }

        public static DiagramResult Build(IDbConnection connection, DiagramOptions options)
        {
            options = options ?? new DiagramOptions();
            var startDatabase = connection.Database;
            var databases = (options.Databases ?? Array.Empty<string>()).Where(d => !string.IsNullOrWhiteSpace(d))
                .Select(d => d.Trim().Trim('[', ']')).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (databases.Count == 0) databases.Add(startDatabase);

            var tables = new List<Table>();
            var relationships = new List<Relationship>();
            var codeReferences = new List<CodeReference>();
            var warnings = new List<string>();

            var loaded = new List<string>();
            foreach (var database in databases)
            {
                try
                {
                    connection.ChangeDatabase(database);
                    ReadDatabase(connection, database, options, tables, relationships, codeReferences);
                    loaded.Add(database);
                }
                catch (Exception ex)
                {
                    warnings.Add("Skipped " + database + ": " + ex.Message);
                }
            }
            try { connection.ChangeDatabase(startDatabase); } catch { /* connection is closed afterwards anyway */ }

            var requested = databases;
            databases = loaded;
            if (tables.Count == 0)
            {
                throw new InvalidOperationException("No user tables found in " + string.Join(", ", requested)
                    + (warnings.Count > 0 ? " (" + string.Join("; ", warnings) + ")" : string.Empty) + ".");
            }

            if (options.InferRelationships) relationships.AddRange(Infer(tables, relationships));

            // Resolve code references to drawn tables where possible.
            var byFullName = tables.GroupBy(t => t.FullName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            foreach (var reference in codeReferences)
            {
                byFullName.TryGetValue(reference.Target, out reference.TargetTable);
            }

            // Trim: tables with relationships first, then the rest alphabetically.
            var related = new HashSet<Table>(relationships.SelectMany(r => new[] { r.From, r.To }));
            var drawn = tables.OrderBy(t => related.Contains(t) ? 0 : 1).ThenBy(t => t.Database).ThenBy(t => t.Schema).ThenBy(t => t.Name).Take(MaxTables).ToList();
            var drawnSet = new HashSet<Table>(drawn);
            var drawnRelationships = relationships.Where(r => drawnSet.Contains(r.From) && drawnSet.Contains(r.To)).ToList();
            if (tables.Count > drawn.Count) warnings.Add("Showing " + drawn.Count + " of " + tables.Count + " tables (tables with relationships first).");

            var multiDatabase = databases.Count > 1;
            var keyColumnsOnly = drawn.Count > KeyColumnsOnlyAbove;
            AssignEntityIds(drawn);

            var mermaid = BuildMermaid(drawn, drawnRelationships, databases, multiDatabase, keyColumnsOnly);
            var declared = drawnRelationships.Count(r => !r.Inferred);
            var inferred = drawnRelationships.Count(r => r.Inferred);
            var crossDatabase = drawnRelationships.Count(r => !string.Equals(r.From.Database, r.To.Database, StringComparison.OrdinalIgnoreCase));

            var summary = drawn.Count + " tables in " + string.Join(", ", databases) + ": " + declared + " foreign keys"
                          + (options.InferRelationships ? ", " + inferred + " inferred relationships (" + crossDatabase + " across databases)" : string.Empty)
                          + (keyColumnsOnly ? "; only key columns shown" : string.Empty) + ".";

            return new DiagramResult
            {
                Title = (multiDatabase ? string.Join(" + ", databases) : databases[0])
                        + (string.IsNullOrWhiteSpace(options.Schema) ? string.Empty : "." + options.Schema.Trim()) + " ER diagram",
                Mermaid = mermaid,
                Summary = summary,
                DetailsHtml = BuildDetails(databases, drawnRelationships.Where(r => r.Inferred).ToList(),
                    options.IncludeCodeReferences ? codeReferences : null, warnings, multiDatabase)
            };
        }

        // ---- reading ----

        private static void ReadDatabase(IDbConnection connection, string database, DiagramOptions options,
                                         List<Table> tables, List<Relationship> relationships, List<CodeReference> codeReferences)
        {
            var byId = new Dictionary<int, Table>();
            using (var reader = Query(connection, TablesSql, string.IsNullOrWhiteSpace(options.Schema) ? (object)DBNull.Value : options.Schema.Trim()))
            {
                while (reader.Read())
                {
                    var table = new Table { Database = database, Schema = reader.GetString(1), Name = reader.GetString(2) };
                    table.Key = database + "|" + reader.GetInt32(0);
                    byId[reader.GetInt32(0)] = table;
                }
            }

            using (var reader = Query(connection, ColumnsSql, null))
            {
                while (reader.Read())
                {
                    if (!byId.TryGetValue(reader.GetInt32(0), out var table)) continue;
                    table.Columns.Add(new Column
                    {
                        Name = reader.GetString(1),
                        Type = reader.IsDBNull(2) ? "unknown" : reader.GetString(2),
                        Nullable = reader.GetBoolean(3),
                        IsPk = reader.GetBoolean(4)
                    });
                }
            }

            using (var reader = Query(connection, ForeignKeysSql, null))
            {
                Relationship current = null;
                var currentId = -1;
                var columns = new List<string>();
                while (reader.Read())
                {
                    var fkId = reader.GetInt32(0);
                    if (fkId != currentId)
                    {
                        Flush(current, columns, relationships);
                        currentId = fkId;
                        current = byId.TryGetValue(reader.GetInt32(2), out var from) && byId.TryGetValue(reader.GetInt32(3), out var to)
                            ? new Relationship { Name = reader.GetString(1), From = from, To = to }
                            : null;
                        columns = new List<string>();
                    }
                    if (current == null) continue;
                    var columnName = reader.GetString(4);
                    columns.Add(columnName);
                    if (reader.GetBoolean(5)) current.Optional = true;
                    var column = current.From.Columns.FirstOrDefault(c => c.Name == columnName);
                    if (column != null) column.IsFk = true;
                }
                Flush(current, columns, relationships);
            }

            tables.AddRange(byId.Values);

            if (!options.IncludeCodeReferences) return;
            using (var reader = Query(connection, CodeReferencesSql, null))
            {
                while (reader.Read())
                {
                    var server = reader.IsDBNull(3) ? null : reader.GetString(3);
                    codeReferences.Add(new CodeReference
                    {
                        Database = database,
                        Object = reader.GetString(0) + "." + reader.GetString(1),
                        Type = reader.GetString(2).Replace('_', ' ').ToLowerInvariant(),
                        Target = (server != null ? server + "." : string.Empty) + reader.GetString(4) + "." + reader.GetString(5) + "." + reader.GetString(6)
                    });
                }
            }
        }

        private static void Flush(Relationship relationship, List<string> columns, List<Relationship> relationships)
        {
            if (relationship == null || columns.Count == 0) return;
            relationship.FromColumns = columns.ToArray();
            relationships.Add(relationship);
        }

        private static IDataReader Query(IDbConnection connection, string sql, object schema)
        {
            var command = connection.CreateCommand();
            command.CommandText = sql; // nosemgrep: csharp.lang.security.sqli.csharp-sqli.csharp-sqli - constant catalog queries, @schema is a parameter
            command.CommandTimeout = 60;
            if (schema != null)
            {
                var parameter = command.CreateParameter();
                parameter.ParameterName = "@schema";
                parameter.Value = schema;
                command.Parameters.Add(parameter);
            }
            return command.ExecuteReader();
        }

        // ---- inference ----

        /// <summary>
        /// A column such as Orders.CustomerId (or Customer_ID, CustomerKey) links to the single-column primary key of a
        /// table named Customer / Customers / tblCustomer, when the types are compatible. The best match wins:
        /// same database and schema, then same database, then any other selected database.
        /// </summary>
        private static IEnumerable<Relationship> Infer(List<Table> tables, List<Relationship> declared)
        {
            var targets = new Dictionary<string, List<(Table Table, Column Key, bool Strong)>>(StringComparer.OrdinalIgnoreCase);
            foreach (var table in tables)
            {
                var pk = table.PrimaryKey;
                if (pk.Length != 1) continue;
                foreach (var candidate in CandidateColumnNames(table.Name, pk[0].Name))
                {
                    if (!targets.TryGetValue(candidate.Name, out var list)) targets[candidate.Name] = list = new List<(Table, Column, bool)>();
                    list.Add((table, pk[0], candidate.Strong));
                }
            }

            var declaredColumns = new HashSet<string>(declared.SelectMany(r => r.FromColumns.Select(c => r.From.Key + "|" + c)), StringComparer.OrdinalIgnoreCase);
            var declaredPairs = new HashSet<string>(declared.Select(r => r.From.Key + ">" + r.To.Key));

            foreach (var table in tables)
            {
                var pk = table.PrimaryKey;
                foreach (var column in table.Columns)
                {
                    if (string.Equals(column.Name, "id", StringComparison.OrdinalIgnoreCase)) continue;
                    if (declaredColumns.Contains(table.Key + "|" + column.Name)) continue;
                    if (!targets.TryGetValue(column.Name, out var matches)) continue;

                    var isOwnKey = pk.Length == 1 && pk[0] == column;
                    var compatible = matches
                        .Where(m => m.Table != table && TypesCompatible(column.Type, m.Key.Type))
                        // A table's own key only points elsewhere when it is named after that table (CustomerProfile.CustomerId -> Customer).
                        .Where(m => m.Strong || !isOwnKey)
                        .ToList();
                    if (compatible.Count == 0) continue;

                    // Named-after-the-table matches beat same-key-name matches; then same schema, same database, other database.
                    var best = compatible
                        .GroupBy(m => (m.Strong ? 0 : 10) + Rank(table, m.Table))
                        .OrderBy(g => g.Key)
                        .First()
                        .Take(MaxCandidatesPerColumn)
                        .ToList();

                    foreach (var match in best)
                    {
                        if (declaredPairs.Contains(table.Key + ">" + match.Table.Key)) continue;

                        // A table whose whole primary key is this column is a 1:1 extension of the target.
                        column.IsInferredFk = true;
                        yield return new Relationship
                        {
                            Name = "inferred: " + column.Name,
                            From = table,
                            To = match.Table,
                            FromColumns = new[] { column.Name },
                            ToColumn = match.Key.Name,
                            Optional = column.Nullable,
                            Inferred = true,
                            Reason = (isOwnKey ? "1:1 - " : string.Empty) + "name and type (" + column.Type + ") match the primary key"
                                     + (best.Count > 1 ? "; ambiguous, " + best.Count + " candidates" : string.Empty)
                        };
                    }
                }
            }
        }

        /// <summary>
        /// Column names that would reference this table's key. Strong = derived from the table name
        /// (Customer + Id); weak = the same distinctive key name (ProductCode in tblProducts).
        /// </summary>
        private static IEnumerable<(string Name, bool Strong)> CandidateColumnNames(string tableName, string keyName)
        {
            var baseName = TablePrefix.Replace(tableName, string.Empty);
            var forms = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { baseName, Singular(baseName) };
            var names = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            var genericKey = string.Equals(keyName, "id", StringComparison.OrdinalIgnoreCase);

            foreach (var form in forms.Where(f => f.Length > 1))
            {
                names[form + "Id"] = true;
                names[form + "_Id"] = true;
                names[form + "Key"] = true;
                names[form + "_Key"] = true;
                if (!genericKey)
                {
                    names[form + keyName] = true;
                    names[form + "_" + keyName] = true;
                }
            }

            if (!genericKey && keyName.Length > 3 && !names.ContainsKey(keyName)) names[keyName] = false;
            return names.Select(n => (n.Key, n.Value));
        }

        private static string Singular(string name)
        {
            if (name.EndsWith("ies", StringComparison.OrdinalIgnoreCase) && name.Length > 3) return name.Substring(0, name.Length - 3) + "y";
            if (Regex.IsMatch(name, "(ss|sh|ch|x|z)es$", RegexOptions.IgnoreCase)) return name.Substring(0, name.Length - 2);
            if (name.EndsWith("s", StringComparison.OrdinalIgnoreCase) && !name.EndsWith("ss", StringComparison.OrdinalIgnoreCase) && name.Length > 1)
            {
                return name.Substring(0, name.Length - 1);
            }
            return name;
        }

        private static bool TypesCompatible(string a, string b)
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase) || (IntegerTypes.Contains(a) && IntegerTypes.Contains(b));
        }

        private static int Rank(Table from, Table to)
        {
            var sameDatabase = string.Equals(from.Database, to.Database, StringComparison.OrdinalIgnoreCase);
            if (sameDatabase && string.Equals(from.Schema, to.Schema, StringComparison.OrdinalIgnoreCase)) return 0;
            return sameDatabase ? 1 : 2;
        }

        // ---- output ----

        private static void AssignEntityIds(List<Table> tables)
        {
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var table in tables)
            {
                var id = NonIdentifier.Replace(table.Database + "__" + table.Schema + "__" + table.Name, "_");
                if (char.IsDigit(id[0])) id = "t_" + id;
                while (!used.Add(id)) id += "_";
                table.EntityId = id;
            }
        }

        private static string BuildMermaid(List<Table> tables, List<Relationship> relationships, List<string> databases, bool multiDatabase, bool keyColumnsOnly)
        {
            var mermaid = new StringBuilder("erDiagram").AppendLine();
            foreach (var table in tables)
            {
                var label = multiDatabase ? table.FullName : table.Schema + "." + table.Name;
                mermaid.Append("  ").Append(table.EntityId).Append("[\"").Append(Label(label)).AppendLine("\"] {");

                var shown = keyColumnsOnly ? table.Columns.Where(c => c.IsPk || c.IsFk || c.IsInferredFk).ToList() : table.Columns;
                foreach (var column in shown.Take(MaxColumnsPerTable))
                {
                    mermaid.Append("    ").Append(Token(column.Type, "t_")).Append(' ').Append(Token(column.Name, "c_"));
                    var keys = new List<string>();
                    if (column.IsPk) keys.Add("PK");
                    if (column.IsFk || column.IsInferredFk) keys.Add("FK");
                    if (keys.Count > 0) mermaid.Append(' ').Append(string.Join(", ", keys));

                    var comment = new List<string>();
                    if (Token(column.Name, "c_") != column.Name) comment.Add(column.Name);
                    if (column.IsInferredFk && !column.IsFk) comment.Add("inferred");
                    if (comment.Count > 0) mermaid.Append(" \"").Append(Label(string.Join(", ", comment))).Append('"');
                    mermaid.AppendLine();
                }
                var hidden = table.Columns.Count - Math.Min(shown.Count, MaxColumnsPerTable);
                if (hidden > 0) mermaid.Append("    more columns \"").Append(hidden).AppendLine(" more not shown\"");
                mermaid.AppendLine("  }");
            }

            foreach (var relationship in relationships)
            {
                // The referencing columns are the referencing table's whole primary key: one-to-one.
                var fromPk = relationship.From.PrimaryKey.Select(c => c.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase);
                var oneToOne = fromPk.SequenceEqual(relationship.FromColumns.OrderBy(n => n, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
                var line = relationship.Inferred ? ".." : "--";
                var left = relationship.Optional ? "|o" : "||";
                var right = oneToOne ? "o|" : "o{";

                // Referenced (one) side on the left, referencing (many) side on the right.
                mermaid.Append("  ").Append(relationship.To.EntityId).Append(' ').Append(left).Append(line).Append(right).Append(' ')
                       .Append(relationship.From.EntityId).Append(" : \"").Append(Label(relationship.Name)).AppendLine("\"");
            }

            // One colour per database.
            for (var i = 0; i < databases.Count; i++)
            {
                var members = tables.Where(t => string.Equals(t.Database, databases[i], StringComparison.OrdinalIgnoreCase)).Select(t => t.EntityId).ToList();
                if (members.Count == 0) continue;
                mermaid.Append("  classDef db").Append(i).Append(' ').AppendLine(Palette[i % Palette.Length]);
                mermaid.Append("  class ").Append(string.Join(",", members)).Append(" db").AppendLine(i.ToString());
            }
            return mermaid.ToString();
        }

        private static string BuildDetails(List<string> databases, List<Relationship> inferred, List<CodeReference> codeReferences,
                                           List<string> warnings, bool multiDatabase)
        {
            var html = new StringBuilder();
            html.Append("<div class=\"legend\"><b>Legend:</b> solid line = declared foreign key; dashed line = inferred from matching column name and type");
            if (multiDatabase)
            {
                html.Append("; colour = database: ");
                for (var i = 0; i < databases.Count; i++)
                {
                    var fill = Palette[i % Palette.Length].Split(',')[0].Substring("fill:".Length);
                    html.Append("<span class=\"swatch\" style=\"background:").Append(fill).Append("\"></span>").Append(Encode(databases[i])).Append(' ');
                }
            }
            html.AppendLine("</div>");

            foreach (var warning in warnings)
            {
                html.Append("<p class=\"warn\">").Append(Encode(warning)).AppendLine("</p>");
            }

            if (inferred.Count > 0)
            {
                html.AppendLine("<details open><summary>Inferred relationships (" + inferred.Count + ") - review before relying on them</summary>");
                html.AppendLine("<table><tr><th>Column</th><th>Probably references</th><th>Why</th></tr>");
                foreach (var r in inferred.OrderBy(r => r.From.FullName))
                {
                    html.Append("<tr><td>").Append(Encode(r.From.FullName + "." + r.FromColumns[0]))
                        .Append("</td><td>").Append(Encode(r.To.FullName + "." + r.ToColumn))
                        .Append(string.Equals(r.From.Database, r.To.Database, StringComparison.OrdinalIgnoreCase) ? string.Empty : " <b>(cross-database)</b>")
                        .Append("</td><td>").Append(Encode(r.Reason)).AppendLine("</td></tr>");
                }
                html.AppendLine("</table>");
                html.AppendLine("<p class=\"hint\">To enforce one inside a database: ALTER TABLE ... ADD CONSTRAINT ... FOREIGN KEY. Across databases SQL Server cannot enforce it; use a trigger or check it in your load process.</p>");
                html.AppendLine("</details>");
            }

            if (codeReferences != null && codeReferences.Count > 0)
            {
                html.AppendLine("<details open><summary>Code that reads other databases (" + codeReferences.Count + ")</summary>");
                html.AppendLine("<table><tr><th>Object</th><th>Type</th><th>References</th></tr>");
                foreach (var c in codeReferences.OrderBy(c => c.Database).ThenBy(c => c.Object))
                {
                    html.Append("<tr><td>").Append(Encode(c.Database + "." + c.Object)).Append("</td><td>").Append(Encode(c.Type))
                        .Append("</td><td>").Append(Encode(c.Target)).Append(c.TargetTable != null ? " (in diagram)" : string.Empty).AppendLine("</td></tr>");
                }
                html.AppendLine("</table></details>");
            }
            return html.ToString();
        }

        private static string Token(string text, string prefix)
        {
            var token = NonIdentifier.Replace(text, "_");
            return token.Length == 0 || char.IsDigit(token[0]) ? prefix + token : token;
        }

        private static string Label(string text)
        {
            return text.Replace("\"", "'");
        }

        private static string Encode(string text)
        {
            return WebUtility.HtmlEncode(text ?? string.Empty);
        }
    }
}
