using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeCodeVS.Mcp
{
    internal delegate Task ConnectionHandler(WebSocketConnection connection, CancellationToken cancellationToken);

    /// <summary>
    /// Listens on an ephemeral loopback port and upgrades incoming HTTP requests to WebSocket.
    /// Every connection must present the shared secret from the lock file; without it the
    /// socket is refused, because any local process could otherwise drive the IDE.
    /// </summary>
    internal sealed class WebSocketServer : IDisposable
    {
        private const string WebSocketAcceptGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
        private const string AuthorizationHeader = "x-claude-code-ide-authorization";
        private const int HandshakeTimeoutMs = 10000;

        private readonly TcpListener _listener;
        private readonly string _authToken;
        private readonly string _subProtocol;
        private readonly ConnectionHandler _handler;
        private readonly CancellationTokenSource _cancellation = new CancellationTokenSource();
        private int _disposed;

        /// <param name="port">
        /// Zero asks the operating system for a free port, which is right for the discovery
        /// endpoint because the CLI reads the port from the lock file. A fixed port is needed
        /// when the address has to appear in a static MCP server configuration.
        /// </param>
        public WebSocketServer(string authToken, string subProtocol, ConnectionHandler handler, int port = 0)
        {
            if (handler == null) throw new ArgumentNullException("handler");

            _authToken = authToken;
            _subProtocol = subProtocol;
            _handler = handler;
            _listener = new TcpListener(IPAddress.Loopback, port);
        }

        public int Port { get; private set; }

        public void Start()
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;

            var token = _cancellation.Token;
            Task.Run(() => AcceptLoopAsync(token));
        }

        private async Task AcceptLoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (SocketException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    Log.Error("Accept loop failed", ex);
                    return;
                }

                var accepted = client;
                var ignored = Task.Run(() => ServeAsync(accepted, cancellationToken));
            }
        }

        private async Task ServeAsync(TcpClient client, CancellationToken cancellationToken)
        {
            WebSocketConnection connection = null;
            try
            {
                client.NoDelay = true;
                var stream = client.GetStream();

                using (var handshakeTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    handshakeTimeout.CancelAfter(HandshakeTimeoutMs);
                    var headers = await ReadHeadersAsync(stream, handshakeTimeout.Token).ConfigureAwait(false);
                    if (headers == null)
                    {
                        client.Close();
                        return;
                    }

                    if (!await CompleteHandshakeAsync(stream, headers, handshakeTimeout.Token).ConfigureAwait(false))
                    {
                        client.Close();
                        return;
                    }
                }

                connection = new WebSocketConnection(stream);
                await _handler(connection, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Shutting down.
            }
            catch (IOException)
            {
                // The peer disappeared; routine.
            }
            catch (Exception ex)
            {
                Log.Error("Connection failed", ex);
            }
            finally
            {
                if (connection != null) connection.Dispose();
                try { client.Close(); } catch (Exception) { }
            }
        }

        private async Task<bool> CompleteHandshakeAsync(
            Stream stream,
            Dictionary<string, string> headers,
            CancellationToken cancellationToken)
        {
            string upgrade;
            headers.TryGetValue("upgrade", out upgrade);
            if (upgrade == null || !upgrade.Trim().Equals("websocket", StringComparison.OrdinalIgnoreCase))
            {
                await WriteStatusAsync(stream, "400 Bad Request", cancellationToken).ConfigureAwait(false);
                return false;
            }

            if (!string.IsNullOrEmpty(_authToken))
            {
                string presented;
                headers.TryGetValue(AuthorizationHeader, out presented);
                if (!FixedTimeEquals(presented, _authToken))
                {
                    Log.Info("Rejected a WebSocket connection with a missing or wrong authorization token.");
                    await WriteStatusAsync(stream, "401 Unauthorized", cancellationToken).ConfigureAwait(false);
                    return false;
                }
            }

            string key;
            headers.TryGetValue("sec-websocket-key", out key);
            if (string.IsNullOrEmpty(key))
            {
                await WriteStatusAsync(stream, "400 Bad Request", cancellationToken).ConfigureAwait(false);
                return false;
            }

            string acceptValue;
            using (var sha1 = SHA1.Create())
            {
                var digest = sha1.ComputeHash(Encoding.ASCII.GetBytes(key.Trim() + WebSocketAcceptGuid));
                acceptValue = Convert.ToBase64String(digest);
            }

            var response = new StringBuilder();
            response.Append("HTTP/1.1 101 Switching Protocols\r\n");
            response.Append("Upgrade: websocket\r\n");
            response.Append("Connection: Upgrade\r\n");
            response.Append("Sec-WebSocket-Accept: ").Append(acceptValue).Append("\r\n");

            string requestedProtocols;
            if (!string.IsNullOrEmpty(_subProtocol) &&
                headers.TryGetValue("sec-websocket-protocol", out requestedProtocols) &&
                ContainsProtocol(requestedProtocols, _subProtocol))
            {
                response.Append("Sec-WebSocket-Protocol: ").Append(_subProtocol).Append("\r\n");
            }

            response.Append("\r\n");

            var bytes = Encoding.ASCII.GetBytes(response.ToString());
            await stream.WriteAsync(bytes, 0, bytes.Length, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }

        private static bool ContainsProtocol(string headerValue, string wanted)
        {
            foreach (var candidate in headerValue.Split(','))
            {
                if (candidate.Trim().Equals(wanted, StringComparison.OrdinalIgnoreCase)) return true;
            }

            return false;
        }

        private static async Task WriteStatusAsync(Stream stream, string status, CancellationToken cancellationToken)
        {
            try
            {
                var bytes = Encoding.ASCII.GetBytes(
                    "HTTP/1.1 " + status + "\r\nConnection: close\r\nContent-Length: 0\r\n\r\n");
                await stream.WriteAsync(bytes, 0, bytes.Length, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Best effort.
            }
        }

        /// <summary>Reads the request line and headers, stopping at the blank line.</summary>
        private static async Task<Dictionary<string, string>> ReadHeadersAsync(Stream stream, CancellationToken cancellationToken)
        {
            var raw = new StringBuilder();
            var one = new byte[1];
            int terminatorProgress = 0;

            while (raw.Length < 16384)
            {
                int read = await stream.ReadAsync(one, 0, 1, cancellationToken).ConfigureAwait(false);
                if (read == 0) return null;

                char c = (char)one[0];
                raw.Append(c);

                bool expectingCr = terminatorProgress == 0 || terminatorProgress == 2;
                if (expectingCr && c == '\r') terminatorProgress++;
                else if (!expectingCr && c == '\n') terminatorProgress++;
                else terminatorProgress = c == '\r' ? 1 : 0;

                if (terminatorProgress == 4) break;
            }

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var lines = raw.ToString().Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 1; i < lines.Length; i++)
            {
                int separator = lines[i].IndexOf(':');
                if (separator <= 0) continue;

                var name = lines[i].Substring(0, separator).Trim().ToLowerInvariant();
                var value = lines[i].Substring(separator + 1).Trim();
                headers[name] = value;
            }

            return headers;
        }

        private static bool FixedTimeEquals(string a, string b)
        {
            if (a == null || b == null) return false;

            var left = Encoding.UTF8.GetBytes(a);
            var right = Encoding.UTF8.GetBytes(b);
            if (left.Length != right.Length) return false;

            int difference = 0;
            for (int i = 0; i < left.Length; i++)
            {
                difference |= left[i] ^ right[i];
            }

            return difference == 0;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

            try { _cancellation.Cancel(); } catch (Exception) { }
            try { _listener.Stop(); } catch (Exception) { }
            _cancellation.Dispose();
        }
    }
}
