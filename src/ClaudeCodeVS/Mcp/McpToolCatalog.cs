using System.Collections.Generic;

namespace ClaudeCodeVS.Mcp
{
    /// <summary>
    /// The tool list advertised on <c>tools/list</c>.
    ///
    /// Only <c>getDiagnostics</c> is ever offered to the model: the Claude Code CLI filters
    /// <c>mcp__ide__*</c> down to a fixed allow list. The others are called by the CLI itself,
    /// as part of the edit and permission flow, and are advertised so that the handshake
    /// describes the server honestly.
    /// </summary>
    internal static class McpToolCatalog
    {
        public static List<object> Build()
        {
            return new List<object>
            {
                Tool(
                    "getDiagnostics",
                    "Get language diagnostics (errors, warnings, messages) from Visual Studio.",
                    Json.Obj(
                        "type", "object",
                        "properties", Json.Obj(
                            "uri", Json.Obj(
                                "type", "string",
                                "description", "Optional file:// URI. Omit to get diagnostics for every file.")),
                        "required", new string[0])),

                Tool(
                    "openDiff",
                    "Open a diff view comparing a file with proposed contents. Blocks until the user saves the proposal, the tab is closed by the caller, or the window is dismissed.",
                    Json.Obj(
                        "type", "object",
                        "properties", Json.Obj(
                            "old_file_path", Json.Obj("type", "string"),
                            "new_file_path", Json.Obj("type", "string"),
                            "new_file_contents", Json.Obj("type", "string"),
                            "tab_name", Json.Obj("type", "string")),
                        "required", new[] { "old_file_path", "new_file_path", "new_file_contents", "tab_name" })),

                Tool(
                    "close_tab",
                    "Close a diff tab previously opened by openDiff.",
                    Json.Obj(
                        "type", "object",
                        "properties", Json.Obj("tab_name", Json.Obj("type", "string")),
                        "required", new[] { "tab_name" })),

                Tool(
                    "closeAllDiffTabs",
                    "Close every diff tab opened by this integration.",
                    Json.Obj("type", "object", "properties", Json.Obj(), "required", new string[0])),

                Tool(
                    "getWorkspaceFolders",
                    "List the root folders currently open in Visual Studio.",
                    Json.Obj("type", "object", "properties", Json.Obj(), "required", new string[0])),

                Tool(
                    "getOpenEditors",
                    "List the documents currently open in the editor.",
                    Json.Obj("type", "object", "properties", Json.Obj(), "required", new string[0])),

                Tool(
                    "getCurrentSelection",
                    "Get the text currently selected in the active editor.",
                    Json.Obj("type", "object", "properties", Json.Obj(), "required", new string[0])),

                Tool(
                    "openFile",
                    "Open a file in the editor and optionally select a line range.",
                    Json.Obj(
                        "type", "object",
                        "properties", Json.Obj(
                            "filePath", Json.Obj("type", "string"),
                            "startLine", Json.Obj("type", "integer"),
                            "endLine", Json.Obj("type", "integer")),
                        "required", new[] { "filePath" })),

                Tool(
                    "saveDocument",
                    "Save an open document.",
                    Json.Obj(
                        "type", "object",
                        "properties", Json.Obj("filePath", Json.Obj("type", "string")),
                        "required", new[] { "filePath" })),

                Tool(
                    "checkDocumentDirty",
                    "Report whether an open document has unsaved changes.",
                    Json.Obj(
                        "type", "object",
                        "properties", Json.Obj("filePath", Json.Obj("type", "string")),
                        "required", new[] { "filePath" })),

                Tool(
                    "set_permission_mode",
                    "Informs the IDE which permission mode the CLI is running in.",
                    Json.Obj(
                        "type", "object",
                        "properties", Json.Obj("mode", Json.Obj("type", "string")),
                        "required", new[] { "mode" }))
            };
        }

        private static Dictionary<string, object> Tool(string name, string description, object inputSchema)
        {
            return Json.Obj(
                "name", name,
                "description", description,
                "inputSchema", inputSchema);
        }
    }
}
