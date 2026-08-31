using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeCodeVS.Mcp
{
    /// <summary>
    /// The endpoint the Claude Code CLI attaches to with <c>/ide</c>: editor state, diagnostics
    /// and the diff flow.
    /// </summary>
    internal sealed class IdeToolHost : IMcpToolHost
    {
        private readonly IIdeBridge _bridge;

        public IdeToolHost(IIdeBridge bridge)
        {
            _bridge = bridge;
        }

        public string ServerName { get { return "Claude Code Visual Studio Extension"; } }

        public string ServerVersion { get { return McpSession.ServerVersion; } }

        public List<object> ListTools()
        {
            return McpToolCatalog.Build();
        }

        public async Task<object> CallToolAsync(
            string name,
            Dictionary<string, object> arguments,
            CancellationToken cancellationToken)
        {
            switch (name)
            {
                case "getDiagnostics":
                    {
                        var diagnostics = await _bridge
                            .GetDiagnosticsAsync(Json.GetString(arguments, "uri"))
                            .ConfigureAwait(false);
                        return McpSession.Content(Json.Serialize(diagnostics));
                    }

                case "openDiff":
                    {
                        var outcome = await _bridge.OpenDiffAsync(
                            Json.GetString(arguments, "old_file_path"),
                            Json.GetString(arguments, "new_file_path"),
                            Json.GetString(arguments, "new_file_contents"),
                            Json.GetString(arguments, "tab_name"),
                            cancellationToken).ConfigureAwait(false);

                        switch (outcome.Result)
                        {
                            case DiffResult.FileSaved:
                                return McpSession.Content("FILE_SAVED", outcome.Content ?? string.Empty);
                            case DiffResult.TabClosed:
                                return McpSession.Content("TAB_CLOSED");
                            default:
                                return McpSession.Content("DIFF_REJECTED");
                        }
                    }

                case "close_tab":
                    await _bridge.CloseTabAsync(Json.GetString(arguments, "tab_name")).ConfigureAwait(false);
                    return McpSession.Content("TAB_CLOSED");

                case "closeAllDiffTabs":
                    await _bridge.CloseAllDiffTabsAsync().ConfigureAwait(false);
                    return McpSession.Content("CLOSED_ALL_DIFF_TABS");

                case "getWorkspaceFolders":
                    {
                        var folders = await _bridge.GetWorkspaceFoldersAsync().ConfigureAwait(false);
                        return McpSession.Content(Json.Serialize(Json.Obj("folders", folders)));
                    }

                case "getOpenEditors":
                    {
                        var editors = await _bridge.GetOpenEditorsAsync().ConfigureAwait(false);
                        return McpSession.Content(Json.Serialize(Json.Obj("tabs", editors)));
                    }

                case "getCurrentSelection":
                case "getLatestSelection":
                    {
                        var selection = await _bridge.GetCurrentSelectionAsync().ConfigureAwait(false);
                        return McpSession.Content(Json.Serialize(selection ?? Json.Obj("success", false)));
                    }

                case "openFile":
                    {
                        var opened = await _bridge.OpenFileAsync(
                            Json.GetString(arguments, "filePath"),
                            Json.GetInt(arguments, "startLine"),
                            Json.GetInt(arguments, "endLine")).ConfigureAwait(false);
                        return McpSession.Content(opened ? "FILE_OPENED" : "FILE_NOT_FOUND");
                    }

                case "saveDocument":
                    {
                        var saved = await _bridge
                            .SaveDocumentAsync(Json.GetString(arguments, "filePath"))
                            .ConfigureAwait(false);
                        return McpSession.Content(saved ? "DOCUMENT_SAVED" : "DOCUMENT_NOT_OPEN");
                    }

                case "checkDocumentDirty":
                    {
                        var path = Json.GetString(arguments, "filePath");
                        var dirty = await _bridge.IsDocumentDirtyAsync(path).ConfigureAwait(false);
                        return McpSession.Content(Json.Serialize(Json.Obj("filePath", path, "isDirty", dirty)));
                    }

                case "set_permission_mode":
                    _bridge.SetPermissionMode(Json.GetString(arguments, "mode"));
                    return McpSession.Content("PERMISSION_MODE_SET");

                default:
                    return McpSession.ToolError("Unknown tool: " + name);
            }
        }
    }
}
