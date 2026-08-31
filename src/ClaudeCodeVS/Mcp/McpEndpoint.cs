using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeCodeVS.Mcp
{
    /// <summary>
    /// One listening MCP endpoint: a WebSocket server, the tools it exposes, and the sessions
    /// currently attached to it. The extension runs two, one for the IDE integration the CLI
    /// discovers through the lock file, one for debugger control.
    /// </summary>
    internal sealed class McpEndpoint : IDisposable
    {
        private readonly string _label;
        private readonly IMcpToolHost _host;
        private readonly WebSocketServer _server;
        private readonly object _gate = new object();
        private readonly List<McpSession> _sessions = new List<McpSession>();
        private int _disposed;

        public McpEndpoint(string label, string authToken, IMcpToolHost host, int port)
        {
            _label = label;
            _host = host;
            AuthToken = authToken;
            _server = new WebSocketServer(authToken, "mcp", HandleConnectionAsync, port);
        }

        public string AuthToken { get; private set; }

        public int Port { get { return _server.Port; } }

        public bool HasClients
        {
            get { lock (_gate) { return _sessions.Count > 0; } }
        }

        /// <summary>Returns false when the port is unavailable, leaving the other endpoint alive.</summary>
        public bool TryStart()
        {
            try
            {
                _server.Start();
                Log.Info(_label + " endpoint listening on 127.0.0.1:" + _server.Port + ".");
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("The " + _label + " endpoint could not start", ex);
                return false;
            }
        }

        private async Task HandleConnectionAsync(WebSocketConnection connection, CancellationToken cancellationToken)
        {
            var session = new McpSession(connection, _host);

            lock (_gate)
            {
                _sessions.Add(session);
            }

            Log.Info("A client connected to the " + _label + " endpoint.");

            try
            {
                await session.RunAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                lock (_gate)
                {
                    _sessions.Remove(session);
                }

                session.Dispose();
                Log.Info("A client disconnected from the " + _label + " endpoint.");
            }
        }

        public void Broadcast(string method, object payload)
        {
            McpSession[] sessions;
            lock (_gate)
            {
                sessions = _sessions.ToArray();
            }

            foreach (var session in sessions)
            {
                var ignored = session.SendNotificationAsync(method, payload);
            }
        }

        public static string CreateToken()
        {
            var bytes = new byte[32];
            using (var random = new RNGCryptoServiceProvider())
            {
                random.GetBytes(bytes);
            }

            return BitConverter.ToString(bytes).Replace("-", string.Empty).ToLowerInvariant();
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

            _server.Dispose();

            McpSession[] sessions;
            lock (_gate)
            {
                sessions = _sessions.ToArray();
                _sessions.Clear();
            }

            foreach (var session in sessions) session.Dispose();
        }
    }
}
