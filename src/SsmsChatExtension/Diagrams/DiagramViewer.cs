using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;

namespace SsmsChatExtension.Diagrams
{
    /// <summary>
    /// Renders Mermaid diagrams (flowcharts of procedure logic, ER diagrams of tables, ...) as a standalone HTML
    /// page in %LOCALAPPDATA%\SsmsChatExtension\diagrams and opens it in the default browser.
    /// Mermaid is loaded from a CDN, so viewing needs internet access; the source is always shown on the page too.
    /// </summary>
    internal static class DiagramViewer
    {
        private const string MermaidScript = "https://cdn.jsdelivr.net/npm/mermaid@11/dist/mermaid.esm.min.mjs";

        public static string Folder
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SsmsChatExtension", "diagrams");
            }
        }

        /// <summary>Writes the page and opens it. Returns the file path.</summary>
        public static string Show(string title, string mermaid, string description)
        {
            if (string.IsNullOrWhiteSpace(mermaid)) throw new ArgumentException("'mermaid' is required.");
            title = string.IsNullOrWhiteSpace(title) ? "Diagram" : title.Trim();

            Directory.CreateDirectory(Folder);
            var path = Path.Combine(Folder, SafeFileName(title) + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".html");
            File.WriteAllText(path, BuildHtml(title, StripFence(mermaid), description), new UTF8Encoding(false));

            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return path;
        }

        private static string BuildHtml(string title, string mermaid, string description)
        {
            var source = WebUtility.HtmlEncode(mermaid);
            var html = new StringBuilder()
                .AppendLine("<!DOCTYPE html>")
                .AppendLine("<html lang=\"en\"><head><meta charset=\"utf-8\">")
                .Append("<title>").Append(WebUtility.HtmlEncode(title)).AppendLine("</title>")
                .AppendLine("<style>")
                .AppendLine("body{font-family:Segoe UI,sans-serif;margin:24px;color:#1f2328;background:#fff}")
                .AppendLine("h1{font-size:20px;margin:0 0 4px} p.desc{margin:0 0 16px;color:#57606a}")
                .AppendLine(".toolbar{margin:0 0 12px} button{font:inherit;padding:4px 10px;margin-right:6px;cursor:pointer}")
                .AppendLine("#diagram{overflow:auto;border:1px solid #d0d7de;border-radius:6px;padding:16px}")
                .AppendLine("#error{color:#cf222e;white-space:pre-wrap} pre{background:#f6f8fa;padding:12px;border-radius:6px;overflow:auto}")
                .AppendLine("</style></head><body>")
                .Append("<h1>").Append(WebUtility.HtmlEncode(title)).AppendLine("</h1>");

            if (!string.IsNullOrWhiteSpace(description))
            {
                html.Append("<p class=\"desc\">").Append(WebUtility.HtmlEncode(description.Trim())).AppendLine("</p>");
            }

            html.AppendLine("<div class=\"toolbar\"><button id=\"svg\">Download SVG</button><button id=\"copy\">Copy Mermaid source</button></div>")
                .Append("<div id=\"diagram\"><pre class=\"mermaid\">").Append(source).AppendLine("</pre></div>")
                .AppendLine("<div id=\"error\"></div>")
                .Append("<details><summary>Mermaid source</summary><pre id=\"source\">").Append(source).AppendLine("</pre></details>")
                .AppendLine("<script type=\"module\">")
                .Append("import mermaid from '").Append(MermaidScript).AppendLine("';")
                .AppendLine("mermaid.initialize({ startOnLoad: false, securityLevel: 'strict', theme: 'default' });")
                .AppendLine("try { await mermaid.run({ querySelector: '.mermaid' }); }")
                .AppendLine("catch (e) { document.getElementById('error').textContent = 'Could not render the diagram: ' + (e.message || e); }")
                .AppendLine("document.getElementById('svg').onclick = () => {")
                .AppendLine("  const svg = document.querySelector('#diagram svg'); if (!svg) return;")
                .AppendLine("  const a = document.createElement('a');")
                .AppendLine("  a.href = URL.createObjectURL(new Blob([svg.outerHTML], { type: 'image/svg+xml' }));")
                .AppendLine("  a.download = document.title + '.svg'; a.click(); };")
                .AppendLine("document.getElementById('copy').onclick = () => navigator.clipboard.writeText(document.getElementById('source').textContent);")
                .AppendLine("</script>")
                .AppendLine("<noscript>Enable JavaScript to render the diagram; the source is above.</noscript>")
                .AppendLine("</body></html>");
            return html.ToString();
        }

        /// <summary>Accepts the diagram with or without a ```mermaid fence.</summary>
        private static string StripFence(string mermaid)
        {
            var text = mermaid.Trim();
            if (!text.StartsWith("```", StringComparison.Ordinal)) return text;

            var firstNewline = text.IndexOf('\n');
            if (firstNewline < 0) return text;
            text = text.Substring(firstNewline + 1);
            if (text.TrimEnd().EndsWith("```", StringComparison.Ordinal)) text = text.TrimEnd().Substring(0, text.TrimEnd().Length - 3);
            return text.Trim();
        }

        private static string SafeFileName(string title)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var name = new string(title.Select(c => invalid.Contains(c) || char.IsWhiteSpace(c) ? '-' : c).ToArray());
            return name.Length > 60 ? name.Substring(0, 60) : name;
        }
    }
}
