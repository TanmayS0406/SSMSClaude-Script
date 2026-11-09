using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SsmsChatExtension.Mcp
{
    /// <summary>
    /// Minimal MCP server (Streamable HTTP transport, JSON responses only) on the loopback interface with a random port
    /// and a random bearer token, so only the Claude Code process we start (which gets the token) can call it.
    /// A raw TcpListener is used instead of HttpListener because HttpListener needs a URL ACL without admin rights.
    /// </summary>
    internal sealed class McpHttpServer : IDisposable
    {
        private const string LatestProtocolVersion = "2025-06-18";
        private const int MaxBodyBytes = 4 * 1024 * 1024;

        private readonly IMcpToolProvider _tools;
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _stop = new CancellationTokenSource();

        public McpHttpServer(IMcpToolProvider tools)
        {
            _tools = tools;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            Token = CreateToken();
            _ = Task.Run(AcceptLoopAsync);
        }

        public int Port { get; }

        public string Token { get; }

        public string Url
        {
            // Loopback only, by design: the listener is bound to IPAddress.Loopback and every call needs the bearer token.
            get { return "http://127.0.0.1:" + Port + "/mcp"; } // DevSkim: ignore DS162092
        }

        public void Dispose()
        {
            _stop.Cancel();
            try { _listener.Stop(); } catch { /* already stopped */ }
        }

        private static string CreateToken()
        {
            var bytes = new byte[32];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        private async Task AcceptLoopAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
                }
                catch (Exception) when (_stop.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("SsmsChat MCP: accept failed: " + ex.Message);
                    continue;
                }
                var _ = Task.Run(() => HandleConnectionAsync(client));
            }
        }

        private async Task HandleConnectionAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    stream.ReadTimeout = 120000;
                    var input = new BufferedStream(stream);

                    // Keep-alive: serve requests until the client closes the connection.
                    while (!_stop.IsCancellationRequested)
                    {
                        var request = ReadRequest(input);
                        if (request == null) return;

                        var keepAlive = !string.Equals(request.Header("Connection"), "close", StringComparison.OrdinalIgnoreCase);
                        var response = await HandleRequestAsync(request).ConfigureAwait(false);
                        WriteResponse(stream, response, keepAlive);
                        if (!keepAlive) return;
                    }
                }
                catch (IOException)
                {
                    // Client went away or idle timeout.
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("SsmsChat MCP: connection failed: " + ex);
                }
            }
        }

        private async Task<HttpResponse> HandleRequestAsync(HttpRequest request)
        {
            var authorization = request.Header("Authorization") ?? string.Empty;
            if (!FixedTimeEquals(authorization, "Bearer " + Token))
            {
                return HttpResponse.Status(401, "Unauthorized");
            }
            if (request.Method != "POST")
            {
                // No server-initiated stream (GET) and no session to delete (DELETE).
                return HttpResponse.Status(405, "Method Not Allowed");
            }

            JToken message;
            try
            {
                message = JToken.Parse(request.Body);
            }
            catch (JsonException)
            {
                return HttpResponse.Json(400, RpcError(null, -32700, "Parse error"));
            }

            if (message is JArray batch)
            {
                var results = new JArray();
                foreach (var item in batch)
                {
                    var result = await HandleMessageAsync(item as JObject).ConfigureAwait(false);
                    if (result != null) results.Add(result);
                }
                return results.Count == 0 ? HttpResponse.Status(202, "Accepted") : HttpResponse.Json(200, results);
            }

            var single = await HandleMessageAsync(message as JObject).ConfigureAwait(false);
            return single == null ? HttpResponse.Status(202, "Accepted") : HttpResponse.Json(200, single);
        }

        /// <summary>Handles one JSON-RPC message. Returns null for notifications and responses.</summary>
        private async Task<JObject> HandleMessageAsync(JObject message)
        {
            if (message == null) return RpcError(null, -32600, "Invalid request");

            var id = message["id"];
            var method = (string)message["method"];
            if (method == null || id == null) return null;

            var parameters = message["params"] as JObject ?? new JObject();
            try
            {
                switch (method)
                {
                    case "initialize":
                        var requested = (string)parameters["protocolVersion"];
                        return RpcResult(id, new JObject
                        {
                            ["protocolVersion"] = requested ?? LatestProtocolVersion,
                            ["capabilities"] = new JObject { ["tools"] = new JObject { ["listChanged"] = false } },
                            ["serverInfo"] = new JObject { ["name"] = "ssms", ["version"] = "1.0.0" },
                            ["instructions"] = _tools.Instructions
                        });

                    case "ping":
                        return RpcResult(id, new JObject());

                    case "tools/list":
                        return RpcResult(id, new JObject { ["tools"] = _tools.ListTools() });

                    case "tools/call":
                        var name = (string)parameters["name"];
                        var arguments = parameters["arguments"] as JObject ?? new JObject();
                        string text;
                        var isError = false;
                        try
                        {
                            text = await _tools.CallToolAsync(name, arguments, _stop.Token).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            text = "Error: " + (ex is System.Reflection.TargetInvocationException && ex.InnerException != null ? ex.InnerException.Message : ex.Message);
                            isError = true;
                        }
                        return RpcResult(id, new JObject
                        {
                            ["content"] = new JArray(new JObject { ["type"] = "text", ["text"] = text ?? string.Empty }),
                            ["isError"] = isError
                        });

                    default:
                        return RpcError(id, -32601, "Method not found: " + method);
                }
            }
            catch (Exception ex)
            {
                return RpcError(id, -32603, ex.Message);
            }
        }

        private static JObject RpcResult(JToken id, JObject result)
        {
            return new JObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };
        }

        private static JObject RpcError(JToken id, int code, string message)
        {
            return new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id ?? JValue.CreateNull(),
                ["error"] = new JObject { ["code"] = code, ["message"] = message }
            };
        }

        private static bool FixedTimeEquals(string a, string b)
        {
            var diff = a.Length ^ b.Length;
            for (var i = 0; i < a.Length && i < b.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }

        // ---- tiny HTTP/1.1 reader / writer ----

        private static HttpRequest ReadRequest(Stream input)
        {
            var requestLine = ReadLine(input);
            if (string.IsNullOrEmpty(requestLine)) return null;

            var parts = requestLine.Split(' ');
            if (parts.Length < 2) return null;

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string line;
            while (!string.IsNullOrEmpty(line = ReadLine(input)))
            {
                var colon = line.IndexOf(':');
                if (colon > 0) headers[line.Substring(0, colon).Trim()] = line.Substring(colon + 1).Trim();
            }

            var body = string.Empty;
            if (headers.TryGetValue("Content-Length", out var lengthText) && int.TryParse(lengthText, out var length) && length > 0)
            {
                if (length > MaxBodyBytes) throw new IOException("Request body too large.");
                var buffer = new byte[length];
                var read = 0;
                while (read < length)
                {
                    var n = input.Read(buffer, read, length - read);
                    if (n == 0) throw new IOException("Connection closed while reading the body.");
                    read += n;
                }
                body = Encoding.UTF8.GetString(buffer);
            }

            return new HttpRequest(parts[0].ToUpperInvariant(), headers, body);
        }

        private static string ReadLine(Stream input)
        {
            var bytes = new List<byte>();
            int b;
            while ((b = input.ReadByte()) != -1)
            {
                if (b == '\n') break;
                if (b != '\r') bytes.Add((byte)b);
                if (bytes.Count > 16 * 1024) throw new IOException("Header line too long.");
            }
            if (b == -1 && bytes.Count == 0) return null;
            return Encoding.ASCII.GetString(bytes.ToArray());
        }

        private static void WriteResponse(Stream output, HttpResponse response, bool keepAlive)
        {
            var body = Encoding.UTF8.GetBytes(response.Body);
            var head = new StringBuilder()
                .Append("HTTP/1.1 ").Append(response.StatusCode).Append(' ').Append(response.Reason).Append("\r\n")
                .Append("Content-Type: ").Append(response.ContentType).Append("\r\n")
                .Append("Content-Length: ").Append(body.Length).Append("\r\n")
                .Append("Connection: ").Append(keepAlive ? "keep-alive" : "close").Append("\r\n")
                .Append("\r\n");

            var headBytes = Encoding.ASCII.GetBytes(head.ToString());
            output.Write(headBytes, 0, headBytes.Length);
            output.Write(body, 0, body.Length);
            output.Flush();
        }

        private sealed class HttpRequest
        {
            private readonly Dictionary<string, string> _headers;

            public HttpRequest(string method, Dictionary<string, string> headers, string body)
            {
                Method = method;
                _headers = headers;
                Body = body;
            }

            public string Method { get; }
            public string Body { get; }

            public string Header(string name)
            {
                return _headers.TryGetValue(name, out var value) ? value : null;
            }
        }

        private sealed class HttpResponse
        {
            private HttpResponse(int statusCode, string reason, string contentType, string body)
            {
                StatusCode = statusCode;
                Reason = reason;
                ContentType = contentType;
                Body = body;
            }

            public int StatusCode { get; }
            public string Reason { get; }
            public string ContentType { get; }
            public string Body { get; }

            public static HttpResponse Json(int statusCode, JToken body)
            {
                return new HttpResponse(statusCode, statusCode == 200 ? "OK" : "Bad Request", "application/json; charset=utf-8",
                    body.ToString(Formatting.None));
            }

            public static HttpResponse Status(int statusCode, string reason)
            {
                return new HttpResponse(statusCode, reason, "text/plain; charset=utf-8", string.Empty);
            }
        }
    }

    /// <summary>The tools exposed by <see cref="McpHttpServer"/>.</summary>
    internal interface IMcpToolProvider
    {
        string Instructions { get; }

        JArray ListTools();

        /// <summary>Runs a tool and returns its text result. Throw to report a tool error to the model.</summary>
        Task<string> CallToolAsync(string name, JObject arguments, CancellationToken cancellationToken);
    }
}
