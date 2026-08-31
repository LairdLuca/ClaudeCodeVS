# Claude Code for Visual Studio

Lets the [Claude Code](https://claude.com/claude-code) CLI attach to Visual Studio through its
`/ide` command, the same way it attaches to VS Code and the JetBrains IDEs.

Visual Studio 2022 and 2026 are supported (`[17.0,)`).

## What it gives you

| In the terminal | What Visual Studio does |
|---|---|
| `/ide` lists and connects to this instance | Publishes a discovery lock file and serves an MCP endpoint on loopback |
| Claude sees what you have selected | The active editor selection is pushed as you move the caret |
| Claude reads compiler and analyzer errors | The Error List is exposed through the `getDiagnostics` tool |
| Proposed edits appear as a diff | Opens the Visual Studio comparison window; saving the right pane accepts your edited version |
| **Tools > Send Selection to Claude Code** (`Ctrl+Alt+K`) | Inserts a reference to the current file and lines into the Claude prompt |
| Claude sets breakpoints and reads variables | Drives the Visual Studio debugger, on a second MCP endpoint (see below) |
| **Tools > Claude Code: Connection Status** | Shows the port, whether a client is attached, and the folders being advertised |

## How the connection actually works

There is no network discovery. The mechanism is a file on disk:

1. On startup the extension listens on an **ephemeral loopback TCP port** and writes
   `%USERPROFILE%\.claude\ide\<port>.lock`. The port is the **file name**; the body carries the
   process id, the open folders, the transport and a per-session secret:

   ```json
   {
     "pid": 12345,
     "workspaceFolders": ["C:\\Users\\me\\source\\repos\\MyApp"],
     "ideName": "Visual Studio",
     "transport": "ws",
     "runningInWindows": true,
     "authToken": "…"
   }
   ```

2. `/ide` scans that directory and offers the entries it finds.

3. Claude Code opens a WebSocket to `ws://127.0.0.1:<port>` with the subprotocol `mcp` and the
   header `X-Claude-Code-Ide-Authorization: <authToken>`, then speaks JSON-RPC 2.0 over it.

The extension is therefore a **server**, not a client: Claude Code calls into the IDE
(`getDiagnostics`, `openDiff`, `close_tab`, `closeAllDiffTabs`, `set_permission_mode`), while the
IDE pushes `selection_changed` and `at_mentioned` notifications back.

The socket is bound to `127.0.0.1` and every handshake must present the token from the lock file,
which lives under the user profile. A connection without it is refused.

## Auto-connect and the workspace folder rule

Claude Code connects **automatically** only when its working directory is inside one of the
folders advertised in the lock file. The extension advertises the solution directory (or, in Open
Folder mode, the opened folder).

So if the solution is at `C:\Repo\src\App.sln` but you run `claude` from `C:\Repo`, auto-connect
will not fire. Two ways round it:

- run `/ide` explicitly, which lists every IDE regardless of folder matching, or
- add `C:\Repo` to **Tools > Options > Claude Code > General > Additional workspace folders**
  (semicolon separated).

## Building

The Visual Studio extension development workload is *not* required; the build targets come from
NuGet. Paths below assume Visual Studio 2026 Professional; adjust the edition and version for
your install, or use `vswhere` to locate it.

```bash
"C:\Program Files\Microsoft Visual Studio\18\Professional\MSBuild\Current\Bin\MSBuild.exe" \
    ClaudeCodeVS.sln -t:Restore -v:minimal
"C:\Program Files\Microsoft Visual Studio\18\Professional\MSBuild\Current\Bin\MSBuild.exe" \
    ClaudeCodeVS.sln -p:Configuration=Release -v:minimal -nodeReuse:false
```

Output: `src\ClaudeCodeVS\bin\Release\ClaudeCodeVS.vsix`.

Two things worth knowing:

- **`-nodeReuse:false` is not optional if you install straight after building.** MSBuild leaves worker
  processes alive by default, and VSIXInstaller refuses to touch extensions while any of them is
  running, failing with exit code 2004 and a `BlockingProcessesException`.
- The repo carries its own `NuGet.config` with `<clear />`. If the machine-wide config lists an
  authenticated feed, a non-interactive restore hangs forever waiting for credentials rather than
  reporting an error.

## Installing

Close every Visual Studio instance, then double-click the `.vsix`, or:

```bash
"C:\Program Files\Microsoft Visual Studio\18\Professional\Common7\IDE\VSIXInstaller.exe" \
    src\ClaudeCodeVS\bin\Release\ClaudeCodeVS.vsix
```

Reopen Visual Studio, open a solution, then in a terminal:

```bash
cd <the solution folder>
claude
/ide
```

## Checking it without the CLI

Both scripts perform exactly the handshake the CLI performs: same lock file lookup, same
subprotocol, same authorization header. They need nothing but Node.

```bash
node tools/mcp-smoke-test.js                       # prints what each call returns
node tools/live-check.js <solutionFolder> <file>   # asserts on every result
```

`mcp-smoke-test.js` also keeps printing `selection_changed` notifications, so moving the caret in
the editor is a direct check that selection tracking is alive.

`live-check.js` is the thorough one: it opens a file, reads the real Error List, checks the shape
of the diagnostics, opens a diff window and then releases it with `close_tab` while `openDiff` is
still parked, which is the only way to prove requests are dispatched concurrently. Point it at a
project Visual Studio can actually load and analyse — see the note below about this repository's
own solution.

## Debugging

The extension also exposes the Visual Studio debugger: breakpoints, stepping, the call stack,
local variables and arbitrary expression evaluation. That turns "read the code and reason about
it" into "stop the program and look at what it actually holds".

It runs on a **second, separate MCP endpoint**, and that separation is forced rather than chosen.
The Claude Code CLI filters the tools of its built-in `ide` server down to a fixed allow list
(`getDiagnostics` and `executeCode`) before the model ever sees them, so a debugger tool added
there would be invisible however it was written. The debugger endpoint is therefore configured
like any other MCP server, where no such filter applies.

Because it appears in a static configuration, this endpoint uses a **fixed port** (8375 by
default, changeable in the options) and a **token that persists across restarts**, kept in
`%LOCALAPPDATA%\ClaudeCodeVS\debug-token`. Register it once:

```bash
claude mcp add-json --scope local vsdebug "{\"type\":\"ws\",\"url\":\"ws://127.0.0.1:8375\",\"headers\":{\"X-Claude-Code-Ide-Authorization\":\"<token>\"}}"
```

The exact command, token included, is written to the log and to the Output pane every time
Visual Studio starts. Use `--scope local` unless you want it in every project: the endpoint only
answers while Visual Studio is running, so a wider scope means a failed connection attempt in
sessions where it is not.

To see it work, open `samples/DebuggerSample` in Visual Studio and run:

```bash
node tools/debug-check.js <repo>\samples\DebuggerSample\Program.cs 44 "order.Id == 1003"
```

It sets the conditional breakpoint, starts the session, waits for the stop to arrive as a
notification, then reads the stack, the locals and an evaluated expression.

Tools: `debug_status`, `debug_processes`, `debug_start`, `debug_stop`, `debug_set_breakpoint`,
`debug_list_breakpoints`, `debug_remove_breakpoints`, `debug_continue`, `debug_step`,
`debug_pause`, `debug_call_stack`, `debug_locals`, `debug_evaluate`.

### Two things that shape how it must be used

**Nothing waits.** `debug_start`, `debug_continue` and `debug_step` return immediately, and the
stop that follows arrives as a `debugger_state_changed` notification. The automation model does
offer a "wait for the next stop" flag on each of them, but it blocks the caller, and on the UI
thread that means freezing Visual Studio until the debuggee happens to stop.

**A hit breakpoint leaves the triggering request hanging.** If the breakpoint was reached because
of a click in a browser, that page is still waiting for its response and will keep waiting until
execution resumes. Anything driving the application has to fire the action and *not* await it,
then act on the notification. Awaiting the page instead is a deadlock against yourself.

### On a real application

Prefer `debug_start` with `mode: "attach"` when the site is already running: `debug_processes`
finds the host process, and attaching skips the cost of restarting it. The attach path picks the
managed engine explicitly, which is what ticking "Managed" in the Attach dialog does; without it
the native engine can be selected and no managed frame is ever visible.

Use conditions. A breakpoint in a data access helper fires dozens of times for a single page, and
`condition` on `debug_set_breakpoint` is what makes it stop on the one case that matters.

## The log

Everything the extension does is appended to:

```
%LOCALAPPDATA%\ClaudeCodeVS\claude-code-vs.log
```

Same content as the **Claude Code** pane in the Output window, but readable without sitting in
front of the IDE, which is what makes a problem diagnosable without a rebuild cycle.

## Troubleshooting

**`/ide` does not list Visual Studio.** Check `Tools > Claude Code: Connection Status`, then the
log. If the bridge is running, confirm the lock file exists in `%USERPROFILE%\.claude\ide\`.

**It is listed but does not connect automatically.** See the workspace folder rule above.

**No diagnostics come back.** First check that the project is actually loaded and analysed:
diagnostics are produced by Roslyn, and a project Visual Studio cannot load produces none.
**This repository's own solution is exactly that case** — a VSIX project does not load without the
Visual Studio extension development workload, so testing `getDiagnostics` against it will always
report nothing, whatever the extension does. Use any ordinary project instead. The log line
`Error List: N source(s), M row(s)` tells you what the extension is seeing.

Note also that Roslyn only analyses open documents unless full solution analysis is enabled, so a
file nobody has opened has no diagnostics to report.

**The diff window does not appear.** Claude Code only routes edits through the IDE while its
`diffTool` setting is `auto`. Check `/config`.

## Layout

```
src/ClaudeCodeVS/
  Mcp/          transport and protocol, with no dependency on the Visual Studio shell
    WebSocketServer.cs      loopback listener, handshake, token check
    WebSocketConnection.cs  RFC 6455 framing
    McpSession.cs           JSON-RPC 2.0 and the MCP method set
    McpToolCatalog.cs       the tools/list payload
    LockFile.cs             discovery file lifecycle
    Json.cs                 JavaScriptSerializer wrappers
    IIdeBridge.cs           the contract the IDE side implements
  Mcp/
    IMcpToolHost.cs         what one endpoint exposes
    IdeToolHost.cs          the /ide tool set
    DebugToolHost.cs        the debugger tool set
    McpEndpoint.cs          one listening endpoint and its sessions
    DebugTokenStore.cs      the persistent debugger secret
  Ide/          the Visual Studio side, all of it on the UI thread
    VsIdeBridge.cs          workspace, editors, open and save
    DiagnosticsReader.cs    Error List to LSP-shaped diagnostics
    DiffTabManager.cs       comparison window and the accept/reject signals
    SelectionWatcher.cs     selection tracking
    DebuggerBridge.cs       breakpoints, stepping, frames, expression evaluation
  ClaudeCodePackage.cs      startup, shutdown, both endpoints
tools/mcp-smoke-test.js     protocol-level test client
tools/live-check.js         asserting end-to-end check against a running IDE
tools/debug-check.js        asserting end-to-end check of the debugger endpoint
samples/DebuggerSample/     a small program for debug-check.js to stop inside
```

## Licence

MIT. See `LICENSE`.

This is an independent project and is not affiliated with or endorsed by Anthropic or Microsoft.
The discovery and protocol details it implements are not a published API: they were determined by
observing how the CLI behaves, and a future release of Claude Code could change them.

The split matters: `Mcp/` never references a Visual Studio type, which is what keeps the protocol
testable and the IDE-specific code confined to `Ide/`.
