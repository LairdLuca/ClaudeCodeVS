using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeCodeVS.Mcp
{
    /// <summary>
    /// Speaks JSON-RPC 2.0 over one WebSocket connection, implementing the slice of the Model
    /// Context Protocol that the Claude Code CLI uses to drive an IDE.
    /// </summary>
    internal sealed class McpSession : IDisposable
    {
        public const string ServerVersion = "1.1.0";
        private const string DefaultProtocolVersion = "2024-11-05";

        private readonly WebSocketConnection _connection;
        private readonly IMcpToolHost _host;
        private readonly CancellationTokenSource _sessionCancellation = new CancellationTokenSource();
        private int _disposed;

        public McpSession(WebSocketConnection connection, IMcpToolHost host)
        {
            _connection = connection;
            _host = host;
        }

        /// <summary>The pid the CLI reported in its <c>ide_connected</c> notification, if any.</summary>
        public int? ClientProcessId { get; private set; }

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _sessionCancellation.Token))
            {
                var token = linked.Token;
                var inFlight = new List<Task>();

                while (!token.IsCancellationRequested)
                {
                    string raw;
                    try
                    {
                        raw = await _connection.ReceiveTextAsync(token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    if (raw == null) break;

                    // Dispatched concurrently on purpose: openDiff parks until the user acts on
                    // the diff window, and a blocked read loop would stall every other request,
                    // including the close_tab that is supposed to release it.
                    var handling = Task.Run(() => HandleMessageAsync(raw, token), token);
                    inFlight.Add(handling);
                    inFlight.RemoveAll(t => t.IsCompleted);
                }
            }
        }

        private async Task HandleMessageAsync(string raw, CancellationToken cancellationToken)
        {
            Dictionary<string, object> message;
            try
            {
                message = Json.ParseObject(raw);
            }
            catch (Exception ex)
            {
                Log.Error("Received a message that is not valid JSON", ex);
                return;
            }

            if (message == null) return;

            var method = Json.GetString(message, "method");
            var id = Json.GetRaw(message, "id");
            var parameters = Json.GetObject(message, "params");

            if (method == null)
            {
                // A response to something we sent. This integration issues only notifications,
                // so there is nothing to correlate.
                return;
            }

            if (id == null)
            {
                await HandleNotificationAsync(method, parameters).ConfigureAwait(false);
                return;
            }

            try
            {
                var result = await HandleRequestAsync(method, parameters, cancellationToken).ConfigureAwait(false);
                await SendAsync(Json.Obj("jsonrpc", "2.0", "id", id, "result", result)).ConfigureAwait(false);
            }
            catch (MethodNotFoundException)
            {
                await SendErrorAsync(id, -32601, "Method not found: " + method).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await SendErrorAsync(id, -32000, "The request was cancelled.").ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Error("Request '" + method + "' failed", ex);
                await SendErrorAsync(id, -32603, ex.Message).ConfigureAwait(false);
            }
        }

        private Task HandleNotificationAsync(string method, Dictionary<string, object> parameters)
        {
            switch (method)
            {
                case "ide_connected":
                    ClientProcessId = Json.GetInt(parameters, "pid");
                    Log.Info("Claude Code attached (pid " +
                        (ClientProcessId.HasValue
                            ? ClientProcessId.Value.ToString(CultureInfo.InvariantCulture)
                            : "unknown") + ").");
                    break;

                case "notifications/initialized":
                case "initialized":
                    break;

                default:
                    Log.Info("Ignoring the unhandled notification '" + method + "'.");
                    break;
            }

            return Task.FromResult(0);
        }

        private async Task<object> HandleRequestAsync(
            string method,
            Dictionary<string, object> parameters,
            CancellationToken cancellationToken)
        {
            switch (method)
            {
                case "initialize":
                    {
                        var requested = Json.GetString(parameters, "protocolVersion");
                        return Json.Obj(
                            "protocolVersion", string.IsNullOrEmpty(requested) ? DefaultProtocolVersion : requested,
                            "capabilities", Json.Obj("tools", Json.Obj()),
                            "serverInfo", Json.Obj("name", _host.ServerName, "version", _host.ServerVersion));
                    }

                case "ping":
                    return Json.Obj();

                case "tools/list":
                    return Json.Obj("tools", _host.ListTools());

                case "tools/call":
                    return await _host.CallToolAsync(
                        Json.GetString(parameters, "name"),
                        Json.GetObject(parameters, "arguments"),
                        cancellationToken).ConfigureAwait(false);

                case "resources/list":
                    return Json.Obj("resources", new object[0]);

                case "prompts/list":
                    return Json.Obj("prompts", new object[0]);

                default:
                    throw new MethodNotFoundException();
            }
        }

        /// <summary>Wraps plain strings into a successful MCP tool result.</summary>
        public static Dictionary<string, object> Content(params string[] parts)
        {
            return Json.Obj("content", Json.TextContent(parts), "isError", false);
        }

        public static Dictionary<string, object> ToolError(string message)
        {
            return Json.Obj("content", Json.TextContent(message), "isError", true);
        }

        public async Task SendNotificationAsync(string method, object parameters)
        {
            try
            {
                await SendAsync(Json.Obj("jsonrpc", "2.0", "method", method, "params", parameters))
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Error("Could not deliver the '" + method + "' notification", ex);
            }
        }

        private Task SendErrorAsync(object id, int code, string message)
        {
            return SendAsync(Json.Obj(
                "jsonrpc", "2.0",
                "id", id,
                "error", Json.Obj("code", code, "message", message ?? string.Empty)));
        }

        private Task SendAsync(object payload)
        {
            return _connection.SendTextAsync(Json.Serialize(payload), _sessionCancellation.Token);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

            try { _sessionCancellation.Cancel(); } catch (Exception) { }
            _sessionCancellation.Dispose();
        }

        private sealed class MethodNotFoundException : Exception
        {
        }
    }
}
