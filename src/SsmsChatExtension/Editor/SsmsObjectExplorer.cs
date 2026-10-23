using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;

namespace SsmsChatExtension.Editor
{
    /// <summary>
    /// Refreshes SSMS Object Explorer (and the IntelliSense cache) after Claude changed the schema, so new or
    /// altered tables and procedures show up without pressing F5 in Object Explorer.
    /// Uses SSMS internals through reflection: IObjectExplorerService.FindNode(urn) returns a NodeContext whose
    /// TreeNode (ExplorerHierarchyNode) has Refresh(). Every step is best effort.
    /// </summary>
    internal static class SsmsObjectExplorer
    {
        private const string ServiceTypeName = "Microsoft.SqlServer.Management.UI.VSIntegration.ObjectExplorer.IObjectExplorerService";

        private static readonly Regex SchemaChange = new Regex(@"\b(CREATE|ALTER|DROP|sp_rename|SELECT\s[\s\S]*?\bINTO)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>True when the script probably changed objects (DDL, sp_rename, SELECT INTO).</summary>
        public static bool ChangesSchema(string sql)
        {
            return !string.IsNullOrEmpty(sql) && SchemaChange.IsMatch(sql);
        }

        /// <summary>
        /// Refreshes the database node (or the server node when <paramref name="database"/> is empty) and the
        /// IntelliSense cache. Returns a short description of what happened, for the tool result.
        /// </summary>
        public static async Task<string> RefreshAsync(string server, string database)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            string result;
            try
            {
                result = RefreshNode(server, database);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("SsmsChat: Object Explorer refresh failed: " + ex);
                result = "Object Explorer could not be refreshed (" + (ex.InnerException ?? ex).Message + ").";
            }

            try
            {
                var dte = await AsyncServiceProvider.GlobalProvider.GetServiceAsync(typeof(DTE)) as DTE2;
                dte?.ExecuteCommand("Edit.RefreshLocalCache");
            }
            catch
            {
                // No active query window - nothing to refresh for IntelliSense.
            }
            return result;
        }

        /// <summary>
        /// Waits (in the background) until the active query window finishes executing, then refreshes.
        /// SSMS runs F5 asynchronously, so this polls whether "Cancel Executing Query" is still available.
        /// </summary>
        public static void RefreshWhenQueryCompletes(string server, string database)
        {
            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                var dte = await AsyncServiceProvider.GlobalProvider.GetServiceAsync(typeof(DTE)) as DTE2;
                var started = DateTime.UtcNow;

                await Task.Delay(500);
                while (dte != null && DateTime.UtcNow - started < TimeSpan.FromMinutes(30))
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    bool executing;
                    try
                    {
                        executing = dte.Commands.Item("Query.CancelExecutingQuery").IsAvailable;
                    }
                    catch
                    {
                        break;
                    }
                    if (!executing) break;
                    await Task.Delay(1000);
                }

                await RefreshAsync(server, database);
            }).FileAndForget("SsmsChat/RefreshAfterQuery");
        }

        private static string RefreshNode(string server, string database)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (string.IsNullOrWhiteSpace(server)) return "Object Explorer not refreshed: unknown server.";

            var service = GetObjectExplorerService();
            if (service == null) return "Object Explorer not refreshed: the Object Explorer service is not available.";

            var urn = "Server[@Name='" + Escape(server) + "']";
            if (!string.IsNullOrWhiteSpace(database)) urn += "/Database[@Name='" + Escape(database.Trim().Trim('[', ']')) + "']";

            object node;
            try
            {
                node = FindMethod(service, "FindNode").Invoke(service, new object[] { urn });
            }
            catch (TargetInvocationException)
            {
                node = null; // FindNode throws when the node is not in the tree.
            }
            if (node == null)
            {
                return "Object Explorer: " + (string.IsNullOrWhiteSpace(database) ? server : database)
                       + " is not expanded there, so it will load fresh when opened.";
            }

            var treeNode = node.GetType().GetProperty("TreeNode", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(node);
            var refresh = treeNode?.GetType().GetMethod("Refresh", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
            if (refresh == null) return "Object Explorer not refreshed: this SSMS version has no refreshable node.";

            refresh.Invoke(treeNode, null);
            return "Refreshed " + (string.IsNullOrWhiteSpace(database) ? server : database) + " in Object Explorer.";
        }

        private static object GetObjectExplorerService()
        {
            var serviceType = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => !a.IsDynamic)
                .Select(a => a.GetType(ServiceTypeName, false))
                .FirstOrDefault(t => t != null);
            if (serviceType == null) return null;

            // SSMS's own service provider first (what SSMS add-ins use), then the VS global provider.
            var serviceCache = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => !a.IsDynamic)
                .Select(a => a.GetType("Microsoft.SqlServer.Management.UI.VSIntegration.ServiceCache", false))
                .FirstOrDefault(t => t != null);
            var provider = serviceCache?.GetProperty("ServiceProvider", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) as IServiceProvider;
            return provider?.GetService(serviceType) ?? Package.GetGlobalService(serviceType);
        }

        private static MethodInfo FindMethod(object target, string name)
        {
            var type = target.GetType();
            return type.GetMethod(name)
                   ?? type.GetInterfaces().Select(i => i.GetMethod(name)).First(m => m != null);
        }

        private static string Escape(string value)
        {
            return value.Replace("'", "''");
        }
    }
}
