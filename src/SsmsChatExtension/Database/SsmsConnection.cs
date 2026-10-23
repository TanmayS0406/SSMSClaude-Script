using System;
using System.Collections.Specialized;
using System.Data;
using System.Data.SqlClient;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;

namespace SsmsChatExtension.Database
{
    /// <summary>A database connection Claude can use: where it points and how to open it.</summary>
    internal sealed class ActiveConnection
    {
        public ActiveConnection(string server, string database, string source, Func<IDbConnection> factory)
        {
            Server = server ?? string.Empty;
            Database = database ?? string.Empty;
            Source = source;
            Factory = factory;
        }

        public string Server { get; }

        /// <summary>Initial database of the connection; empty means the login's default database.</summary>
        public string Database { get; }

        /// <summary>Human-readable origin, for example "active query window".</summary>
        public string Source { get; }

        /// <summary>Creates a new, not yet opened, connection.</summary>
        public Func<IDbConnection> Factory { get; }
    }

    /// <summary>
    /// Reads the connection of the active SSMS query window. SSMS has no public API for this, so it goes
    /// through the (stable for many releases) internal ServiceCache.ScriptFactory types via reflection.
    /// The type lives in SqlWorkbench.Interfaces up to SSMS 20 and in Microsoft.SqlServer.SqlTools.VSIntegration in SSMS 21+.
    /// </summary>
    internal static class SsmsConnection
    {
        private const string ServiceCacheType = "Microsoft.SqlServer.Management.UI.VSIntegration.ServiceCache";
        private const string ConnectionUtilType = "Microsoft.SqlServer.Management.UI.ConnectionDlg.UIConnectionInfoUtil";

        /// <summary>
        /// The connection Claude should use: the override connection string if set, otherwise the active
        /// query window's connection. Null when neither is available.
        /// </summary>
        public static async Task<ActiveConnection> GetAsync(string connectionStringOverride)
        {
            if (!string.IsNullOrWhiteSpace(connectionStringOverride))
            {
                var builder = new SqlConnectionStringBuilder(connectionStringOverride);
                return new ActiveConnection(builder.DataSource, builder.InitialCatalog, "connection string from SSMS Claude Chat settings",
                    () => new SqlConnection(connectionStringOverride));
            }

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            var uiConnectionInfo = GetActiveUIConnectionInfo();
            if (uiConnectionInfo == null) return null;

            var server = GetProperty(uiConnectionInfo, "ServerName") as string;
            var options = GetProperty(uiConnectionInfo, "AdvancedOptions") as NameValueCollection;
            var database = options?["DATABASE"];

            var factory = CreateSsmsFactory(uiConnectionInfo) ?? CreateFallbackFactory(uiConnectionInfo, server, database);
            if (factory == null) return null;

            return new ActiveConnection(server, database, "active query window", factory);
        }

        /// <summary>The UIConnectionInfo of the active query window, or null (no window, or not connected).</summary>
        public static object GetActiveUIConnectionInfo()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var scriptFactory = GetScriptFactory();
            var active = GetProperty(scriptFactory, "CurrentlyActiveWndConnectionInfo");
            return GetProperty(active, "UIConnectionInfo");
        }

        /// <summary>The internal IScriptFactory service (opens query windows), or null.</summary>
        public static object GetScriptFactory()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var serviceCache = FindType(ServiceCacheType, null);
            return serviceCache?.GetProperty("ScriptFactory", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
        }

        // Preferred: let SSMS build the connection itself, so encryption settings and Entra tokens are honoured.
        private static Func<IDbConnection> CreateSsmsFactory(object uiConnectionInfo)
        {
            try
            {
                var util = FindType(ConnectionUtilType, "ConnectionDlg.dll");
                var method = util?.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(m => m.Name == "GetCoreConnectionInfo"
                                         && m.GetParameters().Length == 1
                                         && m.GetParameters()[0].ParameterType.IsInstanceOfType(uiConnectionInfo));
                if (method == null) return null;

                var coreInfo = method.Invoke(null, new[] { uiConnectionInfo });
                var create = coreInfo?.GetType().GetMethod("CreateConnectionObject", Type.EmptyTypes);
                if (create == null) return null;

                return () => (IDbConnection)create.Invoke(coreInfo, null);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("SsmsChat: GetCoreConnectionInfo failed: " + ex);
                return null;
            }
        }

        // Fallback: Windows or SQL Server authentication only.
        private static Func<IDbConnection> CreateFallbackFactory(object uiConnectionInfo, string server, string database)
        {
            if (string.IsNullOrEmpty(server)) return null;

            var authType = GetProperty(uiConnectionInfo, "AuthenticationType") as int? ?? -1;
            var builder = new SqlConnectionStringBuilder
            {
                DataSource = server,
                ApplicationName = "SSMS Claude Chat",
                Encrypt = true,
                TrustServerCertificate = false
            };
            if (!string.IsNullOrEmpty(database)) builder.InitialCatalog = database;

            if (authType == 0)
            {
                builder.IntegratedSecurity = true;
            }
            else if (authType == 1)
            {
                builder.UserID = GetProperty(uiConnectionInfo, "UserName") as string ?? string.Empty;
                builder.Password = GetProperty(uiConnectionInfo, "Password") as string ?? string.Empty;
            }
            else
            {
                return null;
            }

            var connectionString = builder.ConnectionString;
            return () => new SqlConnection(connectionString);
        }

        private static Type FindType(string fullName, string assemblyFileToLoad)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.IsDynamic) continue;
                var type = assembly.GetType(fullName, false);
                if (type != null) return type;
            }

            if (assemblyFileToLoad == null) return null;

            // Not loaded yet: load it from the SSMS IDE folder.
            var ideFolder = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule.FileName);
            var path = Path.Combine(ideFolder ?? string.Empty, assemblyFileToLoad);
            return File.Exists(path) ? Assembly.LoadFrom(path).GetType(fullName, false) : null;
        }

        /// <summary>Reads a public property, including explicitly implemented interface properties.</summary>
        internal static object GetProperty(object target, string name)
        {
            if (target == null) return null;
            var type = target.GetType();
            var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance)
                           ?? type.GetInterfaces().Select(i => i.GetProperty(name)).FirstOrDefault(p => p != null);
            return property?.GetValue(target);
        }
    }
}
