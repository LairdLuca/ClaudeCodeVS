'use strict';
/*
 * Exercises the Visual Studio side of the extension against a live IDE, asserting on each
 * result rather than just printing it. Complements mcp-smoke-test.js, which only reports.
 *
 *   node tools/live-check.js <solutionFolder> <fileInThatSolution>
 *
 * Use a project that Visual Studio can actually load and analyse. This repository's own
 * solution is a poor choice: a VSIX project does not load without the Visual Studio extension
 * development workload, so Roslyn never runs and every diagnostics check comes back empty for
 * reasons that have nothing to do with the extension.
 */

const fs = require('fs');
const os = require('os');
const net = require('net');
const path = require('path');
const crypto = require('crypto');

const IDE_DIR = path.join(os.homedir(), '.claude', 'ide');
const GUID = '258EAFA5-E914-47DA-95CA-C5AB0DC85B11';
const REPO = process.argv[2];
if (!REPO || !process.argv[3]) {
  console.error('usage: node tools/live-check.js <solutionFolder> <fileInThatSolution>');
  process.exit(2);
}
const TARGET = process.argv[3];

let passes = 0;
let failures = 0;
function check(label, ok, detail) {
  if (ok) { passes++; console.log(`  PASS  ${label}`); }
  else { failures++; console.log(`  FAIL  ${label}`); }
  if (detail !== undefined) console.log(`        ${detail}`);
}

function readLock() {
  const files = fs.readdirSync(IDE_DIR).filter((n) => n.endsWith('.lock'));
  for (const name of files) {
    const body = JSON.parse(fs.readFileSync(path.join(IDE_DIR, name), 'utf8'));
    if (/visual studio/i.test(body.ideName || '')) {
      return { port: parseInt(path.basename(name, '.lock'), 10), body, name };
    }
  }
  throw new Error('no Visual Studio lock file');
}

class Ws {
  constructor(socket) {
    this.socket = socket;
    this.buf = Buffer.alloc(0);
    this.onText = () => {};
    socket.on('data', (c) => { this.buf = Buffer.concat([this.buf, c]); this.drain(); });
  }
  static connect(port, token) {
    return new Promise((resolve, reject) => {
      const key = crypto.randomBytes(16).toString('base64');
      const socket = net.connect({ host: '127.0.0.1', port }, () => {
        socket.write('GET / HTTP/1.1\r\nHost: 127.0.0.1\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n' +
          `Sec-WebSocket-Key: ${key}\r\nSec-WebSocket-Version: 13\r\nSec-WebSocket-Protocol: mcp\r\n` +
          `X-Claude-Code-Ide-Authorization: ${token}\r\n\r\n`);
      });
      socket.on('error', reject);
      let head = Buffer.alloc(0);
      const onData = (c) => {
        head = Buffer.concat([head, c]);
        const end = head.indexOf('\r\n\r\n');
        if (end < 0) return;
        socket.removeListener('data', onData);
        const status = head.slice(0, end).toString('ascii').split('\r\n')[0];
        if (!/101/.test(status)) return reject(new Error(status));
        const ws = new Ws(socket);
        ws.buf = head.slice(end + 4);
        ws.drain();
        resolve(ws);
      };
      socket.on('data', onData);
    });
  }
  drain() {
    for (;;) {
      if (this.buf.length < 2) return;
      const opcode = this.buf[0] & 0x0f;
      let len = this.buf[1] & 0x7f, off = 2;
      if (len === 126) { if (this.buf.length < 4) return; len = this.buf.readUInt16BE(2); off = 4; }
      else if (len === 127) { if (this.buf.length < 10) return; len = Number(this.buf.readBigUInt64BE(2)); off = 10; }
      if (this.buf.length < off + len) return;
      const payload = this.buf.slice(off, off + len);
      this.buf = this.buf.slice(off + len);
      if (opcode === 0x1) this.onText(payload.toString('utf8'));
    }
  }
  send(text) {
    const payload = Buffer.from(text, 'utf8');
    const mask = crypto.randomBytes(4);
    const masked = Buffer.from(payload.map((b, i) => b ^ mask[i & 3]));
    let head;
    if (payload.length < 126) head = Buffer.from([0x81, 0x80 | payload.length]);
    else if (payload.length <= 0xffff) { head = Buffer.alloc(4); head[0] = 0x81; head[1] = 0x80 | 126; head.writeUInt16BE(payload.length, 2); }
    else { head = Buffer.alloc(10); head[0] = 0x81; head[1] = 0x80 | 127; head.writeBigUInt64BE(BigInt(payload.length), 2); }
    this.socket.write(Buffer.concat([head, mask, masked]));
  }
}

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

async function main() {
  const lock = readLock();
  console.log(`\n== lock file ==`);
  check('port matches the file name', Number.isInteger(lock.port) && lock.port > 0, `${lock.name} -> ${lock.port}`);
  check('transport is ws', lock.body.transport === 'ws');
  check('auth token present', typeof lock.body.authToken === 'string' && lock.body.authToken.length >= 32);
  check('advertises the open solution folder',
    (lock.body.workspaceFolders || []).some((f) => f.toLowerCase() === REPO.toLowerCase()),
    JSON.stringify(lock.body.workspaceFolders));

  const ws = await Ws.connect(lock.port, lock.body.authToken);
  console.log(`\n== session ==`);
  check('websocket handshake', true);

  let id = 0;
  const pending = new Map();
  const notifications = [];
  ws.onText = (raw) => {
    const m = JSON.parse(raw);
    if (m.id !== undefined && pending.has(m.id)) { pending.get(m.id)(m); pending.delete(m.id); }
    else if (m.method) notifications.push(m);
  };
  const rpc = (method, params, timeoutMs = 20000) => new Promise((resolve, reject) => {
    const myId = ++id;
    pending.set(myId, resolve);
    ws.send(JSON.stringify({ jsonrpc: '2.0', id: myId, method, params }));
    setTimeout(() => { if (pending.has(myId)) { pending.delete(myId); reject(new Error(method + ' timed out')); } }, timeoutMs);
  });
  const call = async (name, args) => {
    const r = await rpc('tools/call', { name, arguments: args || {} });
    return r.result.content[0].text;
  };

  const init = await rpc('initialize', { protocolVersion: '2024-11-05', capabilities: {}, clientInfo: { name: 'live-check', version: '1' } });
  check('initialize returns serverInfo', !!init.result.serverInfo, JSON.stringify(init.result.serverInfo));
  check('server name is not the JetBrains one (which would disable diagnostics)',
    init.result.serverInfo.name !== 'Claude Code JetBrains Plugin');
  ws.send(JSON.stringify({ jsonrpc: '2.0', method: 'notifications/initialized', params: {} }));
  ws.send(JSON.stringify({ jsonrpc: '2.0', method: 'ide_connected', params: { pid: process.pid } }));

  const tools = await rpc('tools/list', {});
  const names = tools.result.tools.map((t) => t.name);
  check('tools/list has the methods the CLI calls',
    ['getDiagnostics', 'openDiff', 'close_tab', 'closeAllDiffTabs', 'set_permission_mode'].every((n) => names.includes(n)),
    names.join(', '));

  console.log(`\n== workspace ==`);
  const folders = JSON.parse(await call('getWorkspaceFolders'));
  check('getWorkspaceFolders returns the solution folder',
    folders.folders.some((f) => f.toLowerCase() === REPO.toLowerCase()), JSON.stringify(folders));

  console.log(`\n== openFile ==`);
  const opened = await call('openFile', { filePath: TARGET, startLine: 40, endLine: 44 });
  check('openFile reports success', opened === 'FILE_OPENED', opened);
  await sleep(4000);

  const editors = JSON.parse(await call('getOpenEditors'));
  check('getOpenEditors lists the file that was opened',
    (editors.tabs || []).some((t) => (t.filePath || '').toLowerCase() === TARGET.toLowerCase()),
    (editors.tabs || []).map((t) => path.basename(t.filePath)).join(', '));

  const selection = JSON.parse(await call('getCurrentSelection'));
  check('getCurrentSelection points at that file',
    (selection.filePath || '').toLowerCase() === TARGET.toLowerCase(),
    `${selection.filePath} lines ${selection.selection && selection.selection.start.line}-${selection.selection && selection.selection.end.line}`);

  console.log(`\n== diagnostics ==`);
  console.log('  (waiting for the analyzers to populate the Error List)');
  let diags = [];
  for (let attempt = 0; attempt < 10; attempt++) {
    await sleep(3000);
    diags = JSON.parse(await call('getDiagnostics'));
    if (diags.length > 0) break;
  }
  check('getDiagnostics returns entries from the real Error List', diags.length > 0,
    `${diags.length} file(s)`);
  if (diags.length > 0) {
    const total = diags.reduce((n, f) => n + f.diagnostics.length, 0);
    console.log(`        ${total} diagnostic(s) across ${diags.length} file(s)`);
    const sample = diags[0].diagnostics[0];
    console.log(`        example: [${sample.severity}] ${sample.source} ${sample.code || ''} line ${sample.range.start.line + 1}`);
    console.log(`                 ${sample.message}`);
    check('severity uses the spelling the CLI maps',
      diags.every((f) => f.diagnostics.every((d) => ['Error', 'Warning', 'Info', 'Hint'].includes(d.severity))));
    check('uri is a file:// URI', /^file:\/\//.test(diags[0].uri), diags[0].uri);
    check('range has the zero-based LSP shape',
      typeof sample.range.start.line === 'number' && typeof sample.range.start.character === 'number');
  }

  console.log(`\n== scoped diagnostics ==`);
  const scoped = JSON.parse(await call('getDiagnostics', { uri: 'file://' + TARGET.replace(/\\/g, '/') }));
  check('a file-scoped request answers for that file', scoped.length === 1,
    scoped.length === 1 ? `${scoped[0].diagnostics.length} diagnostic(s) for ${path.basename(TARGET)}` : JSON.stringify(scoped.map((s) => s.uri)));

  console.log(`\n== diff window ==`);
  const proposal = fs.readFileSync(TARGET, 'utf8').replace('namespace ClaudeCodeVS.Ide', 'namespace ClaudeCodeVS.Ide // proposed by the live check');
  const tabName = '\u273B [Claude Code] DiffTabManager.cs (live01) \u29C9';

  // openDiff parks until the user acts, so it is issued without awaiting and released with
  // close_tab on the same connection. That only works if requests are dispatched concurrently.
  const diffPromise = rpc('tools/call', {
    name: 'openDiff',
    arguments: { old_file_path: TARGET, new_file_path: TARGET, new_file_contents: proposal, tab_name: tabName }
  }, 40000);

  await sleep(6000);
  console.log('  (diff window should be visible in the experimental instance now)');
  const closed = await call('close_tab', { tab_name: tabName });
  check('close_tab is answered', closed === 'TAB_CLOSED', closed);

  const diffResult = await diffPromise;
  const diffText = diffResult.result.content[0].text;
  check('openDiff returns TAB_CLOSED once the tab is closed by the caller', diffText === 'TAB_CLOSED', diffText);
  check('openDiff did not block the other requests on the connection', true);

  console.log(`\n== permission mode ==`);
  const mode = await call('set_permission_mode', { mode: 'acceptEdits' });
  check('set_permission_mode is accepted', mode === 'PERMISSION_MODE_SET', mode);

  console.log(`\n== notifications received ==`);
  const selectionChanges = notifications.filter((n) => n.method === 'selection_changed');
  check('selection_changed arrived after openFile moved the caret', selectionChanges.length > 0,
    `${selectionChanges.length} notification(s); last: ${selectionChanges.length ? JSON.stringify(selectionChanges[selectionChanges.length - 1].params.filePath) : 'none'}`);

  console.log(`\n================ ${passes} passed, ${failures} failed ================\n`);
  ws.socket.destroy();
  process.exit(failures === 0 ? 0 : 1);
}

main().catch((e) => { console.error('\nFATAL: ' + e.message); process.exit(2); });
