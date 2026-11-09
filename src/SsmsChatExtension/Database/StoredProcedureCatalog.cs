using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Globalization;

namespace SsmsChatExtension.Database
{
    /// <summary>A stored procedure of the current database, with what the plan cache knows about its cost.</summary>
    internal sealed class ProcedureInfo
    {
        public ProcedureInfo(SqlObjectName name, bool isEncrypted)
        {
            Name = name;
            IsEncrypted = isEncrypted;
        }

        public SqlObjectName Name { get; }

        /// <summary>WITH ENCRYPTION: the text cannot be read, so it cannot be tuned.</summary>
        public bool IsEncrypted { get; }

        /// <summary>Executions since the plan was cached; null when the statistics are not available.</summary>
        public long? Executions { get; set; }

        public double? TotalElapsedMs { get; set; }

        public double? AvgElapsedMs
        {
            get { return Executions > 0 && TotalElapsedMs.HasValue ? TotalElapsedMs / Executions : null; }
        }

        public double? AvgLogicalReads { get; set; }

        public string DisplayName
        {
            get { return Name.ToString(); }
        }

        /// <summary>"1,204 runs, avg 340 ms, 18,200 reads/run", or an empty string when the procedure is not in the plan cache.</summary>
        public string StatsText
        {
            get
            {
                if (IsEncrypted) return "encrypted";
                if (!(Executions > 0)) return string.Empty;
                return Executions.Value.ToString("N0", CultureInfo.InvariantCulture) + " runs, avg "
                       + FormatMs(AvgElapsedMs ?? 0) + ", " + (AvgLogicalReads ?? 0).ToString("N0", CultureInfo.InvariantCulture) + " reads/run";
            }
        }

        private static string FormatMs(double ms)
        {
            return ms >= 1000 ? (ms / 1000).ToString("0.##", CultureInfo.InvariantCulture) + " s" : ms.ToString("0.#", CultureInfo.InvariantCulture) + " ms";
        }
    }

    /// <summary>The procedures of one database, and whether the plan cache statistics could be read for them.</summary>
    internal sealed class ProcedureListing
    {
        public ProcedureListing(IReadOnlyList<ProcedureInfo> procedures, bool hasStatistics)
        {
            Procedures = procedures;
            HasStatistics = hasStatistics;
        }

        public IReadOnlyList<ProcedureInfo> Procedures { get; }

        public bool HasStatistics { get; }
    }

    /// <summary>Lists the T-SQL stored procedures of a database.</summary>
    internal static class StoredProcedureCatalog
    {
        // Totals over every cached plan of the procedure. The DMV needs VIEW SERVER STATE (VIEW SERVER PERFORMANCE STATE
        // on SQL Server 2022), so the list falls back to the plain catalog when the login lacks it.
        private const string WithStatistics =
            "SELECT s.name, p.name, CAST(CASE WHEN m.definition IS NULL THEN 1 ELSE 0 END AS bit), " +
            "  st.execution_count, st.total_elapsed_time, st.total_logical_reads " +
            "FROM sys.procedures p " +
            "JOIN sys.schemas s ON s.schema_id = p.schema_id " +
            "JOIN sys.sql_modules m ON m.object_id = p.object_id " +
            "OUTER APPLY (SELECT SUM(ps.execution_count) AS execution_count, SUM(ps.total_elapsed_time) AS total_elapsed_time, " +
            "                    SUM(ps.total_logical_reads) AS total_logical_reads " +
            "             FROM sys.dm_exec_procedure_stats ps WHERE ps.object_id = p.object_id AND ps.database_id = DB_ID()) st " +
            "WHERE p.is_ms_shipped = 0 AND p.type = 'P' " +
            "ORDER BY s.name, p.name";

        private const string WithoutStatistics =
            "SELECT s.name, p.name, CAST(CASE WHEN m.definition IS NULL THEN 1 ELSE 0 END AS bit), " +
            "  CAST(NULL AS bigint), CAST(NULL AS bigint), CAST(NULL AS bigint) " +
            "FROM sys.procedures p " +
            "JOIN sys.schemas s ON s.schema_id = p.schema_id " +
            "JOIN sys.sql_modules m ON m.object_id = p.object_id " +
            "WHERE p.is_ms_shipped = 0 AND p.type = 'P' " +
            "ORDER BY s.name, p.name";

        /// <summary>Opens a connection and lists the procedures of <paramref name="database"/>, ordered by schema and name.</summary>
        public static ProcedureListing List(Func<IDbConnection> factory, string database)
        {
            using (var connection = factory())
            {
                if (connection.State != ConnectionState.Open) connection.Open();
                connection.ChangeDatabase(database);

                bool hasStatistics;
                var procedures = List(connection, out hasStatistics);
                return new ProcedureListing(procedures, hasStatistics);
            }
        }

        /// <summary>The procedures of the connection's current database, ordered by schema and name.</summary>
        private static IReadOnlyList<ProcedureInfo> List(IDbConnection connection, out bool hasStatistics)
        {
            try
            {
                var withStatistics = Read(connection, WithStatistics);
                hasStatistics = true;
                return withStatistics;
            }
            catch (DbException)
            {
                hasStatistics = false;
                return Read(connection, WithoutStatistics);
            }
        }

        private static List<ProcedureInfo> Read(IDbConnection connection, string sql)
        {
            var result = new List<ProcedureInfo>();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = sql; // nosemgrep: csharp.lang.security.sqli.csharp-sqli.csharp-sqli - only ever one of the two constant catalog queries above
                command.CommandTimeout = 60;
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var info = new ProcedureInfo(SqlObjectName.FromParts(reader.GetString(0), reader.GetString(1)), reader.GetBoolean(2));
                        if (!reader.IsDBNull(3) && reader.GetInt64(3) > 0)
                        {
                            info.Executions = reader.GetInt64(3);
                            info.TotalElapsedMs = reader.GetInt64(4) / 1000.0;
                            info.AvgLogicalReads = reader.GetInt64(5) / (double)info.Executions.Value;
                        }
                        result.Add(info);
                    }
                }
            }
            return result;
        }
    }
}
