using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;
using Newtonsoft.Json.Linq;
using SsmsChatExtension.Database;
using SsmsChatExtension.Diagrams;
using SsmsChatExtension.Editor;
using SsmsChatExtension.Services.Claude;

namespace SsmsChatExtension.Mcp
{
    /// <summary>
    /// The "ssms" MCP tools Claude uses to understand the database, run SQL (changes need the user's approval
    /// unless allowed in settings) and edit or execute query windows.
    /// </summary>
    internal sealed class SsmsMcpTools : IMcpToolProvider
    {
        private const int MaxDefinitionLength = 50000;
        private const int MaxPreviewLength = 2500;
        private const int IdYes = 6;

        private readonly Func<ClaudeSettings> _settings;

        public SsmsMcpTools(Func<ClaudeSettings> settings)
        {
            _settings = settings;
        }

        public string Instructions
        {
            get
            {
                return "Tools for the SQL Server Management Studio session the user is chatting from. "
                     + "Database tools use the connection of the active query window. run_query is always read-only; "
                     + "execute_sql and execute_query_window can change the database and may need the user's approval in SSMS. "
                     + "replace_query_window and open_new_query_window only change the editor text.";
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

                Tool("get_dependencies",
                    "What an object (procedure, view, function, trigger, table) uses and what uses it, from sys.sql_expression_dependencies. "
                    + "Use it to understand a stored procedure or to check the impact of changing an object.",
                    new JObject
                    {
                        ["object"] = Str("Object name, ideally schema-qualified, for example dbo.usp_PlaceOrder."),
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

                Tool("execute_sql",
                    "Run T-SQL that may change the database (INSERT / UPDATE / DELETE, DDL such as CREATE or ALTER, EXEC of procedures) and commit it. "
                    + "GO batch separators are supported; batches run in order and stop at the first error. "
                    + "Depending on the user's SSMS Claude Chat settings, the user must approve it in SSMS first, or database changes are turned off. "
                    + "Use run_query for read-only exploration. Returns result sets and rows affected.",
                    new JObject
                    {
                        ["sql"] = Str("The script to run."),
                        ["database"] = Str("Database name. Default: the connection's current database."),
                        ["reason"] = Str("One sentence for the user explaining what this does and why; shown in the approval dialog."),
                        ["max_rows"] = new JObject
                        {
                            ["type"] = "integer",
                            ["description"] = "Rows to return per result set, 1-" + ReadOnlySql.MaxRowsLimit + ". Default " + ReadOnlySql.DefaultMaxRows + "."
                        }
                    },
                    "sql", "reason"),

                Tool("execute_query_window",
                    "Execute the active SSMS query window like pressing F5 (only the selection if text is selected), so the user sees the results grid and messages in SSMS. "
                    + "The results are not returned to you; use run_query or execute_sql when you need them. Needs the same approval as execute_sql.",
                    new JObject { ["reason"] = Str("One sentence for the user explaining what this does and why; shown in the approval dialog.") },
                    "reason"),

                Tool("show_diagram",
                    "Render a Mermaid diagram and open it for the user in their browser (with SVG download). "
                    + "Use flowchart TD for the logic of a stored procedure or a data flow, erDiagram for tables and their relationships, "
                    + "sequenceDiagram for call chains between procedures. Use the real object names you looked up.",
                    new JObject
                    {
                        ["title"] = Str("Short title, for example 'dbo.usp_PlaceOrder flow'."),
                        ["mermaid"] = Str("The Mermaid diagram source, without the ``` fence."),
                        ["description"] = Str("Optional one or two sentences shown under the title.")
                    },
                    "title", "mermaid"),

                Tool("show_database_diagram",
                    "Build an ER diagram of one or more databases (or one schema) from the catalog and open it in the user's browser: tables, key columns, "
                    + "declared foreign keys (solid lines) and relationships inferred from matching column names and types (dashed lines), also across databases, "
                    + "plus a list of views / procedures that read other databases. Faster and more complete than writing the Mermaid yourself.",
                    new JObject
                    {
                        ["databases"] = new JObject
                        {
                            ["type"] = "array",
                            ["items"] = new JObject { ["type"] = "string" },
                            ["description"] = "Database names. Default: the connection's current database. Use list_databases to see what exists."
                        },
                        ["schema"] = Str("Only tables in this schema, for example dbo."),
                        ["infer_relationships"] = new JObject { ["type"] = "boolean", ["description"] = "Add inferred (dashed) relationships. Default true." }
                    }),

                Tool("refresh_object_explorer",
                    "Refresh the database in SSMS Object Explorer and the IntelliSense cache so the user sees new or changed objects. "
                    + "execute_sql already does this after schema changes; use it when objects changed another way.",
                    new JObject { ["database"] = Str("Database name. Default: the connection's current database.") }),

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
                case "get_dependencies": return await GetDependenciesAsync(arguments);
                case "run_query": return await RunQueryAsync(arguments);
                case "show_diagram": return await ShowDiagramAsync(arguments);
                case "show_database_diagram": return await ShowDatabaseDiagramAsync(arguments);
                case "refresh_object_explorer": return await RefreshObjectExplorerAsync(arguments);
                case "execute_sql": return await ExecuteSqlAsync(arguments);
                case "execute_query_window": return await ExecuteQueryWindowAsync(arguments);
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

        private async Task<string> GetDependenciesAsync(JObject arguments)
        {
            var objectName = (string)arguments["object"];
            if (string.IsNullOrWhiteSpace(objectName)) throw new ArgumentException("'object' is required.");

            const string sql =
                "DECLARE @id int = OBJECT_ID(@name); " +
                "IF @id IS NULL BEGIN SELECT 'Object not found: ' + @name AS error; RETURN; END; " +
                "SELECT DISTINCT COALESCE(d.referenced_server_name + '.', '') + COALESCE(d.referenced_database_name + '.', '') " +
                "  + COALESCE(d.referenced_schema_name + '.', '') + d.referenced_entity_name AS uses, " +
                "  o.type_desc, d.is_caller_dependent, d.is_ambiguous " +
                "FROM sys.sql_expression_dependencies d LEFT JOIN sys.objects o ON o.object_id = d.referenced_id " +
                "WHERE d.referencing_id = @id ORDER BY uses; " +
                "SELECT DISTINCT OBJECT_SCHEMA_NAME(d.referencing_id) + '.' + OBJECT_NAME(d.referencing_id) AS used_by, o.type_desc " +
                "FROM sys.sql_expression_dependencies d JOIN sys.objects o ON o.object_id = d.referencing_id " +
                "WHERE d.referenced_id = @id ORDER BY used_by;";

            var connection = await GetConnectionAsync();
            return await WithConnectionAsync(connection, (string)arguments["database"], c =>
                "Result sets: 1 objects it uses (dynamic SQL is not tracked), 2 objects that use it." + Environment.NewLine
                + ReadOnlySql.Execute(c, sql, new Dictionary<string, object> { ["@name"] = objectName }, ReadOnlySql.MaxRowsLimit));
        }

        private static async Task<string> ShowDiagramAsync(JObject arguments)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var path = DiagramViewer.Show((string)arguments["title"], (string)arguments["mermaid"], (string)arguments["description"]);
            return "Opened the diagram in the user's browser (saved as " + path + "). "
                   + "If the page reports a Mermaid syntax error, the user will tell you; fix it and call show_diagram again.";
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

        private async Task<string> ExecuteSqlAsync(JObject arguments)
        {
            var sql = (string)arguments["sql"];
            if (string.IsNullOrWhiteSpace(sql)) throw new ArgumentException("'sql' is required.");

            var database = (string)arguments["database"];
            var connection = await GetConnectionAsync();
            await RequireApprovalAsync("Claude wants to run SQL", connection, database, (string)arguments["reason"], sql);

            var maxRows = (int?)arguments["max_rows"] ?? ReadOnlySql.DefaultMaxRows;
            string databaseAfterRun = null;
            var output = await WithConnectionAsync(connection, database, c =>
            {
                try
                {
                    return SqlExecutor.Run(c, sql, maxRows);
                }
                finally
                {
                    databaseAfterRun = SafeDatabaseName(c);
                }
            });

            // Make new / altered objects visible in Object Explorer and IntelliSense right away.
            if (SsmsObjectExplorer.ChangesSchema(sql))
            {
                output += Environment.NewLine + await SsmsObjectExplorer.RefreshAsync(connection.Server, databaseAfterRun ?? database ?? connection.Database);
            }
            return output;
        }

        private async Task<string> RefreshObjectExplorerAsync(JObject arguments)
        {
            var connection = await GetConnectionAsync();
            var database = (string)arguments["database"];
            return await SsmsObjectExplorer.RefreshAsync(connection.Server, string.IsNullOrWhiteSpace(database) ? connection.Database : database);
        }

        private async Task<string> ShowDatabaseDiagramAsync(JObject arguments)
        {
            var connection = await GetConnectionAsync();
            var options = new DiagramOptions
            {
                Databases = (arguments["databases"] as JArray)?.Select(d => (string)d).Where(d => !string.IsNullOrWhiteSpace(d)).ToList()
                            ?? new List<string>(),
                Schema = (string)arguments["schema"],
                InferRelationships = (bool?)arguments["infer_relationships"] ?? true
            };

            var result = await WithConnectionAsync(connection, null, c => SchemaDiagram.Build(c, options));
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var path = DiagramViewer.Show(result.Title, result.Mermaid, result.Summary, result.DetailsHtml);
            return "Opened the ER diagram in the user's browser: " + result.Summary + " Saved as " + path + "."
                   + " Inferred relationships are guesses from column names; mention that when you discuss them.";
        }

        private static string SafeDatabaseName(IDbConnection connection)
        {
            try
            {
                return connection.State == ConnectionState.Open ? connection.Database : null;
            }
            catch
            {
                return null;
            }
        }

        private async Task<string> ExecuteQueryWindowAsync(JObject arguments)
        {
            var snapshot = await SsmsEditor.GetActiveDocumentAsync();
            if (snapshot == null) throw new InvalidOperationException("No query window is open.");

            var script = snapshot.SelectedText.Length > 0 ? snapshot.SelectedText : snapshot.Text;
            if (string.IsNullOrWhiteSpace(script)) throw new InvalidOperationException("The active query window is empty.");

            var connection = await SsmsConnection.GetAsync(null);
            await RequireApprovalAsync("Claude wants to execute " + snapshot.Name, connection, null, (string)arguments["reason"], script);

            if (!await SsmsEditor.ExecuteActiveQueryAsync()) throw new InvalidOperationException("No query window is open.");
            if (SsmsObjectExplorer.ChangesSchema(script) && connection != null)
            {
                SsmsObjectExplorer.RefreshWhenQueryCompletes(connection.Server, connection.Database);
            }
            return "Started executing " + (snapshot.SelectedText.Length > 0 ? "the selected text of " : string.Empty) + snapshot.Name
                   + " in SSMS. The user sees the results and messages there; ask them if you need to know the outcome.";
        }

        /// <summary>Applies the "database changes" setting: refuse, ask the user (Yes / No, default No), or allow.</summary>
        private async Task RequireApprovalAsync(string title, ActiveConnection connection, string database, string reason, string sql)
        {
            var mode = _settings().DatabaseChanges;
            if (mode == DatabaseChangeMode.Off)
            {
                throw new InvalidOperationException(
                    "Database changes are turned off in SSMS Claude Chat settings, so nothing was run. Use run_query for read-only queries, "
                    + "or give the user the script (replace_query_window) and tell them they can enable changes under Settings.");
            }
            if (mode == DatabaseChangeMode.Allow) return;

            var target = connection == null
                ? "(not connected)"
                : connection.Server + " / " + (string.IsNullOrWhiteSpace(database)
                    ? (string.IsNullOrEmpty(connection.Database) ? "default database" : connection.Database)
                    : database.Trim());
            var preview = sql.Trim();
            if (preview.Length > MaxPreviewLength)
            {
                preview = preview.Substring(0, MaxPreviewLength) + Environment.NewLine + "... (" + sql.Length + " characters in total)";
            }

            var message = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(reason)) message.AppendLine(reason.Trim()).AppendLine();
            message.Append("Server / database: ").AppendLine(target).AppendLine();
            message.AppendLine(preview).AppendLine();
            message.Append("Run it now? Changes are committed.");

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var answer = VsShellUtilities.ShowMessageBox(ServiceProvider.GlobalProvider, message.ToString(), "SSMS Claude Chat - " + title,
                OLEMSGICON.OLEMSGICON_WARNING, OLEMSGBUTTON.OLEMSGBUTTON_YESNO, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_SECOND);
            if (answer != IdYes)
            {
                throw new InvalidOperationException("The user declined to run this SQL. Nothing was executed. Ask them what to change.");
            }
        }

        private async Task<ActiveConnection> GetConnectionAsync()
        {
            var connection = await SsmsConnection.GetAsync(_settings().ConnectionStringOverride);
            if (connection == null)
            {
                throw new InvalidOperationException(
                    "No database connection: the user has no connected query window active in SSMS. "
                    + "Ask them to open or click into a connected query window (or set a connection string in SSMS Claude Chat settings).");
            }
            return connection;
        }

        private static async Task<T> WithConnectionAsync<T>(ActiveConnection connection, string database, Func<IDbConnection, T> work)
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
                command.CommandText = sql; // nosemgrep: csharp.lang.security.sqli.csharp-sqli.csharp-sqli - constant query, @name is a parameter
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
