using System;
using System.Collections.Generic;
using System.Data;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;
using Newtonsoft.Json.Linq;
using SsmsChatExtension.Database;
using SsmsChatExtension.Editor;

namespace SsmsChatExtension.Mcp
{
    /// <summary>
    /// The "ssms" MCP tools Claude uses to understand the database (read-only) and to edit query windows.
    /// </summary>
    internal sealed class SsmsMcpTools : IMcpToolProvider
    {
        private const int MaxDefinitionLength = 50000;

        private readonly Func<string> _connectionStringOverride;

        public SsmsMcpTools(Func<string> connectionStringOverride)
        {
            _connectionStringOverride = connectionStringOverride;
        }

        public string Instructions
        {
            get
            {
                return "Tools for the SQL Server Management Studio session the user is chatting from. "
                     + "Database tools are read-only and use the connection of the active query window. "
                     + "Query window tools change what the user sees in SSMS; they never execute SQL.";
            }
        }

        public JArray ListTools()
        {
            return new JArray
            {
                Tool("get_connection_info",
                    "Server, current database, login and SQL Server version of the active SSMS query window connection. Call this first to learn where you are.",
                    new JObject()),

                Tool("list_databases", "List the databases on the connected server.", new JObject()),

                Tool("list_objects",
                    "List user tables, views, stored procedures, functions and triggers in a database.",
                    new JObject
                    {
                        ["database"] = Str("Database name. Default: the connection's current database."),
                        ["schema"] = Str("Only this schema, for example dbo."),
                        ["name_like"] = Str("SQL LIKE pattern on the object name, for example %Order%."),
                        ["type"] = new JObject
                        {
                            ["type"] = "string",
                            ["enum"] = new JArray("all", "table", "view", "procedure", "function", "trigger"),
                            ["description"] = "Object type filter. Default: all."
                        }
                    }),

                Tool("describe_object",
                    "Columns, keys, indexes, foreign keys, parameters, row count and the full T-SQL definition (views, procedures, functions, triggers) of one object.",
                    new JObject
                    {
                        ["object"] = Str("Object name, ideally schema-qualified, for example dbo.Orders."),
                        ["database"] = Str("Database name. Default: the connection's current database.")
                    },
                    "object"),

                Tool("run_query",
                    "Run a read-only T-SQL query (SELECT / WITH / DECLARE) and get the results as tab-separated text. "
                    + "Data-changing statements, EXEC, transactions and USE are rejected, and everything runs in a transaction that is rolled back. "
                    + "Use the database argument instead of USE. One batch per call (no GO).",
                    new JObject
                    {
                        ["sql"] = Str("The query."),
                        ["database"] = Str("Database name. Default: the connection's current database."),
                        ["max_rows"] = new JObject
                        {
                            ["type"] = "integer",
                            ["description"] = "Rows to return per result set, 1-" + ReadOnlySql.MaxRowsLimit + ". Default " + ReadOnlySql.DefaultMaxRows + "."
                        }
                    },
                    "sql"),

                Tool("get_query_window",
                    "Get the name, file path, full text and current selection of the active SSMS query window.",
                    new JObject()),

                Tool("replace_query_window",
                    "Replace the entire text of the active SSMS query window. The user can undo it with Ctrl+Z. It is not executed. "
                    + "Call get_query_window first so you do not lose the user's work.",
                    new JObject { ["text"] = Str("The complete new script.") },
                    "text"),

                Tool("open_new_query_window",
                    "Open a new SSMS query window (connected like the active one) containing the given script. It is not executed.",
                    new JObject { ["text"] = Str("The script to put in the new window.") },
                    "text")
            };
        }

        public async Task<string> CallToolAsync(string name, JObject arguments, CancellationToken cancellationToken)
        {
            switch (name)
            {
                case "get_connection_info": return await GetConnectionInfoAsync();
                case "list_databases": return await ListDatabasesAsync(arguments);
                case "list_objects": return await ListObjectsAsync(arguments);
                case "describe_object": return await DescribeObjectAsync(arguments);
                case "run_query": return await RunQueryAsync(arguments);
                case "get_query_window": return await GetQueryWindowAsync();
                case "replace_query_window": return await ReplaceQueryWindowAsync(arguments);
                case "open_new_query_window": return await OpenNewQueryWindowAsync(arguments);
                default: throw new InvalidOperationException("Unknown tool: " + name);
            }
        }

        // ---- database tools ----

        private async Task<string> GetConnectionInfoAsync()
        {
            var connection = await GetConnectionAsync();
            var details = await WithConnectionAsync(connection, null, c => ReadOnlySql.Execute(c,
                "SELECT @@SERVERNAME AS server_name, DB_NAME() AS current_database, SUSER_SNAME() AS login_name, "
                + "SERVERPROPERTY('Edition') AS edition, SERVERPROPERTY('ProductVersion') AS product_version, "
                + "SERVERPROPERTY('EngineEdition') AS engine_edition",
                null, 1));

            return "Connection source: " + connection.Source + Environment.NewLine + details;
        }

        private async Task<string> ListDatabasesAsync(JObject arguments)
        {
            var connection = await GetConnectionAsync();
            return await WithConnectionAsync(connection, null, c => ReadOnlySql.Execute(c,
                "SELECT name, state_desc, recovery_model_desc, compatibility_level, create_date FROM sys.databases ORDER BY name",
                null, ReadOnlySql.MaxRowsLimit));
        }

        private async Task<string> ListObjectsAsync(JObject arguments)
        {
            string types;
            switch (((string)arguments["type"] ?? "all").ToLowerInvariant())
            {
                case "table": types = "'U'"; break;
                case "view": types = "'V'"; break;
                case "procedure": types = "'P','PC'"; break;
                case "function": types = "'FN','IF','TF','FS','FT'"; break;
                case "trigger": types = "'TR','TA'"; break;
                default: types = "'U','V','P','PC','FN','IF','TF','FS','FT','TR','TA','SN'"; break;
            }

            var sql =
                "SELECT s.name AS [schema], o.name AS [name], o.type_desc AS [type], o.modify_date " +
                "FROM sys.objects o JOIN sys.schemas s ON s.schema_id = o.schema_id " +
                "WHERE o.is_ms_shipped = 0 AND o.type IN (" + types + ") " +
                "AND (@schema IS NULL OR s.name = @schema) AND (@like IS NULL OR o.name LIKE @like) " +
                "ORDER BY s.name, o.type_desc, o.name";

            var parameters = new Dictionary<string, object>
            {
                ["@schema"] = NullIfEmpty((string)arguments["schema"]),
                ["@like"] = NullIfEmpty((string)arguments["name_like"])
            };

            var connection = await GetConnectionAsync();
            return await WithConnectionAsync(connection, (string)arguments["database"],
                c => ReadOnlySql.Execute(c, sql, parameters, ReadOnlySql.MaxRowsLimit));
        }

        private async Task<string> DescribeObjectAsync(JObject arguments)
        {
            var objectName = (string)arguments["object"];
            if (string.IsNullOrWhiteSpace(objectName)) throw new ArgumentException("'object' is required.");

            const string sql =
                "DECLARE @id int = OBJECT_ID(@name); " +
                "IF @id IS NULL BEGIN SELECT 'Object not found: ' + @name AS error; RETURN; END; " +
                "SELECT s.name AS [schema], o.name, o.type_desc, o.create_date, o.modify_date, " +
                "  (SELECT SUM(p.rows) FROM sys.partitions p WHERE p.object_id = @id AND p.index_id IN (0, 1)) AS row_count, " +
                "  CAST(ep.value AS nvarchar(4000)) AS description " +
                "FROM sys.objects o JOIN sys.schemas s ON s.schema_id = o.schema_id " +
                "LEFT JOIN sys.extended_properties ep ON ep.major_id = o.object_id AND ep.minor_id = 0 AND ep.class = 1 AND ep.name = 'MS_Description' " +
                "WHERE o.object_id = @id; " +
                "SELECT c.column_id, c.name AS column_name, TYPE_NAME(c.user_type_id) AS data_type, c.max_length, c.precision, c.scale, " +
                "  c.is_nullable, c.is_identity, c.is_computed, dc.definition AS default_value, cc.definition AS computed_definition " +
                "FROM sys.columns c " +
                "LEFT JOIN sys.default_constraints dc ON dc.object_id = c.default_object_id " +
                "LEFT JOIN sys.computed_columns cc ON cc.object_id = c.object_id AND cc.column_id = c.column_id " +
                "WHERE c.object_id = @id ORDER BY c.column_id; " +
                "SELECT i.name AS index_name, i.type_desc, i.is_primary_key, i.is_unique, i.has_filter, i.filter_definition, " +
                "  STUFF((SELECT ', ' + COL_NAME(ic.object_id, ic.column_id) + CASE WHEN ic.is_descending_key = 1 THEN ' DESC' ELSE '' END " +
                "         FROM sys.index_columns ic WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 0 " +
                "         ORDER BY ic.key_ordinal FOR XML PATH(''), TYPE).value('.', 'nvarchar(max)'), 1, 2, '') AS key_columns, " +
                "  STUFF((SELECT ', ' + COL_NAME(ic.object_id, ic.column_id) " +
                "         FROM sys.index_columns ic WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 1 " +
                "         FOR XML PATH(''), TYPE).value('.', 'nvarchar(max)'), 1, 2, '') AS included_columns " +
                "FROM sys.indexes i WHERE i.object_id = @id AND i.type > 0; " +
                "SELECT fk.name AS foreign_key, " +
                "  OBJECT_SCHEMA_NAME(fk.parent_object_id) + '.' + OBJECT_NAME(fk.parent_object_id) + '.' + COL_NAME(fkc.parent_object_id, fkc.parent_column_id) AS from_column, " +
                "  OBJECT_SCHEMA_NAME(fk.referenced_object_id) + '.' + OBJECT_NAME(fk.referenced_object_id) + '.' + COL_NAME(fkc.referenced_object_id, fkc.referenced_column_id) AS to_column " +
                "FROM sys.foreign_keys fk JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id " +
                "WHERE fk.parent_object_id = @id OR fk.referenced_object_id = @id; " +
                "SELECT p.parameter_id, p.name AS parameter_name, TYPE_NAME(p.user_type_id) AS data_type, p.max_length, p.is_output, p.has_default_value " +
                "FROM sys.parameters p WHERE p.object_id = @id ORDER BY p.parameter_id;";

            var connection = await GetConnectionAsync();
            return await WithConnectionAsync(connection, (string)arguments["database"], c =>
            {
                var output = new StringBuilder();
                output.AppendLine("Result sets: 1 object, 2 columns, 3 indexes, 4 foreign keys (both directions), 5 parameters.");
                output.AppendLine(ReadOnlySql.Execute(c, sql, new Dictionary<string, object> { ["@name"] = objectName }, ReadOnlySql.MaxRowsLimit));

                var definition = ExecuteScalar(c, "SELECT OBJECT_DEFINITION(OBJECT_ID(@name))", objectName) as string;
                if (!string.IsNullOrEmpty(definition))
                {
                    if (definition.Length > MaxDefinitionLength) definition = definition.Substring(0, MaxDefinitionLength) + Environment.NewLine + "-- ... truncated";
                    output.AppendLine("Definition:").AppendLine("```sql").AppendLine(definition.Trim()).AppendLine("```");
                }
                return output.ToString();
            });
        }

        private async Task<string> RunQueryAsync(JObject arguments)
        {
            var sql = (string)arguments["sql"];
            var error = ReadOnlySql.Validate(sql);
            if (error != null) throw new InvalidOperationException(error);

            var maxRows = (int?)arguments["max_rows"] ?? ReadOnlySql.DefaultMaxRows;
            var connection = await GetConnectionAsync();
            return await WithConnectionAsync(connection, (string)arguments["database"], c => ReadOnlySql.Run(c, sql, maxRows));
        }

        private async Task<ActiveConnection> GetConnectionAsync()
        {
            var connection = await SsmsConnection.GetAsync(_connectionStringOverride());
            if (connection == null)
            {
                throw new InvalidOperationException(
                    "No database connection: the user has no connected query window active in SSMS. "
                    + "Ask them to open or click into a connected query window (or set a connection string in SQL Chat settings).");
            }
            return connection;
        }

        private static async Task<string> WithConnectionAsync(ActiveConnection connection, string database, Func<IDbConnection, string> work)
        {
            // Database round-trips must not block the SSMS UI thread.
            await TaskScheduler.Default;

            using (var db = connection.Factory())
            {
                if (db.State != ConnectionState.Open) db.Open();
                if (!string.IsNullOrWhiteSpace(database)) db.ChangeDatabase(database.Trim().Trim('[', ']'));
                return work(db);
            }
        }

        private static object ExecuteScalar(IDbConnection connection, string sql, string name)
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = sql;
                command.CommandTimeout = 30;
                var parameter = command.CreateParameter();
                parameter.ParameterName = "@name";
                parameter.Value = name;
                command.Parameters.Add(parameter);
                var value = command.ExecuteScalar();
                return value == DBNull.Value ? null : value;
            }
        }

        // ---- query window tools ----

        private static async Task<string> GetQueryWindowAsync()
        {
            var snapshot = await SsmsEditor.GetActiveDocumentAsync();
            if (snapshot == null) return "No query window is open.";

            var output = new StringBuilder()
                .Append("Window: ").AppendLine(snapshot.Name)
                .Append("File: ").AppendLine(string.IsNullOrEmpty(snapshot.Path) ? "(not saved)" : snapshot.Path);
            if (snapshot.SelectedText.Length > 0)
            {
                output.AppendLine("Selected text:").AppendLine("```sql").AppendLine(snapshot.SelectedText).AppendLine("```");
            }
            output.AppendLine("Full text:").AppendLine("```sql").AppendLine(snapshot.Text).AppendLine("```");
            return output.ToString();
        }

        private static async Task<string> ReplaceQueryWindowAsync(JObject arguments)
        {
            var text = (string)arguments["text"] ?? string.Empty;
            var ok = await SsmsEditor.ReplaceAllTextAsync(text);
            return ok ? "Query window updated (" + text.Length + " characters). The user can undo with Ctrl+Z." : "No query window is open.";
        }

        private static async Task<string> OpenNewQueryWindowAsync(JObject arguments)
        {
            var ok = await SsmsEditor.OpenNewQueryAsync((string)arguments["text"] ?? string.Empty);
            return ok ? "Opened a new query window with the script." : "Could not open a new query window.";
        }

        // ---- schema helpers ----

        private static JObject Tool(string name, string description, JObject properties, params string[] required)
        {
            var schema = new JObject { ["type"] = "object", ["properties"] = properties };
            if (required.Length > 0) schema["required"] = new JArray(required);
            return new JObject { ["name"] = name, ["description"] = description, ["inputSchema"] = schema };
        }

        private static JObject Str(string description)
        {
            return new JObject { ["type"] = "string", ["description"] = description };
        }

        private static object NullIfEmpty(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
    }
}
