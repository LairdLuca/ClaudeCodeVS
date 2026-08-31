using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeCodeVS.Mcp
{
    /// <summary>
    /// A server-side RFC 6455 WebSocket connection over an already upgraded stream.
    ///
    /// Written by hand rather than using <see cref="System.Net.HttpListener"/>: binding an
    /// HttpListener prefix on Windows needs either elevation or a netsh URL ACL reservation,
    /// which an IDE extension cannot assume. A raw TcpListener on the loopback interface has
    /// no such requirement, and the handshake plus text framing is the only part of the
    /// protocol this integration needs.
    /// </summary>
    internal sealed class WebSocketConnection : IDisposable
    {
        private const int MaxMessageBytes = 64 * 1024 * 1024;

        private readonly Stream _stream;
        private readonly SemaphoreSlim _writeLock = new SemaphoreSlim(1, 1);
        private int _disposed;

        public WebSocketConnection(Stream stream)
        {
            _stream = stream;
        }

        /// <summary>
        /// Reads the next complete text message. Returns <c>null</c> when the peer closed the
        /// connection. Ping frames are answered inline and never surface to the caller.
        /// </summary>
        public async Task<string> ReceiveTextAsync(CancellationToken cancellationToken)
        {
            MemoryStream assembled = null;
            int assembledOpcode = 0;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var header = new byte[2];
                if (!await ReadExactAsync(header, 0, 2, cancellationToken).ConfigureAwait(false))
                {
                    return null;
                }

                bool isFinal = (header[0] & 0x80) != 0;
                int opcode = header[0] & 0x0F;
                bool isMasked = (header[1] & 0x80) != 0;
                long payloadLength = header[1] & 0x7F;

                if (payloadLength == 126)
                {
                    var extended = new byte[2];
                    if (!await ReadExactAsync(extended, 0, 2, cancellationToken).ConfigureAwait(false)) return null;
                    payloadLength = (extended[0] << 8) | extended[1];
                }
                else if (payloadLength == 127)
                {
                    var extended = new byte[8];
                    if (!await ReadExactAsync(extended, 0, 8, cancellationToken).ConfigureAwait(false)) return null;
                    payloadLength = 0;
                    for (int i = 0; i < 8; i++)
                    {
                        payloadLength = (payloadLength << 8) | extended[i];
                    }
                }

                if (payloadLength < 0 || payloadLength > MaxMessageBytes)
                {
                    throw new IOException("WebSocket frame exceeds the maximum accepted size.");
                }

                byte[] mask = null;
                if (isMasked)
                {
                    mask = new byte[4];
                    if (!await ReadExactAsync(mask, 0, 4, cancellationToken).ConfigureAwait(false)) return null;
                }

                var payload = new byte[payloadLength];
                if (payloadLength > 0 &&
                    !await ReadExactAsync(payload, 0, (int)payloadLength, cancellationToken).ConfigureAwait(false))
                {
                    return null;
                }

                if (mask != null)
                {
                    for (int i = 0; i < payload.Length; i++)
                    {
                        payload[i] = (byte)(payload[i] ^ mask[i & 3]);
                    }
                }

                switch (opcode)
                {
                    case 0x8: // close
                        try
                        {
                            await SendFrameAsync(0x8, new byte[0], CancellationToken.None).ConfigureAwait(false);
                        }
                        catch (Exception)
                        {
                            // The peer is already gone; nothing useful to do.
                        }

                        return null;

                    case 0x9: // ping
                        await SendFrameAsync(0xA, payload, cancellationToken).ConfigureAwait(false);
                        continue;

                    case 0xA: // pong
                        continue;

                    case 0x0: // continuation
                    case 0x1: // text
                    case 0x2: // binary
                        if (opcode != 0x0)
                        {
                            assembled = new MemoryStream();
                            assembledOpcode = opcode;
                        }

                        if (assembled == null)
                        {
                            // Continuation without a start frame: ignore rather than tear down.
                            continue;
                        }

                        assembled.Write(payload, 0, payload.Length);
                        if (assembled.Length > MaxMessageBytes)
                        {
                            throw new IOException("WebSocket message exceeds the maximum accepted size.");
                        }

                        if (!isFinal)
                        {
                            continue;
                        }

                        var bytes = assembled.ToArray();
                        assembled.Dispose();
                        assembled = null;

                        if (assembledOpcode == 0x1)
                        {
                            return Encoding.UTF8.GetString(bytes);
                        }

                        continue; // binary payloads are not part of the MCP contract

                    default:
                        continue;
                }
            }
        }

        public Task SendTextAsync(string text, CancellationToken cancellationToken)
        {
            return SendFrameAsync(0x1, Encoding.UTF8.GetBytes(text ?? string.Empty), cancellationToken);
        }

        public async Task CloseAsync()
        {
            try
            {
                await SendFrameAsync(0x8, new byte[0], CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Closing is best effort.
            }
        }

        private async Task SendFrameAsync(byte opcode, byte[] payload, CancellationToken cancellationToken)
        {
            if (_disposed != 0) return;

            await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var header = new byte[10];
                int headerLength = 0;
                header[headerLength++] = (byte)(0x80 | opcode);

                int length = payload.Length;
                if (length < 126)
                {
                    header[headerLength++] = (byte)length;
                }
                else if (length <= ushort.MaxValue)
                {
                    header[headerLength++] = 126;
                    header[headerLength++] = (byte)(length >> 8);
                    header[headerLength++] = (byte)length;
                }
                else
                {
                    header[headerLength++] = 127;
                    for (int shift = 56; shift >= 0; shift -= 8)
                    {
                        header[headerLength++] = (byte)(((long)length >> shift) & 0xFF);
                    }
                }

                await _stream.WriteAsync(header, 0, headerLength, cancellationToken).ConfigureAwait(false);
                if (length > 0)
                {
                    await _stream.WriteAsync(payload, 0, length, cancellationToken).ConfigureAwait(false);
                }

                await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        private async Task<bool> ReadExactAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            int total = 0;
            while (total < count)
            {
                int read = await _stream
                    .ReadAsync(buffer, offset + total, count - total, cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0) return false;
                total += read;
            }

            return true;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

            try { _stream.Dispose(); } catch (Exception) { }
            _writeLock.Dispose();
        }
    }
}
