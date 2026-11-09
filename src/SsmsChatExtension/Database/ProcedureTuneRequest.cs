using System.Collections.Generic;
using System.Text;

namespace SsmsChatExtension.Database
{
    /// <summary>What the user picked in the "Tune procedures" dialog, and the chat message that asks Claude to act on it.</summary>
    internal sealed class ProcedureTuneRequest
    {
        public ProcedureTuneRequest(string database, IReadOnlyList<ProcedureInfo> procedures, int runs, bool rollbackEachRun)
        {
            Database = database;
            Procedures = procedures;
            Runs = runs;
            RollbackEachRun = rollbackEachRun;
        }

        public string Database { get; }

        public IReadOnlyList<ProcedureInfo> Procedures { get; }

        public int Runs { get; }

        public bool RollbackEachRun { get; }

        /// <summary>
        /// The message that goes into the chat. The method (what to look for, how to write the copies) is in the system prompt;
        /// this names the procedures and the benchmark settings, and carries what the plan cache already knows about their cost.
        /// </summary>
        public string BuildMessage()
        {
            var text = new StringBuilder();
            text.Append("Tune ").Append(Procedures.Count == 1 ? "this stored procedure" : "these " + Procedures.Count + " stored procedures")
                .Append(" in database ").Append(SqlObjectName.QuoteName(Database)).AppendLine(" for performance and prove the improvement with a benchmark:");
            foreach (var procedure in Procedures)
            {
                text.Append("- ").Append(procedure.Name.Quoted);
                if (procedure.StatsText.Length > 0) text.Append("  (").Append(procedure.StatsText).Append(" since its plan was cached)");
                text.AppendLine();
            }
            text.AppendLine();
            text.AppendLine("Follow the stored procedure tuning workflow: read each one, find what makes it slow, write optimized copies that return exactly the same results, "
                            + "then call create_benchmark_plan once for all of them so the before / after test plan opens in a new query window.");
            text.Append("Benchmark settings: ").Append(Runs).Append(" timed runs and ").Append(BenchmarkPlan.DefaultWarmupRuns).Append(" warm-up runs per procedure; ")
                .Append(RollbackEachRun ? "every run is rolled back in a transaction (rollback_each_run = true)." : "runs are NOT rolled back (rollback_each_run = false).");
            return text.ToString();
        }
    }
}
