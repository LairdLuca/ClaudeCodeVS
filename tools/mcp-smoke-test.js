#!/usr/bin/env node
/*
 * Talks to the extension the way the Claude Code CLI does, so the bridge can be verified
 * without running the CLI itself.
 *
 * It repeats exactly what the CLI does on /ide: read ~/.claude/ide/<port>.lock, take the port
 * from the file name, open a WebSocket to 127.0.0.1 on that port with the "mcp" subprotocol and
 * the X-Claude-Code-Ide-Authorization header, then run the MCP handshake.
 *
 *   node tools/mcp-smoke-test.js            pick the Visual Studio lock file automatically
 *   node tools/mcp-smoke-test.js 51234      use a specific port
 */

'use strict';

const fs = require('fs');
const os = require('os');
const net = require('net');
const path = require('path');
const crypto = require('crypto');

const IDE_DIR = path.join(os.homedir(), '.claude', 'ide');
const HANDSHAKE_GUID = '258EAFA5-E914-47DA-95CA-C5AB0DC85B11';

function findLockFile(wantedPort) {
  if (!fs.existsSync(IDE_DIR)) {
    throw new Error(`No lock file directory at ${IDE_DIR}. Is Visual Studio running with the extension installed?`);
  }

  const candidates = fs.readdirSync(IDE_DIR)
    .filter((name) => name.endsWith('.lock'))
    .map((name) => {
      const port = parseInt(path.basename(name, '.lock'), 10);
      let body = {};
      try {
        body = JSON.parse(fs.readFileSync(path.join(IDE_DIR, name), 'utf8'));
      } catch (err) {
        return null;
      }
      return { name, port, body };
    })
    .filter(Boolean);

  if (candidates.length === 0) {
    throw new Error(`No lock files in ${IDE_DIR}.`);
  }

  if (wantedPort) {
    const match = candidates.find((c) => c.port === wantedPort);
    if (!match) throw new Error(`No lock file for port ${wantedPort}.`);
    return match;
  }

  const visualStudio = candidates.find(
    (c) => typeof c.body.ideName === 'string' && /visual studio/i.test(c.body.ideName));
  if (!visualStudio) {
    const names = candidates.map((c) => `${c.port} (${c.body.ideName || 'unnamed'})`).join(', ');
    throw new Error(`No Visual Studio lock file found. Present: ${names}`);
  }
  return visualStudio;
}

/** Minimal RFC 6455 client: the handshake plus masked text frames. */
class WsClient {
  constructor(socket) {
    this.socket = socket;
    this.buffer = Buffer.alloc(0);
    this.handlers = [];
    this.fragments = [];
    socket.on('data', (chunk) => {
      this.buffer = Buffer.concat([this.buffer, chunk]);
      this.drain();
    });
  }

  static connect(port, authToken) {
    return new Promise((resolve, reject) => {
      const key = crypto.randomBytes(16).toString('base64');
      const expected = crypto
        .createHash('sha1')
        .update(key + HANDSHAKE_GUID)
        .digest('base64');

      const socket = net.connect({ host: '127.0.0.1', port }, () => {
        socket.write(
          `GET / HTTP/1.1\r\n` +
          `Host: 127.0.0.1:${port}\r\n` +
          `Upgrade: websocket\r\n` +
          `Connection: Upgrade\r\n` +
          `Sec-WebSocket-Key: ${key}\r\n` +
          `Sec-WebSocket-Version: 13\r\n` +
          `Sec-WebSocket-Protocol: mcp\r\n` +
          `X-Claude-Code-Ide-Authorization: ${authToken}\r\n` +
          `\r\n`);
      });

      socket.on('error', reject);

      let headerBuffer = Buffer.alloc(0);
      const onData = (chunk) => {
        headerBuffer = Buffer.concat([headerBuffer, chunk]);
        const end = headerBuffer.indexOf('\r\n\r\n');
        if (end < 0) return;

        socket.removeListener('data', onData);
        const header = headerBuffer.slice(0, end).toString('ascii');
        const rest = headerBuffer.slice(end + 4);

        if (!/^HTTP\/1\.1 101/.test(header)) {
          reject(new Error(`Handshake refused:\n${header}`));
          return;
        }
        if (!header.includes(expected)) {
          reject(new Error('Sec-WebSocket-Accept did not match the key that was sent.'));
          return;
        }
        if (!/sec-websocket-protocol:\s*mcp/i.test(header)) {
          reject(new Error('The server did not negotiate the "mcp" subprotocol.'));
          return;
        }

        const client = new WsClient(socket);
        if (rest.length > 0) {
          client.buffer = rest;
          client.drain();
        }
        resolve(client);
      };
      socket.on('data', onData);
    });
  }

  drain() {
    for (;;) {
      if (this.buffer.length < 2) return;

      const first = this.buffer[0];
      const fin = (first & 0x80) !== 0;
      const opcode = first & 0x0f;
      const masked = (this.buffer[1] & 0x80) !== 0;
      let length = this.buffer[1] & 0x7f;
      let offset = 2;

      if (length === 126) {
        if (this.buffer.length < offset + 2) return;
        length = this.buffer.readUInt16BE(offset);
        offset += 2;
      } else if (length === 127) {
        if (this.buffer.length < offset + 8) return;
        length = Number(this.buffer.readBigUInt64BE(offset));
        offset += 8;
      }

      let mask = null;
      if (masked) {
        if (this.buffer.length < offset + 4) return;
        mask = this.buffer.slice(offset, offset + 4);
        offset += 4;
      }

      if (this.buffer.length < offset + length) return;

      let payload = this.buffer.slice(offset, offset + length);
      this.buffer = this.buffer.slice(offset + length);

      if (mask) {
        payload = Buffer.from(payload.map((byte, i) => byte ^ mask[i & 3]));
      }

      if (opcode === 0x8) {
        this.socket.end();
        return;
      }
      if (opcode === 0x9) {
        this.sendFrame(0xa, payload);
        continue;
      }
      if (opcode === 0xa) continue;

      this.fragments.push(payload);
      if (!fin) continue;

      const message = Buffer.concat(this.fragments).toString('utf8');
      this.fragments = [];
      this.handlers.forEach((handler) => handler(message));
    }
  }

  onMessage(handler) {
    this.handlers.push(handler);
  }

  sendFrame(opcode, payload) {
    const mask = crypto.randomBytes(4);
    const masked = Buffer.from(payload.map((byte, i) => byte ^ mask[i & 3]));

    let header;
    if (payload.length < 126) {
      header = Buffer.from([0x80 | opcode, 0x80 | payload.length]);
    } else if (payload.length <= 0xffff) {
      header = Buffer.alloc(4);
      header[0] = 0x80 | opcode;
      header[1] = 0x80 | 126;
      header.writeUInt16BE(payload.length, 2);
    } else {
      header = Buffer.alloc(10);
      header[0] = 0x80 | opcode;
      header[1] = 0x80 | 127;
      header.writeBigUInt64BE(BigInt(payload.length), 2);
    }

    this.socket.write(Buffer.concat([header, mask, masked]));
  }

  send(text) {
    this.sendFrame(0x1, Buffer.from(text, 'utf8'));
  }

  close() {
    try {
      this.sendFrame(0x8, Buffer.alloc(0));
    } catch (err) {
      /* already gone */
    }
    this.socket.end();
  }
}

async function main() {
  const wantedPort = process.argv[2] ? parseInt(process.argv[2], 10) : null;
  const lock = findLockFile(wantedPort);

  console.log(`Lock file : ${path.join(IDE_DIR, lock.name)}`);
  console.log(`IDE       : ${lock.body.ideName}`);
  console.log(`Port      : ${lock.port}`);
  console.log(`PID       : ${lock.body.pid}`);
  console.log(`Transport : ${lock.body.transport}`);
  console.log(`Folders   : ${JSON.stringify(lock.body.workspaceFolders)}`);
  console.log(`Auth token: ${lock.body.authToken ? 'present' : 'MISSING'}`);
  console.log('');

  const client = await WsClient.connect(lock.port, lock.body.authToken);
  console.log('Handshake : ok (subprotocol "mcp" negotiated)\n');

  let nextId = 1;
  const pending = new Map();

  client.onMessage((raw) => {
    let message;
    try {
      message = JSON.parse(raw);
    } catch (err) {
      console.log(`  <- unparseable: ${raw}`);
      return;
    }

    if (message.id !== undefined && pending.has(message.id)) {
      const resolve = pending.get(message.id);
      pending.delete(message.id);
      resolve(message);
    } else if (message.method) {
      console.log(`  <- notification ${message.method}: ${JSON.stringify(message.params)}`);
    }
  });

  const request = (method, params) => new Promise((resolve, reject) => {
    const id = nextId++;
    pending.set(id, resolve);
    client.send(JSON.stringify({ jsonrpc: '2.0', id, method, params }));
    setTimeout(() => {
      if (pending.has(id)) {
        pending.delete(id);
        reject(new Error(`${method} timed out`));
      }
    }, 15000);
  });

  const initialize = await request('initialize', {
    protocolVersion: '2024-11-05',
    capabilities: {},
    clientInfo: { name: 'mcp-smoke-test', version: '1.0.0' }
  });
  console.log('initialize:', JSON.stringify(initialize.result, null, 2));

  client.send(JSON.stringify({ jsonrpc: '2.0', method: 'notifications/initialized', params: {} }));
  client.send(JSON.stringify({ jsonrpc: '2.0', method: 'ide_connected', params: { pid: process.pid } }));

  const tools = await request('tools/list', {});
  console.log('\ntools/list:', tools.result.tools.map((t) => t.name).join(', '));

  const folders = await request('tools/call', { name: 'getWorkspaceFolders', arguments: {} });
  console.log('\ngetWorkspaceFolders:', folders.result.content[0].text);

  const selection = await request('tools/call', { name: 'getCurrentSelection', arguments: {} });
  console.log('\ngetCurrentSelection:', selection.result.content[0].text);

  const editors = await request('tools/call', { name: 'getOpenEditors', arguments: {} });
  console.log('\ngetOpenEditors:', editors.result.content[0].text);

  const diagnostics = await request('tools/call', { name: 'getDiagnostics', arguments: {} });
  const parsed = JSON.parse(diagnostics.result.content[0].text);
  console.log(`\ngetDiagnostics: ${parsed.length} file(s) with diagnostics`);
  parsed.slice(0, 5).forEach((file) => {
    console.log(`  ${file.uri} -> ${file.diagnostics.length}`);
    file.diagnostics.slice(0, 3).forEach((d) => {
      console.log(`    [${d.severity}] line ${d.range.start.line + 1}: ${d.message}`);
    });
  });

  console.log('\nMove the caret in Visual Studio; selection_changed notifications appear below.');
  console.log('Press Ctrl+C to stop.\n');

  process.on('SIGINT', () => {
    client.close();
    process.exit(0);
  });
}

main().catch((err) => {
  console.error(`\nFAILED: ${err.message}`);
  process.exit(1);
});
