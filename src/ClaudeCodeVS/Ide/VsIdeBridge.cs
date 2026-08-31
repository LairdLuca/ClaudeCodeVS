using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ClaudeCodeVS.Mcp;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;

namespace ClaudeCodeVS.Ide
{
    /// <summary>
    /// The Visual Studio side of the integration. Every method here hops to the UI thread first:
    /// the automation model and the shell services are single-threaded, while the MCP server
    /// runs entirely on background threads.
    /// </summary>
    internal sealed class VsIdeBridge : IIdeBridge, IDisposable
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly JoinableTaskFactory _joinableTaskFactory;
        private readonly Func<IEnumerable<string>> _additionalFolders;
        private readonly DiffTabManager _diffTabs;
        private readonly DiagnosticsReader _diagnostics = new DiagnosticsReader();

        public VsIdeBridge(
            IServiceProvider serviceProvider,
            JoinableTaskFactory joinableTaskFactory,
            Func<IEnumerable<string>> additionalFolders)
        {
            _serviceProvider = serviceProvider;
            _joinableTaskFactory = joinableTaskFactory;
            _additionalFolders = additionalFolders;
            _diffTabs = new DiffTabManager(serviceProvider, joinableTaskFactory);
        }

        /// <summary>The permission mode the CLI last reported; shown in the status command.</summary>
        public string PermissionMode { get; private set; }

        /// <summary>Must be called on the UI thread.</summary>
        public void Initialize()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _diffTabs.Initialize();
            _diagnostics.Initialize(_serviceProvider);
        }

        /// <summary>
        /// The folders advertised in the lock file. Claude Code only offers an IDE for automatic
        /// connection when one of these contains its working directory, so a solution that sits
        /// in a subfolder of the directory the CLI is started from needs the extra entries from
        /// the options page.
        /// </summary>
        public IList<string> GetWorkspaceFoldersOnUiThread()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var folders = new List<string>();

            try
            {
                var solution = _serviceProvider.GetService(typeof(SVsSolution)) as IVsSolution;
                if (solution != null)
                {
                    string solutionDirectory, solutionFile, userOptionsFile;
                    var hr = solution.GetSolutionInfo(out solutionDirectory, out solutionFile, out userOptionsFile);
                    if (ErrorHandler.Succeeded(hr) && !string.IsNullOrEmpty(solutionDirectory))
                    {
                        // In Open Folder mode this is the opened folder rather than a .sln directory.
                        AddFolder(folders, solutionDirectory);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("Could not read the solution directory", ex);
            }

            if (_additionalFolders != null)
            {
                try
                {
                    foreach (var folder in _additionalFolders())
                    {
                        AddFolder(folders, folder);
                    }
                }
                catch (Exception ex)
                {
                    Log.Error("Could not read the configured extra folders", ex);
                }
            }

            return folders;
        }

        private static void AddFolder(List<string> folders, string candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate)) return;

            string full;
            try
            {
                full = Path.GetFullPath(candidate.Trim()).TrimEnd(Path.DirectorySeparatorChar);
            }
            catch (Exception)
            {
                return;
            }

            foreach (var existing in folders)
            {
                if (string.Equals(existing, full, StringComparison.OrdinalIgnoreCase)) return;
            }

            folders.Add(full);
        }

        public Task<IList<string>> GetWorkspaceFoldersAsync()
        {
            return _joinableTaskFactory.RunAsync(async delegate
            {
                await _joinableTaskFactory.SwitchToMainThreadAsync();
                return GetWorkspaceFoldersOnUiThread();
            }).Task;
        }

        public Task<List<object>> GetDiagnosticsAsync(string fileUri)
        {
            return _joinableTaskFactory.RunAsync(async delegate
            {
                await _joinableTaskFactory.SwitchToMainThreadAsync();
                return _diagnostics.Read(fileUri);
            }).Task;
        }

        public Task<DiffOutcome> OpenDiffAsync(
            string oldFilePath,
            string newFilePath,
            string newFileContents,
            string tabName,
            CancellationToken cancellationToken)
        {
            return _diffTabs.OpenDiffAsync(oldFilePath, newFilePath, newFileContents, tabName, cancellationToken);
        }

        public Task<bool> CloseTabAsync(string tabName)
        {
            return _diffTabs.CloseTabAsync(tabName);
        }

        public Task CloseAllDiffTabsAsync()
        {
            return _diffTabs.CloseAllAsync();
        }

        public Task<List<object>> GetOpenEditorsAsync()
        {
            return _joinableTaskFactory.RunAsync(async delegate
            {
                await _joinableTaskFactory.SwitchToMainThreadAsync();

                var editors = new List<object>();
                var dte = Package.GetGlobalService(typeof(SDTE)) as DTE2;
                if (dte == null) return editors;

                try
                {
                    foreach (Document document in dte.Documents)
                    {
                        if (document == null) continue;

                        string path = null;
                        try { path = document.FullName; } catch (Exception) { }
                        if (string.IsNullOrEmpty(path)) continue;

                        bool saved = true;
                        try { saved = document.Saved; } catch (Exception) { }

                        editors.Add(Json.Obj(
                            "uri", ToFileUri(path),
                            "filePath", path,
                            "isActive", IsActiveDocument(dte, path),
                            "isDirty", !saved));
                    }
                }
                catch (Exception ex)
                {
                    Log.Error("Could not enumerate the open documents", ex);
                }

                return editors;
            }).Task;
        }

        private static bool IsActiveDocument(DTE2 dte, string path)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                var active = dte.ActiveDocument;
                return active != null && string.Equals(active.FullName, path, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                return false;
            }
        }

        public Task<object> GetCurrentSelectionAsync()
        {
            return _joinableTaskFactory.RunAsync(async delegate
            {
                await _joinableTaskFactory.SwitchToMainThreadAsync();
                var snapshot = SelectionWatcher.Capture(includeText: true);
                return snapshot == null ? null : snapshot.ToToolResultPayload();
            }).Task;
        }

        public Task<bool> OpenFileAsync(string filePath, int? startLine, int? endLine)
        {
            return _joinableTaskFactory.RunAsync(async delegate
            {
                await _joinableTaskFactory.SwitchToMainThreadAsync();

                if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return false;

                var dte = Package.GetGlobalService(typeof(SDTE)) as DTE2;
                if (dte == null) return false;

                try
                {
                    var window = dte.ItemOperations.OpenFile(filePath, EnvDTE.Constants.vsViewKindCode);
                    if (window != null) window.Activate();
                }
                catch (Exception ex)
                {
                    Log.Error("Could not open " + filePath, ex);
                    return false;
                }

                // Selecting is a separate concern from opening. A range that runs past the end
                // of the file throws, and reporting that as a failed open would tell the caller
                // the file is missing when it is sitting open in front of the user.
                if (startLine.HasValue)
                {
                    try
                    {
                        SelectLines(dte, startLine.Value, endLine);
                    }
                    catch (Exception ex)
                    {
                        Log.Error("Opened " + filePath + " but could not select the requested lines", ex);
                    }
                }

                return true;
            }).Task;
        }

        private static void SelectLines(DTE2 dte, int startLine, int? endLine)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var document = dte.ActiveDocument;
            if (document == null) return;

            var selection = document.Selection as TextSelection;
            if (selection == null) return;

            // The CLI counts lines from zero, the automation model from one.
            int lastLine = int.MaxValue;
            var textDocument = document.Object("TextDocument") as TextDocument;
            if (textDocument != null && textDocument.EndPoint != null)
            {
                lastLine = textDocument.EndPoint.Line;
            }

            int first = Math.Min(Math.Max(1, startLine + 1), lastLine);
            selection.MoveToLineAndOffset(first, 1, false);

            if (!endLine.HasValue || endLine.Value < startLine) return;

            int last = Math.Min(Math.Max(first, endLine.Value + 1), lastLine);
            selection.MoveToLineAndOffset(last, 1, true);
            selection.EndOfLine(true);
        }

        public Task<bool> SaveDocumentAsync(string filePath)
        {
            return _joinableTaskFactory.RunAsync(async delegate
            {
                await _joinableTaskFactory.SwitchToMainThreadAsync();

                var document = FindDocument(filePath);
                if (document == null) return false;

                try
                {
                    document.Save();
                    return true;
                }
                catch (Exception ex)
                {
                    Log.Error("Could not save " + filePath, ex);
                    return false;
                }
            }).Task;
        }

        public Task<bool> IsDocumentDirtyAsync(string filePath)
        {
            return _joinableTaskFactory.RunAsync(async delegate
            {
                await _joinableTaskFactory.SwitchToMainThreadAsync();

                var document = FindDocument(filePath);
                if (document == null) return false;

                try
                {
                    return !document.Saved;
                }
                catch (Exception)
                {
                    return false;
                }
            }).Task;
        }

        private static Document FindDocument(string filePath)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (string.IsNullOrEmpty(filePath)) return null;

            var dte = Package.GetGlobalService(typeof(SDTE)) as DTE2;
            if (dte == null) return null;

            try
            {
                foreach (Document document in dte.Documents)
                {
                    string path = null;
                    try { path = document.FullName; } catch (Exception) { }
                    if (string.Equals(path, filePath, StringComparison.OrdinalIgnoreCase)) return document;
                }
            }
            catch (Exception)
            {
                // The collection can change underneath the enumeration.
            }

            return null;
        }

        public void SetPermissionMode(string mode)
        {
            PermissionMode = mode;
            Log.Info("The CLI reported permission mode '" + (mode ?? "unknown") + "'.");
        }

        internal static string ToFileUri(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;

            try
            {
                return new Uri(path).AbsoluteUri;
            }
            catch (UriFormatException)
            {
                return "file:///" + path.Replace('\\', '/');
            }
        }

        public void Dispose()
        {
            _diagnostics.Dispose();
            _diffTabs.Dispose();
        }
    }
}
