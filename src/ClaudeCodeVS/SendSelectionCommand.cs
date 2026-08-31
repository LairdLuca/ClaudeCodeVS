using System;
using System.ComponentModel.Design;
using System.Globalization;
using System.Text;
using System.Threading.Tasks;
using ClaudeCodeVS.Ide;
using ClaudeCodeVS.Mcp;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace ClaudeCodeVS
{
    /// <summary>
    /// The menu commands: pushing the current selection into the Claude Code prompt, and a
    /// status readout for when the /ide picker does not show this instance.
    /// </summary>
    internal static class SendSelectionCommand
    {
        private static readonly Guid CommandSet = new Guid("B237E1C0-61F3-4A16-A522-899C13BF2395");

        private const int CmdIdSendSelection = 0x0100;
        private const int CmdIdSendSelectionFromEditor = 0x0101;
        private const int CmdIdShowStatus = 0x0102;

        public static async Task InitializeAsync(AsyncPackage package)
        {
            var commandService = await package
                .GetServiceAsync(typeof(IMenuCommandService))
                .ConfigureAwait(false) as OleMenuCommandService;

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);

            if (commandService == null)
            {
                Log.Info("The menu command service is unavailable; the Claude Code commands were not registered.");
                return;
            }

            commandService.AddCommand(new MenuCommand(
                (s, e) => SendSelection(package),
                new CommandID(CommandSet, CmdIdSendSelection)));

            commandService.AddCommand(new MenuCommand(
                (s, e) => SendSelection(package),
                new CommandID(CommandSet, CmdIdSendSelectionFromEditor)));

            commandService.AddCommand(new MenuCommand(
                (s, e) => ShowStatus(package),
                new CommandID(CommandSet, CmdIdShowStatus)));
        }

        private static void SendSelection(AsyncPackage package)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var instance = ClaudeCodePackage.Instance;
            if (instance == null) return;

            if (!instance.IsConnected)
            {
                ShowMessage(package, "Claude Code is not attached. Run 'claude' in a terminal and use /ide to connect.");
                return;
            }

            var snapshot = SelectionWatcher.Capture(includeText: false);
            if (snapshot == null || string.IsNullOrEmpty(snapshot.FilePath))
            {
                ShowMessage(package, "There is no active text editor to send.");
                return;
            }

            // A caret with no selection means the whole file, which is what the equivalent
            // command does in the other editors.
            object payload = snapshot.IsEmpty
                ? Json.Obj("filePath", snapshot.FilePath)
                : Json.Obj(
                    "filePath", snapshot.FilePath,
                    "lineStart", snapshot.StartLine,
                    "lineEnd", snapshot.EndLine);

            instance.Broadcast("at_mentioned", payload);
            Log.Info("Sent " + snapshot.FilePath + " to Claude Code.");
        }

        private static void ShowStatus(AsyncPackage package)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var instance = ClaudeCodePackage.Instance;
            var report = new StringBuilder();

            if (instance == null || instance.Port == 0)
            {
                report.AppendLine("The bridge is not running.");
                report.AppendLine("Check Tools > Options > Claude Code, then restart Visual Studio.");
            }
            else
            {
                report.AppendLine("Listening on 127.0.0.1:" + instance.Port.ToString(CultureInfo.InvariantCulture));
                report.AppendLine("Claude Code attached: " + (instance.IsConnected ? "yes" : "no"));
                if (!string.IsNullOrEmpty(instance.PermissionMode))
                {
                    report.AppendLine("Permission mode: " + instance.PermissionMode);
                }

                report.AppendLine();
                report.AppendLine("Lock file directory:");
                report.AppendLine("  " + LockFile.IdeDirectory);
                report.AppendLine();
                report.AppendLine("Advertised folders (the CLI connects automatically only when");
                report.AppendLine("its working directory is inside one of these):");

                var folders = ThreadHelper.JoinableTaskFactory.Run(async delegate
                {
                    return await instance.GetAdvertisedFoldersAsync().ConfigureAwait(true);
                });

                if (folders == null || folders.Count == 0)
                {
                    report.AppendLine("  (none - no solution or folder is open)");
                }
                else
                {
                    foreach (var folder in folders) report.AppendLine("  " + folder);
                }

                report.AppendLine();
                if (instance.DebuggerPort == 0)
                {
                    report.AppendLine("Debugger control: off.");
                    report.AppendLine("Enable it in Tools > Options > Claude Code, or check the log");
                    report.AppendLine("for a port conflict with another Visual Studio instance.");
                }
                else
                {
                    report.AppendLine("Debugger control: 127.0.0.1:" +
                        instance.DebuggerPort.ToString(CultureInfo.InvariantCulture) +
                        ", client attached: " + (instance.DebuggerConnected ? "yes" : "no"));
                    report.AppendLine();
                    report.AppendLine("It is a separate MCP server and has to be registered once.");
                    report.AppendLine("The exact command is in the Claude Code output pane and in:");
                    report.AppendLine("  " + Log.FilePath);
                }
            }

            ShowMessage(package, report.ToString());
        }

        private static void ShowMessage(IServiceProvider serviceProvider, string message)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            VsShellUtilities.ShowMessageBox(
                serviceProvider,
                message,
                "Claude Code",
                OLEMSGICON.OLEMSGICON_INFO,
                OLEMSGBUTTON.OLEMSGBUTTON_OK,
                OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
        }
    }
}
