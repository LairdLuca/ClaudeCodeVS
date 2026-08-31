using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ClaudeCodeVS.Mcp;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;

namespace ClaudeCodeVS.Ide
{
    /// <summary>
    /// Shows Claude's proposed edits in the Visual Studio comparison window and reports back
    /// what the user did with them.
    ///
    /// The current file is placed on the left and the proposal, written to a temporary file, on
    /// the right. The right pane is a normal editable document, so the user can adjust the
    /// proposal before accepting it: saving it is the signal that the saved text, not the
    /// original proposal, is what should be applied.
    ///
    /// Three things can end the wait, and the CLI reads each one differently. See
    /// <see cref="DiffResult"/>.
    /// </summary>
    internal sealed class DiffTabManager : IVsRunningDocTableEvents3, IDisposable
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly JoinableTaskFactory _joinableTaskFactory;
        private readonly object _gate = new object();
        private readonly List<DiffTab> _tabs = new List<DiffTab>();
        private readonly string _temporaryRoot;

        private IVsRunningDocumentTable _runningDocumentTable;
        private uint _runningDocumentTableCookie;
        private int _disposed;

        public DiffTabManager(IServiceProvider serviceProvider, JoinableTaskFactory joinableTaskFactory)
        {
            _serviceProvider = serviceProvider;
            _joinableTaskFactory = joinableTaskFactory;
            _temporaryRoot = Path.Combine(Path.GetTempPath(), "ClaudeCodeVS");
        }

        /// <summary>Must be called on the UI thread.</summary>
        public void Initialize()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            _runningDocumentTable = _serviceProvider.GetService(typeof(SVsRunningDocumentTable)) as IVsRunningDocumentTable;
            if (_runningDocumentTable != null)
            {
                _runningDocumentTable.AdviseRunningDocTableEvents(this, out _runningDocumentTableCookie);
            }
        }

        public async Task<DiffOutcome> OpenDiffAsync(
            string oldFilePath,
            string newFilePath,
            string newFileContents,
            string tabName,
            CancellationToken cancellationToken)
        {
            await _joinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            DiffTab tab;
            try
            {
                tab = CreateTab(oldFilePath, newFilePath, newFileContents, tabName);
            }
            catch (Exception ex)
            {
                Log.Error("Could not open the diff window", ex);
                // Falling back to a rejection would tell the CLI the user said no. Reporting the
                // tab as closed instead lets the terminal-side prompt remain the decision point.
                return new DiffOutcome(DiffResult.TabClosed, null);
            }

            using (cancellationToken.Register(() => Complete(tab, DiffResult.Rejected, null)))
            {
                return await tab.Completion.Task.ConfigureAwait(false);
            }
        }

        private DiffTab CreateTab(string oldFilePath, string newFilePath, string newFileContents, string tabName)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var target = string.IsNullOrEmpty(newFilePath) ? oldFilePath : newFilePath;
            var fileName = string.IsNullOrEmpty(target) ? "proposal.txt" : Path.GetFileName(target);

            var directory = Path.Combine(_temporaryRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);

            var proposalPath = Path.Combine(directory, fileName);
            File.WriteAllText(proposalPath, newFileContents ?? string.Empty, new UTF8Encoding(false));

            // A brand new file has no left-hand side; compare against an empty document so the
            // whole proposal reads as an addition.
            var leftPath = oldFilePath;
            string emptyLeftPath = null;
            if (string.IsNullOrEmpty(leftPath) || !File.Exists(leftPath))
            {
                emptyLeftPath = Path.Combine(directory, "empty_" + fileName);
                File.WriteAllText(emptyLeftPath, string.Empty, new UTF8Encoding(false));
                leftPath = emptyLeftPath;
            }

            var differenceService = _serviceProvider.GetService(typeof(SVsDifferenceService)) as IVsDifferenceService;
            if (differenceService == null)
            {
                throw new InvalidOperationException("The Visual Studio difference service is unavailable.");
            }

            var caption = string.IsNullOrEmpty(tabName) ? "Claude Code proposal" : tabName;

            var frame = differenceService.OpenComparisonWindow2(
                leftPath,
                proposalPath,
                caption,
                target,
                Path.GetFileName(target) + " (current)",
                Path.GetFileName(target) + " (Claude Code proposal)",
                caption,
                null,
                (uint)__VSDIFFSERVICEOPTIONS.VSDIFFOPT_DetectBinaryFiles);

            if (frame == null)
            {
                throw new InvalidOperationException("The comparison window could not be created.");
            }

            var tab = new DiffTab
            {
                TabName = caption,
                TargetPath = target,
                ProposalPath = proposalPath,
                Directory = directory,
                Frame = frame,
                Completion = new TaskCompletionSource<DiffOutcome>(TaskCreationOptions.RunContinuationsAsynchronously)
            };

            lock (_gate)
            {
                _tabs.Add(tab);
            }

            // Two independent signals, because neither alone covers every way the window ends:
            // the running document table reports the save, the frame notification reports the
            // user closing the tab.
            var watcher = new FrameWatcher(() => Complete(tab, DiffResult.Rejected, null));
            tab.Watcher = watcher;
            frame.SetProperty((int)__VSFPROPID.VSFPROPID_ViewHelper, watcher);

            frame.Show();

            Log.Info("Opened the diff window '" + caption + "' for " + target);
            return tab;
        }

        public Task<bool> CloseTabAsync(string tabName)
        {
            return _joinableTaskFactory.RunAsync(async delegate
            {
                await _joinableTaskFactory.SwitchToMainThreadAsync();

                var matches = new List<DiffTab>();
                lock (_gate)
                {
                    foreach (var tab in _tabs)
                    {
                        if (string.Equals(tab.TabName, tabName, StringComparison.Ordinal)) matches.Add(tab);
                    }
                }

                foreach (var tab in matches)
                {
                    Complete(tab, DiffResult.TabClosed, null);
                }

                return matches.Count > 0;
            }).Task;
        }

        public Task CloseAllAsync()
        {
            return _joinableTaskFactory.RunAsync(async delegate
            {
                await _joinableTaskFactory.SwitchToMainThreadAsync();

                DiffTab[] all;
                lock (_gate)
                {
                    all = _tabs.ToArray();
                }

                foreach (var tab in all)
                {
                    Complete(tab, DiffResult.TabClosed, null);
                }
            }).Task;
        }

        /// <summary>
        /// Resolves the wait exactly once and tears the window down. Safe to call from any of
        /// the three racing signals.
        /// </summary>
        private void Complete(DiffTab tab, DiffResult result, string content)
        {
            if (tab == null) return;
            if (Interlocked.Exchange(ref tab.Completed, 1) != 0) return;

            lock (_gate)
            {
                _tabs.Remove(tab);
            }

            tab.Completion.TrySetResult(new DiffOutcome(result, content));

            // Closing the frame must not run inline: this may be called from the frame's own
            // close notification, and from background threads via cancellation.
            var frame = tab.Frame;
            var ignored = _joinableTaskFactory.RunAsync(async delegate
            {
                await _joinableTaskFactory.SwitchToMainThreadAsync();

                if (frame != null)
                {
                    try
                    {
                        frame.SetProperty((int)__VSFPROPID.VSFPROPID_ViewHelper, null);
                        frame.CloseFrame((uint)__FRAMECLOSE.FRAMECLOSE_NoSave);
                    }
                    catch (Exception)
                    {
                        // The frame may already be gone.
                    }
                }

                CleanUp(tab);
            });
        }

        private static void CleanUp(DiffTab tab)
        {
            try
            {
                if (tab.Directory != null && Directory.Exists(tab.Directory))
                {
                    Directory.Delete(tab.Directory, true);
                }
            }
            catch (Exception)
            {
                // A file still held open by the editor is not worth reporting.
            }
        }

        private DiffTab FindByDocumentCookie(uint cookie)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (_runningDocumentTable == null) return null;

            string moniker = null;
            try
            {
                uint flags, readLocks, editLocks, itemId;
                IVsHierarchy hierarchy;
                IntPtr documentData;
                _runningDocumentTable.GetDocumentInfo(
                    cookie, out flags, out readLocks, out editLocks, out moniker,
                    out hierarchy, out itemId, out documentData);
                if (documentData != IntPtr.Zero) Marshal.Release(documentData);
            }
            catch (Exception)
            {
                return null;
            }

            if (string.IsNullOrEmpty(moniker)) return null;

            lock (_gate)
            {
                foreach (var tab in _tabs)
                {
                    if (string.Equals(tab.ProposalPath, moniker, StringComparison.OrdinalIgnoreCase)) return tab;
                }
            }

            return null;
        }

        int IVsRunningDocTableEvents.OnAfterSave(uint docCookie)
        {
            return HandleSave(docCookie);
        }

        int IVsRunningDocTableEvents2.OnAfterSave(uint docCookie)
        {
            return HandleSave(docCookie);
        }

        int IVsRunningDocTableEvents3.OnAfterSave(uint docCookie)
        {
            return HandleSave(docCookie);
        }

        private int HandleSave(uint docCookie)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                var tab = FindByDocumentCookie(docCookie);
                if (tab != null)
                {
                    var saved = File.ReadAllText(tab.ProposalPath);
                    Log.Info("The proposal in '" + tab.TabName + "' was saved; applying the saved text.");
                    Complete(tab, DiffResult.FileSaved, saved);
                }
            }
            catch (Exception ex)
            {
                Log.Error("Could not read the saved proposal", ex);
            }

            return VSConstants.S_OK;
        }

        int IVsRunningDocTableEvents3.OnBeforeSave(uint docCookie) { return VSConstants.S_OK; }

        int IVsRunningDocTableEvents.OnAfterFirstDocumentLock(uint a, uint b, uint c, uint d) { return VSConstants.S_OK; }
        int IVsRunningDocTableEvents.OnBeforeLastDocumentUnlock(uint a, uint b, uint c, uint d) { return VSConstants.S_OK; }
        int IVsRunningDocTableEvents.OnAfterAttributeChange(uint a, uint b) { return VSConstants.S_OK; }
        int IVsRunningDocTableEvents.OnBeforeDocumentWindowShow(uint a, int b, IVsWindowFrame c) { return VSConstants.S_OK; }
        int IVsRunningDocTableEvents.OnAfterDocumentWindowHide(uint a, IVsWindowFrame b) { return VSConstants.S_OK; }

        int IVsRunningDocTableEvents2.OnAfterFirstDocumentLock(uint a, uint b, uint c, uint d) { return VSConstants.S_OK; }
        int IVsRunningDocTableEvents2.OnBeforeLastDocumentUnlock(uint a, uint b, uint c, uint d) { return VSConstants.S_OK; }
        int IVsRunningDocTableEvents2.OnAfterAttributeChange(uint a, uint b) { return VSConstants.S_OK; }
        int IVsRunningDocTableEvents2.OnBeforeDocumentWindowShow(uint a, int b, IVsWindowFrame c) { return VSConstants.S_OK; }
        int IVsRunningDocTableEvents2.OnAfterDocumentWindowHide(uint a, IVsWindowFrame b) { return VSConstants.S_OK; }
        int IVsRunningDocTableEvents2.OnAfterAttributeChangeEx(uint a, uint b, IVsHierarchy c, uint d, string e, IVsHierarchy f, uint g, string h) { return VSConstants.S_OK; }

        int IVsRunningDocTableEvents3.OnAfterFirstDocumentLock(uint a, uint b, uint c, uint d) { return VSConstants.S_OK; }
        int IVsRunningDocTableEvents3.OnBeforeLastDocumentUnlock(uint a, uint b, uint c, uint d) { return VSConstants.S_OK; }
        int IVsRunningDocTableEvents3.OnAfterAttributeChange(uint a, uint b) { return VSConstants.S_OK; }
        int IVsRunningDocTableEvents3.OnBeforeDocumentWindowShow(uint a, int b, IVsWindowFrame c) { return VSConstants.S_OK; }
        int IVsRunningDocTableEvents3.OnAfterDocumentWindowHide(uint a, IVsWindowFrame b) { return VSConstants.S_OK; }
        int IVsRunningDocTableEvents3.OnAfterAttributeChangeEx(uint a, uint b, IVsHierarchy c, uint d, string e, IVsHierarchy f, uint g, string h) { return VSConstants.S_OK; }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

            _joinableTaskFactory.Run(async delegate
            {
                await _joinableTaskFactory.SwitchToMainThreadAsync();

                if (_runningDocumentTable != null && _runningDocumentTableCookie != 0)
                {
                    try { _runningDocumentTable.UnadviseRunningDocTableEvents(_runningDocumentTableCookie); }
                    catch (Exception) { }
                    _runningDocumentTableCookie = 0;
                }
            });

            DiffTab[] remaining;
            lock (_gate)
            {
                remaining = _tabs.ToArray();
            }

            foreach (var tab in remaining)
            {
                Complete(tab, DiffResult.Rejected, null);
            }

            try
            {
                if (Directory.Exists(_temporaryRoot)) Directory.Delete(_temporaryRoot, true);
            }
            catch (Exception)
            {
                // Leftovers in the temp folder are harmless.
            }
        }

        private sealed class DiffTab
        {
            public string TabName;
            public string TargetPath;
            public string ProposalPath;
            public string Directory;
            public IVsWindowFrame Frame;
            public FrameWatcher Watcher;
            public TaskCompletionSource<DiffOutcome> Completion;
            public int Completed;
        }

        /// <summary>
        /// Attached to the window frame through <c>VSFPROPID_ViewHelper</c>, which is how a
        /// frame delivers close notifications to an extension.
        /// </summary>
        private sealed class FrameWatcher : IVsWindowFrameNotify3
        {
            private readonly Action _onClose;

            public FrameWatcher(Action onClose)
            {
                _onClose = onClose;
            }

            public int OnClose(ref uint pgrfSaveOptions)
            {
                pgrfSaveOptions = (uint)__FRAMECLOSE.FRAMECLOSE_NoSave;
                try { _onClose(); } catch (Exception) { }
                return VSConstants.S_OK;
            }

            public int OnShow(int fShow) { return VSConstants.S_OK; }
            public int OnMove(int x, int y, int w, int h) { return VSConstants.S_OK; }
            public int OnSize(int x, int y, int w, int h) { return VSConstants.S_OK; }
            public int OnDockableChange(int fDockable, int x, int y, int w, int h) { return VSConstants.S_OK; }
        }
    }
}
