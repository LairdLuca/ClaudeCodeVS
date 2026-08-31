using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeCodeVS.Mcp
{
    /// <summary>
    /// How the diff window ended. The three values map one to one onto the replies the Claude
    /// Code CLI recognises for <c>openDiff</c>, and each one means something different to it:
    /// <list type="bullet">
    /// <item><description><see cref="FileSaved"/>: the user edited and saved the right-hand pane,
    /// so the CLI adopts the saved text instead of what it proposed.</description></item>
    /// <item><description><see cref="TabClosed"/>: the CLI itself asked to close the tab because
    /// the decision was taken in the terminal; it keeps its own proposal.</description></item>
    /// <item><description><see cref="Rejected"/>: the user closed the window, and the file is
    /// left untouched.</description></item>
    /// </list>
    /// </summary>
    internal enum DiffResult
    {
        FileSaved,
        TabClosed,
        Rejected
    }

    internal sealed class DiffOutcome
    {
        public DiffOutcome(DiffResult result, string content)
        {
            Result = result;
            Content = content;
        }

        public DiffResult Result { get; private set; }

        /// <summary>The saved text; only meaningful for <see cref="DiffResult.FileSaved"/>.</summary>
        public string Content { get; private set; }
    }

    /// <summary>
    /// Everything the MCP layer needs from the IDE. Kept free of Visual Studio types so the
    /// protocol can be exercised without loading the shell.
    /// </summary>
    internal interface IIdeBridge
    {
        Task<IList<string>> GetWorkspaceFoldersAsync();

        /// <summary>
        /// Returns one entry per file, shaped as <c>{ uri, diagnostics: [...] }</c>.
        /// A null or empty <paramref name="fileUri"/> means every file with diagnostics.
        /// </summary>
        Task<List<object>> GetDiagnosticsAsync(string fileUri);

        Task<DiffOutcome> OpenDiffAsync(
            string oldFilePath,
            string newFilePath,
            string newFileContents,
            string tabName,
            CancellationToken cancellationToken);

        Task<bool> CloseTabAsync(string tabName);

        Task CloseAllDiffTabsAsync();

        Task<List<object>> GetOpenEditorsAsync();

        /// <summary>The current editor selection, or null when no text editor is active.</summary>
        Task<object> GetCurrentSelectionAsync();

        Task<bool> OpenFileAsync(string filePath, int? startLine, int? endLine);

        Task<bool> SaveDocumentAsync(string filePath);

        Task<bool> IsDocumentDirtyAsync(string filePath);

        void SetPermissionMode(string mode);
    }
}
