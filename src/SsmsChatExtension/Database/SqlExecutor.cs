using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace SsmsChatExtension.Database
{
    /// <summary>
    /// Runs SQL that is allowed to change the database (the user approved it, or allowed changes in settings).
    /// Supports GO batch separators like SSMS; each batch is committed as it runs (autocommit), and execution
    /// stops at the first failing batch.
    /// </summary>
    internal static class SqlExecutor
    {
        private const int CommandTimeoutSeconds = 300;

        private static readonly Regex GoLine = new Regex(@"^[ \t]*GO(?:[ \t]+(?<count>\d+))?[ \t]*(?:--[^\r\n]*)?\r?$",
            RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>Splits a script on GO lines. A "GO n" line repeats the preceding batch n times.</summary>
        public static IReadOnlyList<string> SplitBatches(string script)
        {
            var batches = new List<string>();
            var start = 0;
            foreach (Match go in GoLine.Matches(script ?? string.Empty))
            {
                var count = go.Groups["count"].Success ? Math.Max(1, int.Parse(go.Groups["count"].Value)) : 1;
                AddBatch(batches, script.Substring(start, go.Index - start), count);
                start = go.Index + go.Length;
            }
            AddBatch(batches, (script ?? string.Empty).Substring(start), 1);
            return batches;
        }

        /// <summary>Runs every batch and reports result sets and rows affected. Throws if a batch fails.</summary>
        public static string Run(IDbConnection connection, string script, int maxRows)
        {
            var batches = SplitBatches(script);
            if (batches.Count == 0) throw new ArgumentException("The SQL is empty.");

            maxRows = Math.Max(1, Math.Min(maxRows, ReadOnlySql.MaxRowsLimit));
            var output = new StringBuilder();
            var resultSet = 0;

            for (var i = 0; i < batches.Count; i++)
            {
                try
                {
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = batches[i];
                        command.CommandTimeout = CommandTimeoutSeconds;

                        using (var reader = command.ExecuteReader())
                        {
                            do
                            {
                                if (reader.FieldCount == 0) continue;
                                resultSet++;
                                if (output.Length < ReadOnlySql.MaxOutputLength) ReadOnlySql.AppendResultSet(output, reader, resultSet, maxRows);
                                output.AppendLine();
                            }
                            while (reader.NextResult());

                            reader.Close();
                            if (reader.RecordsAffected >= 0)
                            {
                                output.Append(batches.Count > 1 ? "Batch " + (i + 1) + ": " : string.Empty)
                                      .Append(reader.RecordsAffected).AppendLine(" row(s) affected");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    var done = i == 0
                        ? "Nothing was committed."
                        : (i == 1 ? "Batch 1 of " + batches.Count + " already ran and was" : "Batches 1-" + i + " of " + batches.Count + " already ran and were") + " committed.";
                    throw new InvalidOperationException(
                        (batches.Count > 1 ? "Batch " + (i + 1) + " failed: " : string.Empty) + ex.Message + " " + done
                        + (output.Length > 0 ? Environment.NewLine + output : string.Empty), ex);
                }
            }

            output.AppendLine("Completed " + batches.Count + (batches.Count == 1 ? " batch" : " batches") + " successfully (committed).");
            if (output.Length > ReadOnlySql.MaxOutputLength)
            {
                return output.ToString(0, ReadOnlySql.MaxOutputLength) + Environment.NewLine + "... output truncated";
            }
            return output.ToString();
        }

        private static void AddBatch(List<string> batches, string batch, int count)
        {
            if (string.IsNullOrWhiteSpace(batch)) return;
            batches.AddRange(Enumerable.Repeat(batch.Trim(), count));
        }
    }
}
