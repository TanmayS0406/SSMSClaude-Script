using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace SsmsChatExtension.Database
{
    /// <summary>One stored procedure to benchmark: the original, its optimized copy and how to call both.</summary>
    internal sealed class BenchmarkProcedure
    {
        /// <summary>The original procedure, as schema.name.</summary>
        public string Name { get; set; }

        /// <summary>Name of the optimized copy. Default: the original name plus _Optimized.</summary>
        public string OptimizedName { get; set; }

        /// <summary>CREATE [OR ALTER] PROCEDURE statement of the optimized copy (one batch, no GO).</summary>
        public string OptimizedDefinition { get; set; }

        /// <summary>The argument list of the EXEC, for example @CustomerId = 42, @From = '2025-01-01'.</summary>
        public string Arguments { get; set; }

        /// <summary>What was changed and why; printed in the script header.</summary>
        public string Changes { get; set; }

        /// <summary>True when the procedure writes data (the plan then warns if runs are not rolled back).</summary>
        public bool ModifiesData { get; set; }

        /// <summary>True when a procedure with the optimized name already exists; running the plan replaces it.</summary>
        public bool OptimizedExists { get; set; }
    }

    /// <summary>A change that both procedures depend on, such as an index, applied between the BEFORE and AFTER runs.</summary>
    internal sealed class BenchmarkChange
    {
        public string Description { get; set; }

        public string ApplySql { get; set; }

        public string UndoSql { get; set; }
    }

    internal sealed class BenchmarkRequest
    {
        public string Title { get; set; }

        public string Database { get; set; }

        public int Runs { get; set; } = BenchmarkPlan.DefaultRuns;

        public int WarmupRuns { get; set; } = BenchmarkPlan.DefaultWarmupRuns;

        public bool RollbackEachRun { get; set; } = true;

        public IList<BenchmarkProcedure> Procedures { get; set; } = new List<BenchmarkProcedure>();

        public IList<BenchmarkChange> SupportingChanges { get; set; } = new List<BenchmarkChange>();
    }

    /// <summary>
    /// Builds the before / after benchmark script that is opened in a query window. The measuring and the statistics are
    /// fixed T-SQL (so the numbers mean the same thing every time); only the procedures, the arguments and the optimized
    /// definitions come from the request. Nothing here runs against the database.
    /// </summary>
    internal static class BenchmarkPlan
    {
        public const int DefaultRuns = 10;
        public const int DefaultWarmupRuns = 2;
        public const int MinRuns = 3;
        public const int MaxRuns = 200;
        public const int MaxWarmupRuns = 10;
        public const int MaxProcedures = 10;
        public const string OptimizedSuffix = "_Optimized";

        private static readonly Regex GoLine = new Regex(@"^[ \t]*GO(?:[ \t]+\d+)?[ \t]*(?:--[^\r\n]*)?\r?$",
            RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex CreateHeader = new Regex(@"\G(?<create>CREATE)(?:\s+OR\s+ALTER)?\s+PROC(?:EDURE)?\s+",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex ForbiddenArguments = new Regex(@"--|/\*|\*/|;", RegexOptions.Compiled);

        /// <summary>Checks the request and returns the script. Throws ArgumentException with a message that says what to fix.</summary>
        public static string Build(BenchmarkRequest request, DateTime generatedAt)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (string.IsNullOrWhiteSpace(request.Database)) throw new ArgumentException("The database name is required.");
            if (request.Procedures == null || request.Procedures.Count == 0) throw new ArgumentException("Give at least one procedure to benchmark.");
            if (request.Procedures.Count > MaxProcedures) throw new ArgumentException("At most " + MaxProcedures + " procedures per plan; split them over several plans.");
            if (request.Runs < MinRuns || request.Runs > MaxRuns) throw new ArgumentException("runs must be between " + MinRuns + " and " + MaxRuns + ".");
            if (request.WarmupRuns < 0 || request.WarmupRuns > MaxWarmupRuns) throw new ArgumentException("warmup_runs must be between 0 and " + MaxWarmupRuns + ".");

            var cases = request.Procedures.Select((p, i) => Prepare(p, i + 1)).ToList();
            var duplicates = cases.GroupBy(c => c.Original.ToString(), StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
            if (duplicates != null) throw new ArgumentException(duplicates.Key + " is listed twice.");

            var changes = (request.SupportingChanges ?? new List<BenchmarkChange>())
                .Where(c => !string.IsNullOrWhiteSpace(c?.ApplySql))
                .ToList();

            var database = request.Database.Trim().Trim('[', ']');
            var script = new StringBuilder();
            script.Append(Header(request, database, cases, changes, generatedAt));
            script.AppendLine("USE " + SqlObjectName.QuoteName(database) + ";");
            script.AppendLine("GO");
            script.AppendLine();
            script.Append(Setup(request, database, cases));
            script.AppendLine("GO");
            script.AppendLine();
            script.Append(RunnerProcedure);
            script.AppendLine("GO");
            script.AppendLine();
            script.Append(VerifyProcedure);
            script.AppendLine("GO");
            script.AppendLine();
            if (changes.Count == 0)
            {
                // Nothing has to happen between the two measurements, so the versions alternate run by run and drift cancels out.
                script.Append(Apply(cases, changes));
                script.Append(Phase("MEASURE - the original and its optimized copy, alternating run by run", "EXEC #BenchRun @mode = 'Interleaved';"));
            }
            else
            {
                // A supporting change (an index) must sit between the two measurements, so they run one after the other.
                script.Append(Phase("BEFORE - the original procedures", "EXEC #BenchRun @mode = 'Before';"));
                script.AppendLine("GO");
                script.AppendLine();
                script.Append(Apply(cases, changes));
                script.Append(Phase("AFTER - the optimized copies", "EXEC #BenchRun @mode = 'After';"));
            }
            script.AppendLine("GO");
            script.AppendLine();
            script.Append(Phase("CHECK - do both versions return the same rows?", "EXEC #BenchVerify;"));
            script.AppendLine("GO");
            script.AppendLine();
            script.Append(Report);
            script.AppendLine("GO");
            script.AppendLine();
            script.Append(Cleanup(cases, changes));

            // One line ending throughout: the templates below use the source file's, AppendLine uses the machine's, and SSMS asks to normalise a mix on save.
            return script.ToString().Replace("\r\n", "\n").Replace("\n", Environment.NewLine);
        }

        // ---- validation ----

        private sealed class Case
        {
            public int Id;
            public SqlObjectName Original;
            public SqlObjectName Optimized;
            public string Definition;
            public string Arguments;
            public string Changes;
            public bool ModifiesData;
            public bool OptimizedExists;
        }

        private static Case Prepare(BenchmarkProcedure procedure, int id)
        {
            if (procedure == null) throw new ArgumentException("A procedure entry is empty.");

            var original = SqlObjectName.Parse(procedure.Name);
            var optimized = string.IsNullOrWhiteSpace(procedure.OptimizedName)
                ? SqlObjectName.FromParts(original.Schema, original.Name + OptimizedSuffix)
                : SqlObjectName.Parse(procedure.OptimizedName);
            if (original.SameAs(optimized))
            {
                throw new ArgumentException(original + ": the optimized copy needs its own name (for example " + original.Name + "_Optimized); the original is never modified.");
            }
            // The plan runs CREATE OR ALTER on this name, so it must be recognisably a copy and never one of the user's own procedures.
            if (!optimized.Name.EndsWith(OptimizedSuffix, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(original + ": the optimized copy must be named *" + OptimizedSuffix + " (for example " + original.Name + OptimizedSuffix + "), so the plan can never overwrite another procedure.");
            }

            var arguments = Regex.Replace(procedure.Arguments ?? string.Empty, @"\s+", " ").Trim();
            if (ForbiddenArguments.IsMatch(arguments))
            {
                throw new ArgumentException(original + ": call_arguments may only be the argument list (for example @Id = 42), without comments or semicolons.");
            }

            return new Case
            {
                Id = id,
                Original = original,
                Optimized = optimized,
                Definition = NormalizeDefinition(original, optimized, procedure.OptimizedDefinition),
                Arguments = arguments,
                Changes = procedure.Changes,
                ModifiesData = procedure.ModifiesData,
                OptimizedExists = procedure.OptimizedExists
            };
        }

        /// <summary>
        /// Checks that the definition creates exactly the optimized procedure and nothing else, and rewrites its header to
        /// CREATE OR ALTER PROCEDURE [schema].[name], so running the plan twice works.
        /// </summary>
        private static string NormalizeDefinition(SqlObjectName original, SqlObjectName optimized, string definition)
        {
            if (string.IsNullOrWhiteSpace(definition))
            {
                throw new ArgumentException(original + ": optimized_definition is required (a CREATE OR ALTER PROCEDURE statement for " + optimized + ").");
            }
            if (GoLine.IsMatch(definition))
            {
                throw new ArgumentException(original + ": optimized_definition must be one batch; remove the GO lines.");
            }

            var start = SkipCommentsAndWhitespace(definition);
            var header = CreateHeader.Match(definition, start);
            if (!header.Success)
            {
                throw new ArgumentException(original + ": optimized_definition must start with CREATE OR ALTER PROCEDURE " + optimized + ".");
            }

            var position = header.Index + header.Length;
            SqlObjectName declared;
            string problem;
            if (!SqlObjectName.TryRead(definition, ref position, out declared, out problem))
            {
                throw new ArgumentException(original + ": " + problem);
            }
            if (!declared.SameAs(optimized))
            {
                throw new ArgumentException(original + ": optimized_definition creates " + declared + " but optimized_name is " + optimized + ".");
            }

            return definition.Substring(0, header.Index).TrimEnd() + (header.Index > 0 ? Environment.NewLine : string.Empty)
                   + "CREATE OR ALTER PROCEDURE " + optimized.Quoted + definition.Substring(position).TrimEnd();
        }

        private static int SkipCommentsAndWhitespace(string text)
        {
            var i = 0;
            while (i < text.Length)
            {
                if (char.IsWhiteSpace(text[i])) { i++; continue; }
                if (text[i] == '-' && i + 1 < text.Length && text[i + 1] == '-')
                {
                    while (i < text.Length && text[i] != '\n') i++;
                    continue;
                }
                if (text[i] == '/' && i + 1 < text.Length && text[i + 1] == '*')
                {
                    var end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    if (end < 0) return text.Length;
                    i = end + 2;
                    continue;
                }
                break;
            }
            return i;
        }

        // ---- script sections ----

        private static string Header(BenchmarkRequest request, string database, List<Case> cases, List<BenchmarkChange> changes, DateTime generatedAt)
        {
            var text = new StringBuilder();
            text.AppendLine("/*==============================================================================");
            text.AppendLine("  " + Comment(string.IsNullOrWhiteSpace(request.Title) ? "Stored procedure benchmark: before and after" : request.Title.Trim()));
            text.AppendLine("  Generated by SSMS Claude Chat on " + generatedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " for database " + Comment(SqlObjectName.QuoteName(database)));
            text.AppendLine();
            text.AppendLine("  WHAT THIS SCRIPT DOES");
            if (changes.Count == 0)
            {
                text.AppendLine("    1. APPLY    Creates the optimized copies (<name>_Optimized). The original procedures are NOT modified.");
                text.AppendLine("    2. MEASURE  Runs every original procedure and its optimized copy " + request.WarmupRuns + " warm-up + " + request.Runs + " timed times,");
                text.AppendLine("                alternating run by run (original first, then optimized first, ...), so slow drift of the");
                text.AppendLine("                machine hits both versions equally. Every timed run records elapsed time, CPU time");
                text.AppendLine("                and logical reads.");
                text.AppendLine("    3. CHECK    Runs every pair once more and compares the rows they return.");
                text.AppendLine("    4. REPORT   Result grids: before vs after per procedure, overall total, statistics, errors.");
            }
            else
            {
                text.AppendLine("    1. BEFORE   Runs each original procedure " + request.WarmupRuns + " warm-up + " + request.Runs + " timed times. Every timed run records");
                text.AppendLine("                elapsed time, CPU time and logical reads.");
                text.AppendLine("    2. APPLY    Creates the optimized copies (<name>_Optimized) and the supporting changes listed");
                text.AppendLine("                below. The original procedures are NOT modified.");
                text.AppendLine("    3. AFTER    Runs the optimized copies the same way.");
                text.AppendLine("    4. CHECK    Runs every pair once more and compares the rows they return.");
                text.AppendLine("    5. REPORT   Result grids: before vs after per procedure, overall total, statistics, errors.");
            }
            text.AppendLine();
            text.AppendLine("  BEFORE YOU PRESS EXECUTE");
            text.AppendLine("    * This script RUNS the procedures under TEST CASES with the arguments shown. Read them first, and");
            text.AppendLine("      prefer a test copy of the database for procedures that change data.");
            text.AppendLine("    * rollback_each_run = 1 (the default, set under TEST CASES AND SETTINGS) wraps every run in BEGIN TRAN ... ROLLBACK, so");
            text.AppendLine("      data changes are undone. Locks and log writes still happen, and effects outside the database");
            text.AppendLine("      (mail, files, linked servers, sequence values) are NOT undone.");
            text.AppendLine("    * Switch on Query > Query Options > Results > Grid > \"Discard results after execution\" first,");
            text.AppendLine("      otherwise the grids the procedures return slow the runs down and distort the timings.");
            text.AppendLine("    * Needs SQL Server 2016 SP1 or later. Do not run it on a busy production server: the");
            text.AppendLine("      timings are only meaningful when nothing else is competing for the machine.");
            text.AppendLine("    * Running it a second time? Run the CLEANUP block at the end first. Otherwise the BEFORE numbers");
            text.AppendLine("      already include the supporting changes of the previous run.");
            text.AppendLine();
            text.AppendLine("  HOW TO READ THE REPORT");
            text.AppendLine("    mean   = sum of the timed runs / n            sd = sample standard deviation");
            text.AppendLine("    Time saved %  = (mean_before - mean_after) / mean_before * 100");
            text.AppendLine("    Speed-up (x)  = mean_before / mean_after");
            text.AppendLine("    Reads saved % = same formula on logical reads (they barely vary between runs)");
            text.AppendLine("    SE  = sqrt( sd_before^2 / n_before + sd_after^2 / n_after )");
            text.AppendLine("    t   = (mean_before - mean_after) / SE                       (Welch's t statistic)");
            text.AppendLine("    95% CI of the time saved = (mean_before - mean_after) +/- 1.96 * SE");
            text.AppendLine("    |t| >= 2, or a CI that does not contain 0, means the difference is very unlikely to be noise.");
            text.AppendLine("    Verdict: FASTER / SLOWER needs |t| >= 2 AND at least a 5% change in mean time; a smaller change is reported");
            text.AppendLine("    as NO MEANINGFUL DIFFERENCE. A procedure whose results differ is always flagged DO NOT USE.");
            if (changes.Count > 0)
            {
                text.AppendLine("    With supporting changes, BEFORE and AFTER run one after the other, so a small systematic bias between the");
                text.AppendLine("    two phases is possible (and the supporting change also affects every other procedure on the same tables).");
            }
            text.AppendLine("    Elapsed and CPU time come from the server clock (about 1 ms resolution on many machines). The mean over many");
            text.AppendLine("    runs averages that out; for procedures that finish in under a millisecond raise the number of timed runs");
            text.AppendLine("    (50 or more) and weigh the logical reads, which are exact.");
            text.AppendLine();
            text.AppendLine("  TEST CASES");
            foreach (var c in cases)
            {
                text.AppendLine("    " + c.Id + ". " + Comment(c.Original.Quoted) + "  ->  " + Comment(c.Optimized.Quoted));
                text.AppendLine("       EXEC " + Comment(c.Original.Quoted) + (c.Arguments.Length > 0 ? " " + Comment(c.Arguments) : string.Empty)
                                + (c.ModifiesData ? "      -- changes data" : string.Empty));
                if (c.OptimizedExists)
                {
                    text.AppendLine("       NOTE: " + Comment(c.Optimized.Quoted) + " already exists; this script replaces it.");
                }
                if (!string.IsNullOrWhiteSpace(c.Changes))
                {
                    foreach (var line in Comment(c.Changes).Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        text.AppendLine("       - " + line.Trim());
                    }
                }
            }
            if (changes.Count > 0)
            {
                text.AppendLine();
                text.AppendLine("  SUPPORTING CHANGES (applied between BEFORE and AFTER; undo statements are in the CLEANUP block at the end)");
                for (var i = 0; i < changes.Count; i++)
                {
                    text.AppendLine("    " + (i + 1) + ". " + Comment(Regex.Replace(changes[i].Description ?? "(no description)", @"\s+", " ").Trim()));
                }
            }
            text.AppendLine("==============================================================================*/");
            text.AppendLine();
            return text.ToString();
        }

        private static string Setup(BenchmarkRequest request, string database, List<Case> cases)
        {
            var rows = cases.Select(c => "    (" + c.Id + ", "
                + Literal(c.Original.Quoted) + ", "
                + Literal(SqlObjectName.QuoteName(database) + "." + c.Original.Quoted) + ", "
                + Literal(SqlObjectName.QuoteName(database) + "." + c.Optimized.Quoted) + ", "
                + (c.Arguments.Length > 0 ? Literal(c.Arguments) : "NULL") + ", "
                + (c.ModifiesData ? "1" : "0") + ")");

            return SetupTemplate
                .Replace("<<RUNS>>", request.Runs.ToString(CultureInfo.InvariantCulture))
                .Replace("<<WARMUP>>", request.WarmupRuns.ToString(CultureInfo.InvariantCulture))
                .Replace("<<ROLLBACK>>", request.RollbackEachRun ? "1" : "0")
                .Replace("<<CASES>>", string.Join("," + Environment.NewLine, rows));
        }

        private static string Apply(List<Case> cases, List<BenchmarkChange> changes)
        {
            var text = new StringBuilder();
            text.AppendLine("-- =============================================================================");
            text.AppendLine("-- APPLY - creates the optimized copies; the original procedures are not touched");
            text.AppendLine("-- =============================================================================");
            text.AppendLine("PRINT N'APPLY: creating the optimized copies...';");
            text.AppendLine("GO");
            for (var i = 0; i < changes.Count; i++)
            {
                text.AppendLine();
                text.AppendLine("-- Supporting change " + (i + 1) + ": " + Regex.Replace(Comment(changes[i].Description ?? string.Empty), @"\s+", " ").Trim());
                text.AppendLine(changes[i].ApplySql.Trim());
                text.AppendLine("GO");
            }
            foreach (var c in cases)
            {
                text.AppendLine();
                text.AppendLine("-- Optimized copy of " + c.Original.Quoted);
                text.AppendLine(c.Definition);
                text.AppendLine("GO");
            }
            text.AppendLine();
            return text.ToString();
        }

        private static string Phase(string title, string statement)
        {
            return "-- =============================================================================" + Environment.NewLine
                 + "-- " + title + Environment.NewLine
                 + "-- =============================================================================" + Environment.NewLine
                 + "PRINT N'" + title.Replace("'", "''") + "...';" + Environment.NewLine
                 + statement + Environment.NewLine;
        }

        private static string Cleanup(List<Case> cases, List<BenchmarkChange> changes)
        {
            var text = new StringBuilder();
            text.AppendLine("/*==============================================================================");
            text.AppendLine("  CLEANUP (optional) - remove what this plan created. Select the lines below and run them,");
            text.AppendLine("  or keep the optimized copies and adopt them with CREATE OR ALTER on the original name.");
            text.AppendLine();
            foreach (var c in cases)
            {
                text.AppendLine("  DROP PROCEDURE IF EXISTS " + Comment(c.Optimized.Quoted) + ";");
            }
            foreach (var change in changes.Where(c => !string.IsNullOrWhiteSpace(c.UndoSql)))
            {
                text.AppendLine();
                text.AppendLine("  -- undo: " + Regex.Replace(Comment(change.Description ?? string.Empty), @"\s+", " ").Trim());
                foreach (var line in Comment(change.UndoSql).Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None))
                {
                    text.AppendLine("  " + line.TrimEnd());
                }
            }
            text.AppendLine("==============================================================================*/");
            return text.ToString();
        }

        /// <summary>Text that goes inside a /* */ comment: a stray comment terminator in it must not end the comment early.</summary>
        private static string Comment(string text)
        {
            return (text ?? string.Empty).Replace("*/", "* /").Replace("/*", "/ *");
        }

        private static string Literal(string text)
        {
            return "N'" + text.Replace("'", "''") + "'";
        }

        // ---- fixed T-SQL ----

        private const string SetupTemplate = @"-- =============================================================================
-- TEST CASES AND SETTINGS
-- =============================================================================
SET NOCOUNT ON;

IF OBJECT_ID(N'tempdb..#BenchConfig') IS NOT NULL DROP TABLE #BenchConfig;
IF OBJECT_ID(N'tempdb..#BenchCases')  IS NOT NULL DROP TABLE #BenchCases;
IF OBJECT_ID(N'tempdb..#BenchRuns')   IS NOT NULL DROP TABLE #BenchRuns;
IF OBJECT_ID(N'tempdb..#BenchCheck')  IS NOT NULL DROP TABLE #BenchCheck;
IF OBJECT_ID(N'tempdb..#BenchStats')  IS NOT NULL DROP TABLE #BenchStats;
IF OBJECT_ID(N'tempdb..#BenchMedian') IS NOT NULL DROP TABLE #BenchMedian;

CREATE TABLE #BenchConfig
(
    runs              int NOT NULL,   -- timed runs per procedure and variant
    warmup_runs       int NOT NULL,   -- runs before the timed ones (compile the plan, warm the cache); not reported
    rollback_each_run bit NOT NULL,   -- 1 = wrap every run in BEGIN TRAN ... ROLLBACK
    cold_cache        bit NOT NULL    -- 1 = CHECKPOINT + DBCC DROPCLEANBUFFERS before every timed run (test servers only, needs sysadmin)
);
INSERT #BenchConfig (runs, warmup_runs, rollback_each_run, cold_cache) VALUES (<<RUNS>>, <<WARMUP>>, <<ROLLBACK>>, 0);

CREATE TABLE #BenchCases
(
    case_id             int            NOT NULL PRIMARY KEY,
    display_name        nvarchar(400)  NOT NULL,
    original_call_name  nvarchar(600)  NOT NULL,
    optimized_call_name nvarchar(600)  NOT NULL,
    arguments           nvarchar(max)  NULL,
    modifies_data       bit            NOT NULL
);
INSERT #BenchCases (case_id, display_name, original_call_name, optimized_call_name, arguments, modifies_data) VALUES
<<CASES>>;

CREATE TABLE #BenchRuns
(
    case_id        int           NOT NULL,
    variant        varchar(6)    NOT NULL,   -- Before / After
    run_no         int           NOT NULL,
    is_warmup      bit           NOT NULL,
    elapsed_ms     float         NULL,
    cpu_ms         float         NULL,
    logical_reads  bigint        NULL,
    physical_reads bigint        NULL,
    error_message  nvarchar(2000) NULL,
    PRIMARY KEY (case_id, variant, run_no)
);

CREATE TABLE #BenchCheck
(
    case_id        int            NOT NULL PRIMARY KEY,
    status         nvarchar(40)   NOT NULL,
    rows_before    int            NULL,
    rows_after     int            NULL,
    differing_rows int            NULL,
    detail         nvarchar(1000) NULL
);

IF EXISTS (SELECT 1 FROM #BenchCases c CROSS JOIN #BenchConfig f WHERE c.modifies_data = 1 AND f.rollback_each_run = 0)
    PRINT N'WARNING: a procedure that changes data will run with rollback_each_run = 0, so its changes stay in the database.';
";

        private const string RunnerProcedure = @"-- =============================================================================
-- The measuring procedures (temporary: they live for this session only)
-- =============================================================================
IF OBJECT_ID(N'tempdb..#BenchOne') IS NOT NULL DROP PROCEDURE #BenchOne;
IF OBJECT_ID(N'tempdb..#BenchRun') IS NOT NULL DROP PROCEDURE #BenchRun;
GO
-- One measured execution of one call: stores a row in #BenchRuns and returns the error, if there was one.
CREATE PROCEDURE #BenchOne @case_id int, @variant varchar(6), @call nvarchar(max), @run int, @warmup int, @rollback bit, @err nvarchar(2000) OUTPUT
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @t0 datetime2(7), @t1 datetime2(7), @cpu0 bigint, @cpu1 bigint, @reads0 bigint, @reads1 bigint, @phys0 bigint, @phys1 bigint;
    SET @err = NULL;

    IF @run > @warmup AND EXISTS (SELECT 1 FROM #BenchConfig WHERE cold_cache = 1)
    BEGIN
        BEGIN TRY
            CHECKPOINT;
            DBCC DROPCLEANBUFFERS WITH NO_INFOMSGS;
        END TRY
        BEGIN CATCH
            UPDATE #BenchConfig SET cold_cache = 0;
            PRINT N'Cold-cache runs need permission to empty the buffer cache (' + ERROR_MESSAGE() + N'); continuing with warm-cache runs.';
        END CATCH;
    END;

    IF @rollback = 1 BEGIN TRANSACTION;

    -- cpu_time, logical_reads and reads of this session's running request grow while the batch runs; the difference is the cost of the call
    SELECT @cpu0 = cpu_time, @reads0 = logical_reads, @phys0 = reads FROM sys.dm_exec_requests WHERE session_id = @@SPID;
    SET @t0 = SYSDATETIME();
    BEGIN TRY
        EXEC sys.sp_executesql @call;
    END TRY
    BEGIN CATCH
        SET @err = LEFT(ERROR_MESSAGE(), 2000);
    END CATCH;
    SET @t1 = SYSDATETIME();
    SELECT @cpu1 = cpu_time, @reads1 = logical_reads, @phys1 = reads FROM sys.dm_exec_requests WHERE session_id = @@SPID;

    IF @rollback = 1 AND @@TRANCOUNT > 0 ROLLBACK TRANSACTION;

    INSERT #BenchRuns (case_id, variant, run_no, is_warmup, elapsed_ms, cpu_ms, logical_reads, physical_reads, error_message)
    VALUES (@case_id, @variant, @run, CASE WHEN @run <= @warmup THEN 1 ELSE 0 END,
            DATEDIFF_BIG(MICROSECOND, @t0, @t1) / 1000.0, @cpu1 - @cpu0, @reads1 - @reads0, @phys1 - @phys0, @err);
END;
GO
-- The loop. Before / After: one version after the other. Interleaved: both versions in every run, in alternating order (ABBA).
CREATE PROCEDURE #BenchRun @mode varchar(11)
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @runs int, @warmup int, @rollback bit;
    SELECT @runs = runs, @warmup = warmup_runs, @rollback = rollback_each_run FROM #BenchConfig;

    DECLARE @case_id int, @before nvarchar(max), @after nvarchar(max), @run int, @err_before nvarchar(2000), @err_after nvarchar(2000);

    DECLARE bench_cases CURSOR LOCAL FAST_FORWARD FOR
        SELECT case_id,
               N'EXEC ' + original_call_name  + ISNULL(N' ' + arguments, N''),
               N'EXEC ' + optimized_call_name + ISNULL(N' ' + arguments, N'')
        FROM #BenchCases
        ORDER BY case_id;
    OPEN bench_cases;
    FETCH NEXT FROM bench_cases INTO @case_id, @before, @after;

    WHILE @@FETCH_STATUS = 0
    BEGIN
        SET @run = 1;
        WHILE @run <= @warmup + @runs
        BEGIN
            SELECT @err_before = NULL, @err_after = NULL;

            IF @mode = 'Before'
            BEGIN
                EXEC #BenchOne @case_id, 'Before', @before, @run, @warmup, @rollback, @err_before OUTPUT;
            END
            ELSE IF @mode = 'After'
            BEGIN
                EXEC #BenchOne @case_id, 'After', @after, @run, @warmup, @rollback, @err_after OUTPUT;
            END
            ELSE IF @run % 2 = 1
            BEGIN
                EXEC #BenchOne @case_id, 'Before', @before, @run, @warmup, @rollback, @err_before OUTPUT;
                EXEC #BenchOne @case_id, 'After',  @after,  @run, @warmup, @rollback, @err_after  OUTPUT;
            END
            ELSE
            BEGIN
                EXEC #BenchOne @case_id, 'After',  @after,  @run, @warmup, @rollback, @err_after  OUTPUT;
                EXEC #BenchOne @case_id, 'Before', @before, @run, @warmup, @rollback, @err_before OUTPUT;
            END;

            IF @err_before IS NOT NULL OR @err_after IS NOT NULL BREAK;   -- a failing procedure fails every time: record it once and move on
            SET @run += 1;
        END;

        FETCH NEXT FROM bench_cases INTO @case_id, @before, @after;
    END;

    CLOSE bench_cases;
    DEALLOCATE bench_cases;
END;
";

        private const string VerifyProcedure = @"-- =============================================================================
-- The result comparison (temporary procedure): same rows, same order?
-- =============================================================================
IF OBJECT_ID(N'tempdb..#BenchVerify') IS NOT NULL DROP PROCEDURE #BenchVerify;
GO
CREATE PROCEDURE #BenchVerify
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @rollback bit;
    SELECT @rollback = rollback_each_run FROM #BenchConfig;

    DECLARE @case_id int, @before nvarchar(max), @after nvarchar(max), @problem nvarchar(1000), @has_result bit,
            @columns nvarchar(max), @names nvarchar(max), @a_names nvarchar(max), @b_names nvarchar(max), @sql nvarchar(max),
            @rows_a int, @rows_b int, @diff int, @order_diff int, @status nvarchar(40), @detail nvarchar(1000);

    DECLARE bench_pairs CURSOR LOCAL FAST_FORWARD FOR
        SELECT case_id,
               N'EXEC ' + original_call_name  + ISNULL(N' ' + arguments, N''),
               N'EXEC ' + optimized_call_name + ISNULL(N' ' + arguments, N'')
        FROM #BenchCases
        ORDER BY case_id;
    OPEN bench_pairs;
    FETCH NEXT FROM bench_pairs INTO @case_id, @before, @after;

    WHILE @@FETCH_STATUS = 0
    BEGIN
        SELECT @status = NULL, @detail = NULL, @problem = NULL, @has_result = 0, @columns = NULL, @names = NULL, @a_names = NULL, @b_names = NULL,
               @rows_a = NULL, @rows_b = NULL, @diff = NULL, @order_diff = NULL;

        -- the shape of the first result set of the original procedure
        SELECT TOP (1) @problem = CASE WHEN error_message IS NOT NULL THEN error_message
                                       WHEN name IS NULL THEN N'a result column has no name, so the rows cannot be compared'
                                       ELSE N'a result column has a type that cannot be compared' END
        FROM sys.dm_exec_describe_first_result_set(@before, NULL, 0)
        WHERE ISNULL(is_hidden, 0) = 0 AND (error_message IS NOT NULL OR name IS NULL OR system_type_name IS NULL);

        IF @problem IS NULL AND EXISTS (SELECT 1 FROM sys.dm_exec_describe_first_result_set(@before, NULL, 0) WHERE ISNULL(is_hidden, 0) = 0)
            SET @has_result = 1;

        IF @problem IS NOT NULL
        BEGIN
            SELECT @status = N'NOT CHECKED', @detail = LEFT(@problem, 1000);
        END
        ELSE IF @has_result = 0
        BEGIN
            SELECT @status = N'NO RESULT SET', @detail = N'The procedure returns no result set, so there are no rows to compare.';
        END
        ELSE
        BEGIN
            SELECT @columns = STUFF((SELECT N', ' + QUOTENAME(name) + N' ' + system_type_name + N' NULL'
                                     FROM sys.dm_exec_describe_first_result_set(@before, NULL, 0)
                                     WHERE ISNULL(is_hidden, 0) = 0 ORDER BY column_ordinal
                                     FOR XML PATH(N''), TYPE).value(N'.', N'nvarchar(max)'), 1, 2, N''),
                   @names   = STUFF((SELECT N', ' + QUOTENAME(name)
                                     FROM sys.dm_exec_describe_first_result_set(@before, NULL, 0)
                                     WHERE ISNULL(is_hidden, 0) = 0 ORDER BY column_ordinal
                                     FOR XML PATH(N''), TYPE).value(N'.', N'nvarchar(max)'), 1, 2, N''),
                   @a_names = STUFF((SELECT N', a.' + QUOTENAME(name)
                                     FROM sys.dm_exec_describe_first_result_set(@before, NULL, 0)
                                     WHERE ISNULL(is_hidden, 0) = 0 ORDER BY column_ordinal
                                     FOR XML PATH(N''), TYPE).value(N'.', N'nvarchar(max)'), 1, 2, N''),
                   @b_names = STUFF((SELECT N', b.' + QUOTENAME(name)
                                     FROM sys.dm_exec_describe_first_result_set(@before, NULL, 0)
                                     WHERE ISNULL(is_hidden, 0) = 0 ORDER BY column_ordinal
                                     FOR XML PATH(N''), TYPE).value(N'.', N'nvarchar(max)'), 1, 2, N'');

            SET @sql = N'
DECLARE @A TABLE (__seq int IDENTITY(1,1), ' + @columns + N');
DECLARE @B TABLE (__seq int IDENTITY(1,1), ' + @columns + N');
' + CASE WHEN @rollback = 1 THEN N'BEGIN TRANSACTION;' ELSE N'' END + N'
INSERT @A (' + @names + N') ' + @before + N';
' + CASE WHEN @rollback = 1 THEN N'ROLLBACK TRANSACTION;
BEGIN TRANSACTION;' ELSE N'' END + N'
INSERT @B (' + @names + N') ' + @after + N';
' + CASE WHEN @rollback = 1 THEN N'ROLLBACK TRANSACTION;' ELSE N'' END + N'
SELECT @rows_a = COUNT(*) FROM @A;
SELECT @rows_b = COUNT(*) FROM @B;
WITH ga AS (SELECT ' + @names + N', COUNT_BIG(*) AS __n FROM @A GROUP BY ' + @names + N'),
     gb AS (SELECT ' + @names + N', COUNT_BIG(*) AS __n FROM @B GROUP BY ' + @names + N')
SELECT @diff = COUNT(*) FROM (
    (SELECT * FROM ga EXCEPT SELECT * FROM gb)
    UNION ALL
    (SELECT * FROM gb EXCEPT SELECT * FROM ga)) AS d;
SELECT @order_diff = COUNT(*) FROM @A AS a JOIN @B AS b ON b.__seq = a.__seq
WHERE EXISTS (SELECT ' + @a_names + N' EXCEPT SELECT ' + @b_names + N');';

            BEGIN TRY
                EXEC sys.sp_executesql @sql,
                     N'@rows_a int OUTPUT, @rows_b int OUTPUT, @diff int OUTPUT, @order_diff int OUTPUT',
                     @rows_a OUTPUT, @rows_b OUTPUT, @diff OUTPUT, @order_diff OUTPUT;

                SELECT @status = CASE WHEN @diff > 0 THEN N'DIFFERENT RESULTS'
                                      WHEN @order_diff > 0 THEN N'SAME ROWS, DIFFERENT ORDER'
                                      ELSE N'IDENTICAL' END,
                       @detail = CASE WHEN @diff > 0 THEN N'The versions return different rows. Check float/real columns and anything that depends on the time or on random values.'
                                      WHEN @order_diff > 0 THEN N'Same rows in a different order. Fine when the original has no ORDER BY; otherwise the optimized copy must sort the same way.'
                                      ELSE N'Same rows in the same order.' END;
            END TRY
            BEGIN CATCH
                IF @rollback = 1 AND @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
                SELECT @status = N'NOT CHECKED', @detail = LEFT(ERROR_MESSAGE(), 1000);
            END CATCH;
        END;

        INSERT #BenchCheck (case_id, status, rows_before, rows_after, differing_rows, detail)
        VALUES (@case_id, @status, @rows_a, @rows_b, @diff, @detail);

        FETCH NEXT FROM bench_pairs INTO @case_id, @before, @after;
    END;

    CLOSE bench_pairs;
    DEALLOCATE bench_pairs;
END;
";

        private const string Report = @"-- =============================================================================
-- REPORT
-- =============================================================================
SET NOCOUNT ON;

SELECT r.case_id, r.variant,
       COUNT(*)                              AS n,
       AVG(r.elapsed_ms)                     AS mean_ms,
       ISNULL(STDEV(r.elapsed_ms), 0)        AS sd_ms,
       MIN(r.elapsed_ms)                     AS min_ms,
       MAX(r.elapsed_ms)                     AS max_ms,
       AVG(r.cpu_ms)                         AS mean_cpu_ms,
       AVG(CAST(r.logical_reads AS float))   AS mean_reads,
       AVG(CAST(r.physical_reads AS float))  AS mean_phys_reads
INTO #BenchStats
FROM #BenchRuns AS r
WHERE r.is_warmup = 0 AND r.error_message IS NULL
GROUP BY r.case_id, r.variant;

SELECT DISTINCT case_id, variant,
       PERCENTILE_CONT(0.5) WITHIN GROUP (ORDER BY elapsed_ms) OVER (PARTITION BY case_id, variant) AS median_ms
INTO #BenchMedian
FROM #BenchRuns
WHERE is_warmup = 0 AND error_message IS NULL;

-- RESULT: one row per procedure
SELECT c.case_id                                                        AS [#],
       c.display_name                                                   AS [Procedure],
       b.n                                                              AS [Timed runs],
       CAST(b.mean_ms AS decimal(18,2))                                 AS [Before (ms)],
       CAST(a.mean_ms AS decimal(18,2))                                 AS [After (ms)],
       CAST(100.0 * x.diff / NULLIF(b.mean_ms, 0) AS decimal(9,1))      AS [Time saved %],
       CAST(b.mean_ms / NULLIF(a.mean_ms, 0) AS decimal(9,2))           AS [Speed-up (x)],
       CAST(b.mean_reads AS decimal(18,1))                              AS [Before reads],
       CAST(a.mean_reads AS decimal(18,1))                              AS [After reads],
       CAST(100.0 * (b.mean_reads - a.mean_reads) / NULLIF(b.mean_reads, 0) AS decimal(9,1)) AS [Reads saved %],
       CAST(b.mean_cpu_ms AS decimal(18,2))                             AS [Before CPU (ms)],
       CAST(a.mean_cpu_ms AS decimal(18,2))                             AS [After CPU (ms)],
       CAST(y.t AS decimal(9,2))                                        AS [t],
       CAST(x.diff - 1.96 * x.se AS decimal(18,2))                      AS [95% CI low (ms)],
       CAST(x.diff + 1.96 * x.se AS decimal(18,2))                      AS [95% CI high (ms)],
       CASE WHEN b.n IS NULL OR a.n IS NULL            THEN N'NOT COMPARABLE - a run failed (see ERRORS)'
            WHEN k.status = N'DIFFERENT RESULTS'       THEN N'DO NOT USE - the results differ'
            WHEN x.se = 0 AND x.diff = 0               THEN N'NO DIFFERENCE'
            WHEN (ABS(y.t) >= 2 OR (x.se = 0 AND x.diff <> 0)) AND ABS(100.0 * x.diff / NULLIF(b.mean_ms, 0)) >= 5
                                                       THEN CASE WHEN x.diff > 0 THEN N'FASTER' ELSE N'SLOWER' END
            WHEN ABS(y.t) >= 2 OR (x.se = 0 AND x.diff <> 0)
                                                       THEN N'NO MEANINGFUL DIFFERENCE (under 5%)'
            ELSE N'NO SIGNIFICANT DIFFERENCE' END                       AS [Verdict],
       ISNULL(k.status, N'not checked')                                 AS [Same results?]
FROM #BenchCases AS c
LEFT JOIN #BenchStats AS b ON b.case_id = c.case_id AND b.variant = 'Before'
LEFT JOIN #BenchStats AS a ON a.case_id = c.case_id AND a.variant = 'After'
LEFT JOIN #BenchCheck AS k ON k.case_id = c.case_id
CROSS APPLY (SELECT diff = b.mean_ms - a.mean_ms,
                    se   = SQRT(POWER(b.sd_ms, 2) / NULLIF(b.n, 0) + POWER(a.sd_ms, 2) / NULLIF(a.n, 0))) AS x
CROSS APPLY (SELECT t = x.diff / NULLIF(x.se, 0)) AS y
ORDER BY c.case_id;

-- OVERALL: the cost of running every compared procedure once
SELECT COUNT(*)                                                         AS [Procedures compared],
       CAST(SUM(b.mean_ms) AS decimal(18,2))                            AS [Before total (ms)],
       CAST(SUM(a.mean_ms) AS decimal(18,2))                            AS [After total (ms)],
       CAST(100.0 * (SUM(b.mean_ms) - SUM(a.mean_ms)) / NULLIF(SUM(b.mean_ms), 0) AS decimal(9,1)) AS [Time saved %],
       CAST(SUM(b.mean_ms) / NULLIF(SUM(a.mean_ms), 0) AS decimal(9,2)) AS [Speed-up (x)],
       CAST(SUM(b.mean_reads) AS decimal(18,1))                         AS [Before reads],
       CAST(SUM(a.mean_reads) AS decimal(18,1))                         AS [After reads],
       CAST(100.0 * (SUM(b.mean_reads) - SUM(a.mean_reads)) / NULLIF(SUM(b.mean_reads), 0) AS decimal(9,1)) AS [Reads saved %]
FROM #BenchStats AS b
JOIN #BenchStats AS a ON a.case_id = b.case_id AND a.variant = 'After'
WHERE b.variant = 'Before';

-- STATISTICS: the numbers behind the result
SELECT c.case_id AS [#], c.display_name AS [Procedure], s.variant AS [Variant], s.n AS [n],
       CAST(s.mean_ms AS decimal(18,3))   AS [Mean (ms)],
       CAST(m.median_ms AS decimal(18,3)) AS [Median (ms)],
       CAST(s.sd_ms AS decimal(18,3))     AS [SD (ms)],
       CAST(s.min_ms AS decimal(18,3))    AS [Min (ms)],
       CAST(s.max_ms AS decimal(18,3))    AS [Max (ms)],
       CAST(s.mean_cpu_ms AS decimal(18,2))     AS [Mean CPU (ms)],
       CAST(s.mean_reads AS decimal(18,1))      AS [Mean logical reads],
       CAST(s.mean_phys_reads AS decimal(18,1)) AS [Mean physical reads]
FROM #BenchStats AS s
JOIN #BenchMedian AS m ON m.case_id = s.case_id AND m.variant = s.variant
JOIN #BenchCases AS c ON c.case_id = s.case_id
ORDER BY c.case_id, CASE s.variant WHEN 'Before' THEN 0 ELSE 1 END;

-- RESULT CHECK: does the optimized copy return the same rows as the original?
SELECT c.case_id AS [#], c.display_name AS [Procedure], k.status AS [Status], k.rows_before AS [Rows before], k.rows_after AS [Rows after],
       k.differing_rows AS [Differing row groups], k.detail AS [Detail]
FROM #BenchCheck AS k
JOIN #BenchCases AS c ON c.case_id = k.case_id
ORDER BY c.case_id;

-- ERRORS: only when a run failed
IF EXISTS (SELECT 1 FROM #BenchRuns WHERE error_message IS NOT NULL)
    SELECT c.case_id AS [#], c.display_name AS [Procedure], r.variant AS [Variant], r.run_no AS [Run], r.error_message AS [Error]
    FROM #BenchRuns AS r
    JOIN #BenchCases AS c ON c.case_id = r.case_id
    WHERE r.error_message IS NOT NULL
    ORDER BY c.case_id, r.variant, r.run_no;

-- RAW: every measured run, including the warm-up runs
SELECT c.case_id AS [#], c.display_name AS [Procedure], r.variant AS [Variant], r.run_no AS [Run],
       CASE r.is_warmup WHEN 1 THEN N'warm-up' ELSE N'timed' END AS [Kind],
       CAST(r.elapsed_ms AS decimal(18,3)) AS [Elapsed (ms)], CAST(r.cpu_ms AS decimal(18,0)) AS [CPU (ms)],
       r.logical_reads AS [Logical reads], r.physical_reads AS [Physical reads]
FROM #BenchRuns AS r
JOIN #BenchCases AS c ON c.case_id = r.case_id
ORDER BY c.case_id, r.variant DESC, r.run_no;

PRINT N'Done. Start with the RESULT grid (first one); the formulas are explained at the top of this script.';
";
    }
}
