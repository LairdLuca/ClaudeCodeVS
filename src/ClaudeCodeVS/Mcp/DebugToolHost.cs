using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ClaudeCodeVS.Ide;

namespace ClaudeCodeVS.Mcp
{
    /// <summary>
    /// The debugger endpoint. Runs as an ordinary MCP server rather than under the CLI's built-in
    /// <c>ide</c> name, because tools under that name are filtered away before the model sees
    /// them. See <see cref="IMcpToolHost"/>.
    /// </summary>
    internal sealed class DebugToolHost : IMcpToolHost
    {
        private readonly DebuggerBridge _debugger;

        public DebugToolHost(DebuggerBridge debugger)
        {
            _debugger = debugger;
        }

        public string ServerName { get { return "Visual Studio Debugger"; } }

        public string ServerVersion { get { return McpSession.ServerVersion; } }

        public List<object> ListTools()
        {
            return new List<object>
            {
                Tool("debug_status",
                    "Report whether Visual Studio is debugging, and where execution is stopped.",
                    Schema(Json.Obj(), new string[0])),

                Tool("debug_processes",
                    "List local processes that can be attached to. Use it to find the IIS Express or w3wp process hosting the site.",
                    Schema(Json.Obj("filter", Str("Case-insensitive substring of the process name.")), new string[0])),

                Tool("debug_start",
                    "Start debugging. 'launch' is the equivalent of pressing F5 and honours the startup project; 'attach' connects to an already running process and is usually preferable when the site is already up.",
                    Schema(Json.Obj(
                        "mode", Enum2("launch", "attach"),
                        "processName", Str("For attach: substring of the process name, for example iisexpress."),
                        "processId", Int("For attach: exact process id, which wins over processName.")),
                        new string[0])),

                Tool("debug_stop",
                    "End the debug session. Detaching leaves the process running, which is what you want for a site you attached to.",
                    Schema(Json.Obj("detachOnly", Bool("Detach instead of terminating the debuggee.")), new string[0])),

                Tool("debug_set_breakpoint",
                    "Set a breakpoint. A condition is close to mandatory on a busy path: without one a breakpoint in a data helper fires dozens of times for a single page.",
                    Schema(Json.Obj(
                        "file", Str("Full path of the source file."),
                        "line", Int("One-based line number, as shown in the editor."),
                        "condition", Str("Expression that must be true, for example order.Id == 12345.")),
                        new[] { "file", "line" })),

                Tool("debug_list_breakpoints",
                    "List the breakpoints currently set.",
                    Schema(Json.Obj(), new string[0])),

                Tool("debug_remove_breakpoints",
                    "Remove breakpoints by file and line, or all of them.",
                    Schema(Json.Obj(
                        "file", Str("Full path of the source file."),
                        "line", Int("One-based line number."),
                        "all", Bool("Remove every breakpoint.")),
                        new string[0])),

                Tool("debug_continue",
                    "Resume execution. Returns immediately; the next stop arrives as a debugger_state_changed notification.",
                    Schema(Json.Obj(), new string[0])),

                Tool("debug_step",
                    "Step one statement. Returns immediately; the stop arrives as a notification.",
                    Schema(Json.Obj("kind", Enum3("over", "into", "out")), new string[0])),

                Tool("debug_pause",
                    "Break into a running debuggee.",
                    Schema(Json.Obj(), new string[0])),

                Tool("debug_call_stack",
                    "The call stack of the current thread while stopped.",
                    Schema(Json.Obj("maxFrames", Int("Default 30.")), new string[0])),

                Tool("debug_locals",
                    "Arguments and local variables of a stack frame while stopped.",
                    Schema(Json.Obj(
                        "frameIndex", Int("0 is the innermost frame, the default. Selecting another frame also retargets debug_evaluate."),
                        "depth", Int("How far to expand object members. Default 1.")),
                        new string[0])),

                Tool("debug_evaluate",
                    "Evaluate an expression in the context of the selected stack frame, exactly as the Watch window would.",
                    Schema(Json.Obj(
                        "expression", Str("For example order.Customer.Status."),
                        "depth", Int("How far to expand object members. Default 1.")),
                        new[] { "expression" }))
            };
        }

        public async Task<object> CallToolAsync(
            string name,
            Dictionary<string, object> arguments,
            CancellationToken cancellationToken)
        {
            try
            {
                switch (name)
                {
                    case "debug_status":
                        return Result(await _debugger.StatusAsync().ConfigureAwait(false));

                    case "debug_processes":
                        return Result(await _debugger
                            .ProcessesAsync(Json.GetString(arguments, "filter"))
                            .ConfigureAwait(false));

                    case "debug_start":
                        return Result(await _debugger.StartAsync(
                            Json.GetString(arguments, "mode") ?? "launch",
                            Json.GetString(arguments, "processName"),
                            Json.GetInt(arguments, "processId")).ConfigureAwait(false));

                    case "debug_stop":
                        return Result(await _debugger
                            .StopAsync(Json.GetBool(arguments, "detachOnly") ?? false)
                            .ConfigureAwait(false));

                    case "debug_set_breakpoint":
                        {
                            var line = Json.GetInt(arguments, "line");
                            return Result(await _debugger.AddBreakpointAsync(
                                Json.GetString(arguments, "file"),
                                line ?? 0,
                                Json.GetString(arguments, "condition")).ConfigureAwait(false));
                        }

                    case "debug_list_breakpoints":
                        return Result(await _debugger.ListBreakpointsAsync().ConfigureAwait(false));

                    case "debug_remove_breakpoints":
                        return Result(await _debugger.RemoveBreakpointsAsync(
                            Json.GetString(arguments, "file"),
                            Json.GetInt(arguments, "line"),
                            Json.GetBool(arguments, "all") ?? false).ConfigureAwait(false));

                    case "debug_continue":
                        return Result(await _debugger.ResumeAsync("continue").ConfigureAwait(false));

                    case "debug_step":
                        return Result(await _debugger
                            .ResumeAsync(Json.GetString(arguments, "kind") ?? "over")
                            .ConfigureAwait(false));

                    case "debug_pause":
                        return Result(await _debugger.PauseAsync().ConfigureAwait(false));

                    case "debug_call_stack":
                        return Result(await _debugger
                            .CallStackAsync(Json.GetInt(arguments, "maxFrames") ?? 30)
                            .ConfigureAwait(false));

                    case "debug_locals":
                        return Result(await _debugger.LocalsAsync(
                            Json.GetInt(arguments, "frameIndex") ?? 0,
                            Json.GetInt(arguments, "depth") ?? 1).ConfigureAwait(false));

                    case "debug_evaluate":
                        return Result(await _debugger.EvaluateAsync(
                            Json.GetString(arguments, "expression"),
                            Json.GetInt(arguments, "depth") ?? 1).ConfigureAwait(false));

                    default:
                        return McpSession.ToolError("Unknown tool: " + name);
                }
            }
            catch (Exception ex)
            {
                Log.Error("Debug tool '" + name + "' failed", ex);
                return McpSession.ToolError(ex.Message);
            }
        }

        private static Dictionary<string, object> Result(object payload)
        {
            return McpSession.Content(Json.Serialize(payload));
        }

        private static Dictionary<string, object> Tool(string name, string description, object inputSchema)
        {
            return Json.Obj("name", name, "description", description, "inputSchema", inputSchema);
        }

        private static Dictionary<string, object> Schema(object properties, string[] required)
        {
            return Json.Obj("type", "object", "properties", properties, "required", required);
        }

        private static Dictionary<string, object> Str(string description)
        {
            return Json.Obj("type", "string", "description", description);
        }

        private static Dictionary<string, object> Int(string description)
        {
            return Json.Obj("type", "integer", "description", description);
        }

        private static Dictionary<string, object> Bool(string description)
        {
            return Json.Obj("type", "boolean", "description", description);
        }

        private static Dictionary<string, object> Enum2(string first, string second)
        {
            return Json.Obj("type", "string", "enum", new[] { first, second });
        }

        private static Dictionary<string, object> Enum3(string first, string second, string third)
        {
            return Json.Obj("type", "string", "enum", new[] { first, second, third });
        }
    }
}
