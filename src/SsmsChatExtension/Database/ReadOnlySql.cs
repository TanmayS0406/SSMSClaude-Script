using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace SsmsChatExtension.Database
{
    /// <summary>
    /// Runs SQL for Claude without letting it change anything. Two layers:
    /// 1. a keyword check that rejects anything that can write, run code or escape the transaction, and
    /// 2. every batch runs inside a transaction that is always rolled back.
    /// </summary>
    internal static class ReadOnlySql
    {
        public const int DefaultMaxRows = 100;
        public const int MaxRowsLimit = 1000;
        private const int MaxCellLength = 400;
        internal const int MaxOutputLength = 60000;
        private const int CommandTimeoutSeconds = 30;

        private static readonly HashSet<string> Forbidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "INSERT", "UPDATE", "DELETE", "MERGE", "TRUNCATE", "DROP", "ALTER", "CREATE", "INTO",
            "EXEC", "EXECUTE", "GRANT", "REVOKE", "DENY", "TRAN", "TRANSACTION", "COMMIT", "ROLLBACK", "SAVE",
            "BACKUP", "RESTORE", "DBCC", "SHUTDOWN", "KILL", "BULK", "RECONFIGURE", "CHECKPOINT", "USE",
            "OPENROWSET", "OPENQUERY", "OPENDATASOURCE", "WAITFOR", "SEND", "RECEIVE", "UPDATETEXT", "WRITETEXT"
        };

        // Comments, string literals and quoted identifiers, removed before the keyword check
        // so that e.g. a column called [Update] or the text 'delete' is not mistaken for a statement.
        private static readonly Regex Ignorable = new Regex(
            @"--[^\r\n]*|/\*.*?\*/|N?'(?:[^']|'')*'|\[(?:[^\]]|\]\])*\]|""(?:[^""]|"""")*""|[@#]+\w+",
            RegexOptions.Singleline | RegexOptions.Compiled);

        private static readonly Regex Word = new Regex(@"\b[A-Za-z_]+\b", RegexOptions.Compiled);

        /// <summary>Returns null when <paramref name="sql"/> looks read-only, otherwise the reason it was rejected.</summary>
        public static string Validate(string sql)
        {
            if (string.IsNullOrWhiteSpace(sql)) return "The query is empty.";
            if (Regex.IsMatch(sql, @"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase))
            {
                return "GO batch separators are not supported; send one batch per call.";
            }

            var stripped = Ignorable.Replace(sql, " ");
            var bad = Word.Matches(stripped).Cast<Match>().Select(m => m.Value).FirstOrDefault(Forbidden.Contains);
            return bad == null
                ? null
                : "Rejected: '" + bad.ToUpperInvariant() + "' is not allowed. Only read-only queries (SELECT / WITH / DECLARE) can run. "
                  + "If it is an object or column name, put it in [brackets].";
        }

        /// <summary>Runs a validated query and formats every result set as text. Changes are always rolled back.</summary>
        public static string Run(IDbConnection connection, string sql, int maxRows)
        {
            var error = Validate(sql);
            if (error != null) throw new InvalidOperationException(error);
            return Execute(connection, sql, null, maxRows);
        }

        /// <summary>
        /// Runs the extension's own catalog queries (not validated, may use parameters), also inside a rolled-back transaction.
        /// </summary>
        public static string Execute(IDbConnection connection, string sql, IDictionary<string, object> parameters, int maxRows)
        {
            maxRows = Math.Max(1, Math.Min(maxRows, MaxRowsLimit));
            var output = new StringBuilder();

            using (var transaction = connection.BeginTransaction())
            {
                try
                {
                    using (var command = connection.CreateCommand())
                    {
                        command.Transaction = transaction;
                        command.CommandText = sql;
                        command.CommandTimeout = CommandTimeoutSeconds;
                        if (parameters != null)
                        {
                            foreach (var pair in parameters)
                            {
                                var parameter = command.CreateParameter();
                                parameter.ParameterName = pair.Key;
                                parameter.Value = pair.Value ?? DBNull.Value;
                                command.Parameters.Add(parameter);
                            }
                        }

                        using (var reader = command.ExecuteReader())
                        {
                            var resultSet = 0;
                            do
                            {
                                if (reader.FieldCount == 0) continue;
                                resultSet++;
                                if (resultSet > 1) output.AppendLine();
                                AppendResultSet(output, reader, resultSet, maxRows);
                            }
                            while (output.Length < MaxOutputLength && reader.NextResult());

                            if (resultSet == 0) output.AppendLine("(no result sets)");
                        }
                    }
                }
                finally
                {
                    transaction.Rollback();
                }
            }

            if (output.Length > MaxOutputLength)
            {
                output.Length = MaxOutputLength;
                output.AppendLine().AppendLine("... output truncated");
            }
            return output.ToString();
        }

        internal static void AppendResultSet(StringBuilder output, IDataReader reader, int number, int maxRows)
        {
            var columns = Enumerable.Range(0, reader.FieldCount)
                .Select(i => string.IsNullOrEmpty(reader.GetName(i)) ? "(no name)" : reader.GetName(i));

            output.Append("Result set ").Append(number).AppendLine(":");
            output.AppendLine(string.Join("\t", columns));

            var rows = 0;
            var more = false;
            while (reader.Read())
            {
                if (rows == maxRows) { more = true; break; }
                var values = new string[reader.FieldCount];
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    values[i] = FormatValue(reader.IsDBNull(i) ? null : reader.GetValue(i));
                }
                output.AppendLine(string.Join("\t", values));
                rows++;
                if (output.Length > MaxOutputLength) { more = true; break; }
            }

            output.Append('(').Append(rows).Append(rows == 1 ? " row" : " rows");
            output.AppendLine(more ? ", more rows not shown)" : ")");
        }

        internal static string FormatValue(object value)
        {
            string text;
            if (value == null) text = "NULL";
            else if (value is byte[] bytes) text = "0x" + BitConverter.ToString(bytes, 0, Math.Min(bytes.Length, 64)).Replace("-", string.Empty) + (bytes.Length > 64 ? "..." : string.Empty);
            else if (value is DateTime dt) text = dt.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
            else text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;

            text = text.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
            return text.Length > MaxCellLength ? text.Substring(0, MaxCellLength) + "..." : text;
        }
    }
}
