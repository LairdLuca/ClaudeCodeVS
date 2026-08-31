#!/usr/bin/env node
/*
 * Exercises the debugger endpoint end to end: sets a conditional breakpoint, starts the session,
 * waits for the stop to arrive as a notification, reads the call stack, the locals and an
 * evaluated expression, steps, then resumes and stops.
 *
 *   node tools/debug-check.js <sourceFile> <line> <condition>
 *
 * It asserts on the shape of samples/DebuggerSample, so run it against that:
 *
 *   node tools/debug-check.js <repo>\samples\DebuggerSample\Program.cs 44 "order.Id == 1003"
 *
 * The waiting is driven by debugger_state_changed notifications rather than by polling, which is
 * the same shape a caller has to use for real: a hit breakpoint leaves the triggering request
 * hanging, so nothing that triggered it can be awaited.
 */

'use strict';

const fs = require('fs');
const os = require('os');
const net = require('net');
const path = require('path');
const crypto = require('crypto');

const TOKEN_FILE = path.join(process.env.LOCALAPPDATA || os.homedir(), 'ClaudeCodeVS', 'debug-token');
const PORT = Number(process.env.CLAUDE_VS_DEBUG_PORT || 8375);

const FILE = process.argv[2];
const LINE = Number(process.argv[3]);
const CONDITION = process.argv[4] || '';

if (!FILE || !LINE) {
  console.error('usage: node tools/debug-check.js <sourceFile> <line> [condition]');
  process.exit(2);
}

let passes = 0;
let failures = 0;
function check(label, ok, detail) {
  if (ok) { passes++; console.log(`  PASS  ${label}`); }
  else { failures++; console.log(`  FAIL  ${label}`); }
  if (detail !== undefined) console.log(`        ${detail}`);
}

class Ws {
  constructor(s) {
    this.s = s; this.buf = Buffer.alloc(0); this.onText = () => {};
    s.on('data', (c) => { this.buf = Buffer.concat([this.buf, c]); this.drain(); });
  }
  static connect(port, token) {
    return new Promise((res, rej) => {
      const key = crypto.randomBytes(16).toString('base64');
      const s = net.connect({ host: '127.0.0.1', port }, () => s.write(
        'GET / HTTP/1.1\r\nHost: 127.0.0.1\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n' +
        `Sec-WebSocket-Key: ${key}\r\nSec-WebSocket-Version: 13\r\nSec-WebSocket-Protocol: mcp\r\n` +
        `X-Claude-Code-Ide-Authorization: ${token}\r\n\r\n`));
      s.on('error', rej);
      let h = Buffer.alloc(0);
      const on = (c) => {
        h = Buffer.concat([h, c]);
        const e = h.indexOf('\r\n\r\n');
        if (e < 0) return;
        s.removeListener('data', on);
        const status = h.slice(0, e).toString('ascii').split('\r\n')[0];
        if (!/101/.test(status)) return rej(new Error(status));
        const ws = new Ws(s); ws.buf = h.slice(e + 4); ws.drain(); res(ws);
      };
      s.on('data', on);
    });
  }
  drain() {
    for (;;) {
      if (this.buf.length < 2) return;
      const op = this.buf[0] & 0x0f;
      let len = this.buf[1] & 0x7f, off = 2;
      if (len === 126) { if (this.buf.length < 4) return; len = this.buf.readUInt16BE(2); off = 4; }
      else if (len === 127) { if (this.buf.length < 10) return; len = Number(this.buf.readBigUInt64BE(2)); off = 10; }
      if (this.buf.length < off + len) return;
      const p = this.buf.slice(off, off + len);
      this.buf = this.buf.slice(off + len);
      if (op === 0x1) this.onText(p.toString('utf8'));
    }
  }
  send(t) {
    const p = Buffer.from(t, 'utf8'); const m = crypto.randomBytes(4);
    const k = Buffer.from(p.map((b, i) => b ^ m[i & 3]));
    let h;
    if (p.length < 126) h = Buffer.from([0x81, 0x80 | p.length]);
    else if (p.length <= 0xffff) { h = Buffer.alloc(4); h[0] = 0x81; h[1] = 0x80 | 126; h.writeUInt16BE(p.length, 2); }
    else { h = Buffer.alloc(10); h[0] = 0x81; h[1] = 0x80 | 127; h.writeBigUInt64BE(BigInt(p.length), 2); }
    this.s.write(Buffer.concat([h, m, k]));
  }
}

async function main() {
  const token = fs.readFileSync(TOKEN_FILE, 'utf8').trim();
  const ws = await Ws.connect(PORT, token);
  console.log(`connected to the debugger endpoint on 127.0.0.1:${PORT}\n`);

  let id = 0;
  const pending = new Map();
  const breaks = [];
  let onBreak = null;

  ws.onText = (raw) => {
    const m = JSON.parse(raw);
    if (m.id !== undefined && pending.has(m.id)) { pending.get(m.id)(m); pending.delete(m.id); return; }
    if (m.method === 'debugger_state_changed') {
      const p = m.params || {};
      console.log(`  <- ${m.method}: ${p.state} (${p.reason})` +
        (p.function ? ` in ${p.function}` : '') + (p.line !== undefined ? ` line ${p.line + 1}` : ''));
      if (p.state === 'break') { breaks.push(p); if (onBreak) { const f = onBreak; onBreak = null; f(p); } }
    }
  };

  const rpc = (method, params, timeoutMs = 30000) => new Promise((res, rej) => {
    const i = ++id; pending.set(i, res);
    ws.send(JSON.stringify({ jsonrpc: '2.0', id: i, method, params }));
    setTimeout(() => { if (pending.has(i)) { pending.delete(i); rej(new Error(method + ' timed out')); } }, timeoutMs);
  });
  const call = async (n, a) => JSON.parse((await rpc('tools/call', { name: n, arguments: a || {} })).result.content[0].text);
  const waitForBreak = (ms) => new Promise((res, rej) => {
    if (breaks.length) return res(breaks[breaks.length - 1]);
    onBreak = res;
    setTimeout(() => { if (onBreak) { onBreak = null; rej(new Error('no break within ' + ms + 'ms')); } }, ms);
  });

  const init = await rpc('initialize', { protocolVersion: '2024-11-05', capabilities: {}, clientInfo: { name: 'debug-check', version: '1' } });
  check('initialize identifies the debugger endpoint',
    init.result.serverInfo.name === 'Visual Studio Debugger', JSON.stringify(init.result.serverInfo));
  ws.send(JSON.stringify({ jsonrpc: '2.0', method: 'notifications/initialized', params: {} }));

  const tools = (await rpc('tools/list', {})).result.tools.map((t) => t.name);
  check('debug tools are advertised and unfiltered',
    ['debug_set_breakpoint', 'debug_continue', 'debug_locals', 'debug_evaluate'].every((t) => tools.includes(t)),
    tools.join(', '));

  console.log('\n== before starting ==');
  let status = await call('debug_status');
  check('reports no session before starting', status.mode === 'stopped', JSON.stringify(status.mode));

  await call('debug_remove_breakpoints', { all: true });
  const added = await call('debug_set_breakpoint', { file: FILE, line: LINE, condition: CONDITION });
  check('conditional breakpoint is accepted', added.added === true, JSON.stringify(added));

  const list = await call('debug_list_breakpoints');
  check('breakpoint is listed with its condition',
    list.breakpoints.length > 0 && list.breakpoints[0].condition === CONDITION,
    JSON.stringify(list.breakpoints[0]));

  console.log('\n== starting ==');
  const started = await call('debug_start', { mode: 'launch' });
  check('debug_start returns without blocking', started.started === true, JSON.stringify(started));

  console.log('  waiting for the breakpoint to be hit...');
  const hit = await waitForBreak(120000);
  check('the stop arrived as a notification, not by polling', !!hit, `${hit.state} / ${hit.reason}`);
  check('stopped at the requested line', hit.line + 1 === LINE, `line ${hit.line + 1}, expected ${LINE}`);

  console.log('\n== inspection ==');
  const stack = await call('debug_call_stack', { maxFrames: 5 });
  check('call stack is readable', stack.frames.length > 0,
    stack.frames.map((f) => f.function).join(' <- '));

  const locals = await call('debug_locals', { depth: 2 });
  const named = {};
  (locals.arguments || []).concat(locals.locals || []).forEach((v) => { named[v.name] = v; });
  check('arguments and locals are readable', Object.keys(named).length > 0, Object.keys(named).join(', '));

  const target = named['order'];
  check('an object argument is expanded into members', !!(target && target.members && target.members.length),
    target ? (target.members || []).map((m) => `${m.name}=${m.value}`).join(', ') : 'order not found');

  const idMember = target && (target.members || []).find((m) => m.name === 'Id');
  check('the condition really selected the intended iteration',
    !!idMember && CONDITION.includes(idMember.value),
    idMember ? `order.Id = ${idMember.value}, condition was "${CONDITION}"` : 'Id not found');

  const evaluated = await call('debug_evaluate', { expression: 'order.Customer.Score' });
  check('an arbitrary expression evaluates in frame context',
    evaluated.isValid === true, `order.Customer.Score = ${evaluated.value} (${evaluated.type})`);

  const bad = await call('debug_evaluate', { expression: 'thisDoesNotExist' });
  check('an invalid expression is reported, not thrown', bad.isValid === false, JSON.stringify(bad.value));

  console.log('\n== stepping ==');
  breaks.length = 0;
  const stepped = await call('debug_step', { kind: 'over' });
  check('debug_step returns immediately', stepped.resumed === true, JSON.stringify(stepped.kind));
  const afterStep = await waitForBreak(30000);
  check('the step produced a new stop', afterStep.reason === 'step' || afterStep.state === 'break',
    `${afterStep.state} / ${afterStep.reason} line ${afterStep.line + 1}`);

  console.log('\n== outer frame ==');
  const outer = await call('debug_locals', { frameIndex: 1, depth: 1 });
  check('locals of an outer frame are readable', !!outer.frame,
    `frame 1 = ${outer.frame && outer.frame.function}`);

  console.log('\n== finishing ==');
  await call('debug_remove_breakpoints', { all: true });
  const resumed = await call('debug_continue');
  check('execution resumes', resumed.resumed === true, JSON.stringify(resumed.resumed));

  await new Promise((r) => setTimeout(r, 4000));
  const stopped = await call('debug_stop', { detachOnly: false });
  check('the session can be stopped', stopped.stopped === true || stopped.reason === 'No debug session is running.',
    JSON.stringify(stopped));

  console.log(`\n================ ${passes} passed, ${failures} failed ================\n`);
  ws.s.destroy();
  process.exit(failures === 0 ? 0 : 1);
}

main().catch((e) => { console.error('\nFATAL: ' + e.message); process.exit(2); });
