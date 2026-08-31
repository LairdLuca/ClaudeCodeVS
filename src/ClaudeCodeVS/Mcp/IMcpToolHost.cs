using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeCodeVS.Mcp
{
    /// <summary>
    /// The set of tools one MCP endpoint exposes.
    ///
    /// There are two endpoints, and they are separate for a reason that is not stylistic. The
    /// Claude Code CLI filters the tools of its built-in <c>ide</c> server down to a fixed allow
    /// list (<c>getDiagnostics</c> and <c>executeCode</c>) before the model ever sees them, so a
    /// debugger tool added there would be invisible no matter how it was written. Debugging
    /// therefore lives on its own endpoint, configured as an ordinary MCP server, where no such
    /// filter applies.
    /// </summary>
    internal interface IMcpToolHost
    {
        string ServerName { get; }

        string ServerVersion { get; }

        List<object> ListTools();

        Task<object> CallToolAsync(
            string name,
            Dictionary<string, object> arguments,
            CancellationToken cancellationToken);
    }
}
