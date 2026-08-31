using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using ClaudeCodeVS.Mcp;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;

namespace ClaudeCodeVS.Ide
{
    /// <summary>
    /// Drives the Visual Studio debugger through the automation model.
    ///
    /// Two decisions shape everything here.
    ///
    /// First, execution commands are always issued without waiting. Every one of them accepts a
    /// "wait for break or end" flag, and setting it blocks the caller until the debuggee stops
    /// next, which on the UI thread means freezing Visual Studio for as long as the request takes
    /// to arrive. Instead the commands return at once and a <c>debugger_state_changed</c>
    /// notification reports the stop when it happens.
    ///
    /// Second, that notification is not a nicety. When a breakpoint is hit the HTTP request that
    /// triggered it is left hanging, so a caller that clicked something in a browser and then
    /// waited for the page would deadlock against itself. The intended shape is: fire the click,
    /// do not wait for it, and act on the notification.
    /// </summary>
    internal sealed class DebuggerBridge : IDisposable
    {
        private const int DefaultEvaluationTimeoutMs = 5000;
        private const int MaxExpressionNodes = 250;
        private const int MaxMembersPerLevel = 60;

        private readonly JoinableTaskFactory _joinableTaskFactory;

        // These must be fields. Automation event objects are held only by the caller, and a
        // local would be collected, silently stopping every notification.
        private DebuggerEvents _debuggerEvents;
        private _dispDebuggerEvents_OnEnterBreakModeEventHandler _onEnterBreakMode;
        private _dispDebuggerEvents_OnEnterRunModeEventHandler _onEnterRunMode;
        private _dispDebuggerEvents_OnEnterDesignModeEventHandler _onEnterDesignMode;
        private _dispDebuggerEvents_OnExceptionNotHandledEventHandler _onExceptionNotHandled;

        private bool _disposed;

        public DebuggerBridge(JoinableTaskFactory joinableTaskFactory)
        {
            _joinableTaskFactory = joinableTaskFactory;
        }

        /// <summary>Raised with the payload of a <c>debugger_state_changed</c> notification.</summary>
        public event Action<object> StateChanged;

        /// <summary>Must be called on the UI thread.</summary>
        public void Initialize()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                var dte = Package.GetGlobalService(typeof(SDTE)) as DTE2;
                if (dte == null)
                {
                    Log.Error("The automation model is unavailable; debugger control is off.", null);
                    return;
                }

                _debuggerEvents = dte.Events.DebuggerEvents;

                _onEnterBreakMode = OnEnterBreakMode;
                _onEnterRunMode = OnEnterRunMode;
                _onEnterDesignMode = OnEnterDesignMode;
                _onExceptionNotHandled = OnExceptionNotHandled;

                _debuggerEvents.OnEnterBreakMode += _onEnterBreakMode;
                _debuggerEvents.OnEnterRunMode += _onEnterRunMode;
                _debuggerEvents.OnEnterDesignMode += _onEnterDesignMode;
                _debuggerEvents.OnExceptionNotHandled += _onExceptionNotHandled;

                Log.Info("Debugger control ready.");
            }
            catch (Exception ex)
            {
                Log.Error("Could not subscribe to the debugger events", ex);
            }
        }

        private void OnEnterBreakMode(dbgEventReason reason, ref dbgExecutionAction executionAction)
        {
            // Leaving the action at its default keeps Visual Studio behaving exactly as it would
            // without this extension attached.
            executionAction = dbgExecutionAction.dbgExecutionActionDefault;
            Publish("break", DescribeReason(reason));
        }

        private void OnEnterRunMode(dbgEventReason reason)
        {
            Publish("running", DescribeReason(reason));
        }

        private void OnEnterDesignMode(dbgEventReason reason)
        {
            Publish("stopped", DescribeReason(reason));
        }

        private void OnExceptionNotHandled(
            string exceptionType, string name, int code, string description, ref dbgExceptionAction exceptionAction)
        {
            exceptionAction = dbgExceptionAction.dbgExceptionActionBreak;
            Publish("exception", (exceptionType ?? name) + ": " + description);
        }

        private void Publish(string state, string reason)
        {
            var handler = StateChanged;
            if (handler == null) return;

            try
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                handler(BuildState(state, reason));
            }
            catch (Exception ex)
            {
                Log.Error("Could not publish the debugger state", ex);
            }
        }

        /// <summary>Must be called on the UI thread.</summary>
        private object BuildState(string state, string reason)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var payload = Json.Obj("state", state, "reason", reason ?? string.Empty);

            try
            {
                var dte = Package.GetGlobalService(typeof(SDTE)) as DTE2;
                if (dte == null) return payload;

                var debugger = dte.Debugger;
                payload["mode"] = DescribeMode(debugger.CurrentMode);

                if (debugger.CurrentMode != dbgDebugMode.dbgBreakMode) return payload;

                var frame = debugger.CurrentStackFrame;
                if (frame != null)
                {
                    payload["function"] = SafeString(() => frame.FunctionName);
                    payload["module"] = SafeString(() => frame.Module);
                }

                // The automation model does not expose the file and line of a frame. Visual
                // Studio navigates to the break location, so the active document is where we are.
                var location = SelectionWatcher.Capture(includeText: false);
                if (location != null)
                {
                    payload["file"] = location.FilePath;
                    payload["line"] = location.StartLine;
                }
            }
            catch (Exception ex)
            {
                Log.Error("Could not describe the debugger state", ex);
            }

            return payload;
        }

        private Task<T> OnUiThreadAsync<T>(Func<DTE2, T> work)
        {
            return _joinableTaskFactory.RunAsync(async delegate
            {
                await _joinableTaskFactory.SwitchToMainThreadAsync();

                var dte = Package.GetGlobalService(typeof(SDTE)) as DTE2;
                if (dte == null) throw new InvalidOperationException("The automation model is unavailable.");

                return work(dte);
            }).Task;
        }

        public Task<object> StatusAsync()
        {
            return OnUiThreadAsync<object>(dte =>
            {
                var debugger = dte.Debugger;
                var processes = new List<object>();

                try
                {
                    foreach (EnvDTE.Process process in debugger.DebuggedProcesses)
                    {
                        processes.Add(Json.Obj("processId", process.ProcessID, "name", process.Name));
                    }
                }
                catch (Exception)
                {
                    // No session, or the collection changed underneath.
                }

                var state = BuildState(DescribeMode(debugger.CurrentMode), "status query");
                var payload = state as Dictionary<string, object> ?? new Dictionary<string, object>();
                payload["debuggedProcesses"] = processes;
                payload["breakpointCount"] = SafeInt(() => debugger.Breakpoints.Count);
                return payload;
            });
        }

        public Task<object> StartAsync(string mode, string processName, int? processId)
        {
            return OnUiThreadAsync<object>(dte =>
            {
                var debugger = dte.Debugger;

                if (string.Equals(mode, "attach", StringComparison.OrdinalIgnoreCase))
                {
                    return Attach(debugger, processName, processId);
                }

                if (debugger.CurrentMode != dbgDebugMode.dbgDesignMode)
                {
                    return Json.Obj("started", false, "reason", "A debug session is already running.");
                }

                // The literal equivalent of pressing F5, so it honours the startup project and
                // its launch settings rather than second-guessing them.
                dte.ExecuteCommand("Debug.Start", string.Empty);
                return Json.Obj("started", true, "mode", "launch");
            });
        }

        private static object Attach(Debugger debugger, string processName, int? processId)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var candidates = new List<object>();
            EnvDTE.Process match = null;

            foreach (EnvDTE.Process process in debugger.LocalProcesses)
            {
                string name;
                int id;
                try
                {
                    name = process.Name;
                    id = process.ProcessID;
                }
                catch (Exception)
                {
                    continue;
                }

                var shortName = System.IO.Path.GetFileName(name ?? string.Empty);
                candidates.Add(Json.Obj("processId", id, "name", shortName, "path", name));

                if (processId.HasValue)
                {
                    if (id == processId.Value) match = process;
                }
                else if (!string.IsNullOrEmpty(processName) &&
                    shortName.IndexOf(processName, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    if (match == null) match = process;
                }
            }

            if (match == null)
            {
                return Json.Obj(
                    "started", false,
                    "reason", "No matching process. Pass processId, or a name that matches one of these.",
                    "candidates", candidates);
            }

            // Choosing the engine explicitly is what the Attach dialog does when the managed
            // engine is ticked; without it the native engine can be picked and no managed frame
            // is ever visible.
            try
            {
                var debugger2 = debugger as Debugger2;
                var process2 = match as Process2;
                if (debugger2 != null && process2 != null)
                {
                    var transport = debugger2.Transports.Item("Default");
                    var engine = FindManagedEngine(transport);
                    if (engine != null)
                    {
                        process2.Attach2(engine);
                        return Json.Obj("started", true, "mode", "attach",
                            "processId", match.ProcessID, "engine", engine.Name);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("Attaching with an explicit engine failed; falling back", ex);
            }

            match.Attach();
            return Json.Obj("started", true, "mode", "attach", "processId", match.ProcessID, "engine", "default");
        }

        private static Engine FindManagedEngine(Transport transport)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            Engine fallback = null;
            foreach (Engine engine in transport.Engines)
            {
                var name = engine.Name ?? string.Empty;
                if (name.IndexOf("Managed", StringComparison.OrdinalIgnoreCase) < 0) continue;

                // Prefer the engine that names the .NET Framework versions: attaching to a web
                // application hosted by IIS Express needs that one, not the .NET Core engine.
                if (name.IndexOf("4.", StringComparison.Ordinal) >= 0) return engine;
                if (fallback == null) fallback = engine;
            }

            return fallback;
        }

        public Task<object> StopAsync(bool detachOnly)
        {
            return OnUiThreadAsync<object>(dte =>
            {
                var debugger = dte.Debugger;
                if (debugger.CurrentMode == dbgDebugMode.dbgDesignMode)
                {
                    return Json.Obj("stopped", false, "reason", "No debug session is running.");
                }

                if (detachOnly)
                {
                    debugger.DetachAll();
                    return Json.Obj("stopped", true, "mode", "detach");
                }

                debugger.Stop(WaitForDesignMode: false);
                return Json.Obj("stopped", true, "mode", "stop");
            });
        }

        public Task<object> AddBreakpointAsync(string file, int line, string condition)
        {
            return OnUiThreadAsync<object>(dte =>
            {
                if (string.IsNullOrEmpty(file)) return Json.Obj("added", false, "reason", "file is required");
                if (line < 1) return Json.Obj("added", false, "reason", "line must be 1 or greater");

                var before = SafeInt(() => dte.Debugger.Breakpoints.Count);

                dte.Debugger.Breakpoints.Add(
                    Function: string.Empty,
                    File: file,
                    Line: line,
                    Column: 1,
                    Condition: condition ?? string.Empty,
                    ConditionType: dbgBreakpointConditionType.dbgBreakpointConditionTypeWhenTrue,
                    Language: string.Empty,
                    Data: string.Empty,
                    DataCount: 0,
                    Address: string.Empty,
                    HitCount: 0,
                    HitCountType: dbgHitCountType.dbgHitCountTypeNone);

                var after = SafeInt(() => dte.Debugger.Breakpoints.Count);

                // A condition is close to mandatory on a busy data access path: without one, a
                // breakpoint in a helper fires dozens of times for a single page.
                return Json.Obj(
                    "added", after > before,
                    "file", file,
                    "line", line,
                    "condition", condition ?? string.Empty,
                    "totalBreakpoints", after);
            });
        }

        public Task<object> ListBreakpointsAsync()
        {
            return OnUiThreadAsync<object>(dte =>
            {
                var list = new List<object>();
                foreach (Breakpoint breakpoint in dte.Debugger.Breakpoints)
                {
                    list.Add(Json.Obj(
                        "file", SafeString(() => breakpoint.File),
                        "line", SafeInt(() => breakpoint.FileLine),
                        "condition", SafeString(() => breakpoint.Condition),
                        "enabled", SafeBool(() => breakpoint.Enabled),
                        "name", SafeString(() => breakpoint.Name)));
                }

                return Json.Obj("breakpoints", list);
            });
        }

        public Task<object> RemoveBreakpointsAsync(string file, int? line, bool all)
        {
            return OnUiThreadAsync<object>(dte =>
            {
                var doomed = new List<Breakpoint>();

                foreach (Breakpoint breakpoint in dte.Debugger.Breakpoints)
                {
                    if (all)
                    {
                        doomed.Add(breakpoint);
                        continue;
                    }

                    var breakpointFile = SafeString(() => breakpoint.File);
                    if (string.IsNullOrEmpty(file) ||
                        !string.Equals(breakpointFile, file, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (line.HasValue && SafeInt(() => breakpoint.FileLine) != line.Value) continue;
                    doomed.Add(breakpoint);
                }

                foreach (var breakpoint in doomed)
                {
                    try { breakpoint.Delete(); } catch (Exception) { }
                }

                return Json.Obj("removed", doomed.Count, "remaining", SafeInt(() => dte.Debugger.Breakpoints.Count));
            });
        }

        public Task<object> ResumeAsync(string kind)
        {
            return OnUiThreadAsync<object>(dte =>
            {
                var debugger = dte.Debugger;
                if (debugger.CurrentMode != dbgDebugMode.dbgBreakMode)
                {
                    return Json.Obj("resumed", false, "reason", "The debuggee is not stopped.",
                        "mode", DescribeMode(debugger.CurrentMode));
                }

                // Never wait: see the note at the top of this class.
                switch ((kind ?? "continue").ToLowerInvariant())
                {
                    case "over": debugger.StepOver(WaitForBreakOrEnd: false); break;
                    case "into": debugger.StepInto(WaitForBreakOrEnd: false); break;
                    case "out": debugger.StepOut(WaitForBreakOrEnd: false); break;
                    default: debugger.Go(WaitForBreakOrEnd: false); break;
                }

                return Json.Obj("resumed", true, "kind", kind ?? "continue",
                    "note", "The next stop arrives as a debugger_state_changed notification.");
            });
        }

        public Task<object> PauseAsync()
        {
            return OnUiThreadAsync<object>(dte =>
            {
                var debugger = dte.Debugger;
                if (debugger.CurrentMode != dbgDebugMode.dbgRunMode)
                {
                    return Json.Obj("paused", false, "mode", DescribeMode(debugger.CurrentMode));
                }

                debugger.Break(WaitForBreakMode: false);
                return Json.Obj("paused", true);
            });
        }

        public Task<object> CallStackAsync(int maxFrames)
        {
            return OnUiThreadAsync<object>(dte =>
            {
                var debugger = dte.Debugger;
                if (debugger.CurrentMode != dbgDebugMode.dbgBreakMode)
                {
                    return Json.Obj("frames", new object[0], "reason", "The debuggee is not stopped.");
                }

                var frames = new List<object>();
                int index = 0;

                foreach (StackFrame frame in debugger.CurrentThread.StackFrames)
                {
                    frames.Add(Json.Obj(
                        "index", index,
                        "function", SafeString(() => frame.FunctionName),
                        "module", SafeString(() => frame.Module),
                        "language", SafeString(() => frame.Language)));

                    index++;
                    if (maxFrames > 0 && frames.Count >= maxFrames) break;
                }

                return Json.Obj("frames", frames, "threadId", SafeInt(() => debugger.CurrentThread.ID));
            });
        }

        public Task<object> LocalsAsync(int frameIndex, int depth)
        {
            return OnUiThreadAsync<object>(dte =>
            {
                var debugger = dte.Debugger;
                if (debugger.CurrentMode != dbgDebugMode.dbgBreakMode)
                {
                    return Json.Obj("locals", new object[0], "reason", "The debuggee is not stopped.");
                }

                var frame = SelectFrame(debugger, frameIndex);
                if (frame == null) return Json.Obj("locals", new object[0], "reason", "No such frame.");

                int budget = MaxExpressionNodes;
                var arguments = Describe(frame.Arguments, depth, ref budget);
                var locals = Describe(frame.Locals, depth, ref budget);

                return Json.Obj(
                    "frame", Json.Obj("index", frameIndex, "function", SafeString(() => frame.FunctionName)),
                    "arguments", arguments,
                    "locals", locals,
                    "truncated", budget <= 0);
            });
        }

        public Task<object> EvaluateAsync(string expression, int depth)
        {
            return OnUiThreadAsync<object>(dte =>
            {
                var debugger = dte.Debugger;
                if (debugger.CurrentMode != dbgDebugMode.dbgBreakMode)
                {
                    return Json.Obj("isValid", false, "reason", "The debuggee is not stopped.");
                }

                if (string.IsNullOrWhiteSpace(expression))
                {
                    return Json.Obj("isValid", false, "reason", "expression is required");
                }

                var evaluated = debugger.GetExpression(expression, true, DefaultEvaluationTimeoutMs);
                int budget = MaxExpressionNodes;
                return DescribeOne(evaluated, depth, ref budget);
            });
        }

        public Task<object> ProcessesAsync(string filter)
        {
            return OnUiThreadAsync<object>(dte =>
            {
                var processes = new List<object>();

                foreach (EnvDTE.Process process in dte.Debugger.LocalProcesses)
                {
                    string path;
                    int id;
                    try
                    {
                        path = process.Name;
                        id = process.ProcessID;
                    }
                    catch (Exception)
                    {
                        continue;
                    }

                    var name = System.IO.Path.GetFileName(path ?? string.Empty);
                    if (!string.IsNullOrEmpty(filter) &&
                        name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }

                    processes.Add(Json.Obj("processId", id, "name", name, "path", path));
                }

                return Json.Obj("processes", processes);
            });
        }

        private static StackFrame SelectFrame(Debugger debugger, int frameIndex)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (frameIndex <= 0) return debugger.CurrentStackFrame;

            int index = 0;
            foreach (StackFrame frame in debugger.CurrentThread.StackFrames)
            {
                if (index == frameIndex)
                {
                    // Selecting the frame is what makes locals and expression evaluation resolve
                    // against it rather than against the innermost one.
                    debugger.CurrentStackFrame = frame;
                    return frame;
                }

                index++;
            }

            return null;
        }

        private static List<object> Describe(Expressions expressions, int depth, ref int budget)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var result = new List<object>();
            if (expressions == null) return result;

            foreach (Expression expression in expressions)
            {
                if (budget <= 0 || result.Count >= MaxMembersPerLevel) break;
                result.Add(DescribeOne(expression, depth, ref budget));
            }

            return result;
        }

        private static Dictionary<string, object> DescribeOne(Expression expression, int depth, ref int budget)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            budget--;

            var described = Json.Obj(
                "name", SafeString(() => expression.Name),
                "value", SafeString(() => expression.Value),
                "type", SafeString(() => expression.Type),
                "isValid", SafeBool(() => expression.IsValidValue));

            if (depth <= 0 || budget <= 0) return described;

            try
            {
                var members = expression.DataMembers;
                if (members == null || members.Count == 0) return described;

                var children = new List<object>();
                foreach (Expression member in members)
                {
                    if (budget <= 0 || children.Count >= MaxMembersPerLevel) break;
                    children.Add(DescribeOne(member, depth - 1, ref budget));
                }

                if (children.Count > 0) described["members"] = children;
            }
            catch (Exception)
            {
                // Expanding a value can fail or time out; the value itself is still useful.
            }

            return described;
        }

        private static string DescribeMode(dbgDebugMode mode)
        {
            switch (mode)
            {
                case dbgDebugMode.dbgBreakMode: return "break";
                case dbgDebugMode.dbgRunMode: return "running";
                default: return "stopped";
            }
        }

        private static string DescribeReason(dbgEventReason reason)
        {
            switch (reason)
            {
                case dbgEventReason.dbgEventReasonBreakpoint: return "breakpoint";
                case dbgEventReason.dbgEventReasonStep: return "step";
                case dbgEventReason.dbgEventReasonExceptionThrown: return "exception thrown";
                case dbgEventReason.dbgEventReasonExceptionNotHandled: return "unhandled exception";
                case dbgEventReason.dbgEventReasonUserBreak: return "user break";
                case dbgEventReason.dbgEventReasonLaunchProgram: return "program launched";
                case dbgEventReason.dbgEventReasonAttachProgram: return "attached";
                case dbgEventReason.dbgEventReasonDetachProgram: return "detached";
                case dbgEventReason.dbgEventReasonEndProgram: return "program ended";
                case dbgEventReason.dbgEventReasonStopDebugging: return "debugging stopped";
                default: return reason.ToString();
            }
        }

        private static string SafeString(Func<string> read)
        {
            try { return read() ?? string.Empty; } catch (Exception) { return string.Empty; }
        }

        private static int SafeInt(Func<int> read)
        {
            try { return read(); } catch (Exception) { return 0; }
        }

        private static bool SafeBool(Func<bool> read)
        {
            try { return read(); } catch (Exception) { return false; }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _joinableTaskFactory.Run(async delegate
            {
                await _joinableTaskFactory.SwitchToMainThreadAsync();

                try
                {
                    if (_debuggerEvents != null)
                    {
                        if (_onEnterBreakMode != null) _debuggerEvents.OnEnterBreakMode -= _onEnterBreakMode;
                        if (_onEnterRunMode != null) _debuggerEvents.OnEnterRunMode -= _onEnterRunMode;
                        if (_onEnterDesignMode != null) _debuggerEvents.OnEnterDesignMode -= _onEnterDesignMode;
                        if (_onExceptionNotHandled != null) _debuggerEvents.OnExceptionNotHandled -= _onExceptionNotHandled;
                    }
                }
                catch (Exception)
                {
                    // Visual Studio is going down anyway.
                }

                _debuggerEvents = null;
            });
        }
    }
}
