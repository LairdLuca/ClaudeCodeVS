using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using ClaudeCodeVS.Ide;
using ClaudeCodeVS.Mcp;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace ClaudeCodeVS
{
    /// <summary>
    /// Starts the two local MCP endpoints, publishes the lock file that makes this instance
    /// visible to <c>claude /ide</c>, and keeps both in step with whatever solution is open.
    /// </summary>
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [Guid(PackageGuidString)]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [ProvideAutoLoad(VSConstants.UICONTEXT.ShellInitialized_string, PackageAutoLoadFlags.BackgroundLoad)]
    [ProvideOptionPage(typeof(ClaudeCodeOptions), "Claude Code", "General", 0, 0, true)]
    public sealed class ClaudeCodePackage : AsyncPackage
    {
        public const string PackageGuidString = "96A4CDA8-6BCF-4D9C-BBA9-72EF24E65400";

        private static readonly Guid OutputPaneGuid = new Guid("7C3A2E5B-9D41-4C18-9A5F-0B4D6E8C1A72");

        private ClaudeCodeOptions _options;
        private VsIdeBridge _bridge;
        private DebuggerBridge _debugger;
        private McpEndpoint _ideEndpoint;
        private McpEndpoint _debugEndpoint;
        private LockFile _lockFile;
        private SelectionWatcher _selectionWatcher;
        private IVsSolution _solution;
        private uint _solutionEventsCookie;
        private IVsOutputWindowPane _outputPane;

        internal static ClaudeCodePackage Instance { get; private set; }

        internal bool IsConnected { get { return _ideEndpoint != null && _ideEndpoint.HasClients; } }

        internal int Port { get { return _ideEndpoint == null ? 0 : _ideEndpoint.Port; } }

        internal int DebuggerPort { get { return _debugEndpoint == null ? 0 : _debugEndpoint.Port; } }

        internal bool DebuggerConnected { get { return _debugEndpoint != null && _debugEndpoint.HasClients; } }

        internal string DebuggerAuthToken { get { return _debugEndpoint == null ? null : _debugEndpoint.AuthToken; } }

        internal string PermissionMode { get { return _bridge == null ? null : _bridge.PermissionMode; } }

        protected override async Task InitializeAsync(
            CancellationToken cancellationToken,
            IProgress<ServiceProgressData> progress)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            Instance = this;
            SetUpOutputPane();

            _options = (ClaudeCodeOptions)GetDialogPage(typeof(ClaudeCodeOptions));

            await SendSelectionCommand.InitializeAsync(this);

            if (!_options.Enabled)
            {
                Log.Info("The Claude Code bridge is disabled in Tools > Options.");
                return;
            }

            try
            {
                LockFile.RemoveStaleEntries();

                _bridge = new VsIdeBridge(this, JoinableTaskFactory, () => _options.GetAdditionalWorkspaceFolders());
                _bridge.Initialize();

                StartIdeEndpoint();
                StartDebugEndpoint();

                AdviseSolutionEvents();

                if (_options.TrackSelection)
                {
                    _selectionWatcher = new SelectionWatcher(() => IsConnected, OnSelectionChanged);
                    _selectionWatcher.Start();
                }
            }
            catch (Exception ex)
            {
                Log.Error("The Claude Code bridge failed to start", ex);
            }
        }

        private void StartIdeEndpoint()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            // A fresh secret per session, handed to the CLI through the lock file. The server
            // listens on loopback, so without it any local process could drive the IDE.
            var token = McpEndpoint.CreateToken();
            _ideEndpoint = new McpEndpoint("IDE", token, new IdeToolHost(_bridge), 0);

            if (!_ideEndpoint.TryStart())
            {
                _ideEndpoint = null;
                return;
            }

            _lockFile = new LockFile();
            _lockFile.Publish(_ideEndpoint.Port, token, _options.IdeName, _bridge.GetWorkspaceFoldersOnUiThread());
            Log.Info("Run 'claude' and use /ide to attach.");
        }

        private void StartDebugEndpoint()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (!_options.EnableDebuggerBridge)
            {
                Log.Info("Debugger control is disabled in Tools > Options.");
                return;
            }

            _debugger = new DebuggerBridge(JoinableTaskFactory);
            _debugger.Initialize();
            _debugger.StateChanged += OnDebuggerStateChanged;

            // Fixed port and persistent token, because this endpoint is referenced by a static
            // MCP server configuration rather than discovered through the lock file.
            _debugEndpoint = new McpEndpoint(
                "debugger",
                DebugTokenStore.LoadOrCreate(),
                new DebugToolHost(_debugger),
                _options.DebuggerPort);

            if (!_debugEndpoint.TryStart())
            {
                Log.Error(
                    "Port " + _options.DebuggerPort.ToString(CultureInfo.InvariantCulture) +
                    " is unavailable, most likely another Visual Studio instance holds it. " +
                    "Debugger control is off for this instance; the IDE integration is unaffected.",
                    null);
                _debugEndpoint = null;
                return;
            }

            Log.Info("To use it, register the debugger endpoint once with:");
            Log.Info("  " + BuildMcpAddCommand());
        }

        /// <summary>The command that registers the debugger endpoint as an MCP server.</summary>
        internal string BuildMcpAddCommand()
        {
            var port = DebuggerPort.ToString(CultureInfo.InvariantCulture);
            var token = DebuggerAuthToken ?? string.Empty;

            return "claude mcp add-json --scope user vsdebug \"{\\\"type\\\":\\\"ws\\\"," +
                "\\\"url\\\":\\\"ws://127.0.0.1:" + port + "\\\"," +
                "\\\"headers\\\":{\\\"X-Claude-Code-Ide-Authorization\\\":\\\"" + token + "\\\"}}\"";
        }

        private void OnDebuggerStateChanged(object payload)
        {
            if (_debugEndpoint == null) return;
            _debugEndpoint.Broadcast("debugger_state_changed", payload);
        }

        private void SetUpOutputPane()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                var outputWindow = GetService(typeof(SVsOutputWindow)) as IVsOutputWindow;
                if (outputWindow == null) return;

                var paneGuid = OutputPaneGuid;
                outputWindow.CreatePane(ref paneGuid, "Claude Code", 1, 1);
                outputWindow.GetPane(ref paneGuid, out _outputPane);

                var pane = _outputPane;
                if (pane != null)
                {
                    // OutputStringThreadSafe is the one member safe to call off the UI thread,
                    // which matters because most log lines come from the server's own threads.
                    Log.SetSink(line => pane.OutputStringThreadSafe(line + Environment.NewLine));
                }
            }
            catch (Exception)
            {
                // Without a pane, logging still reaches the file and the debugger.
            }
        }

        private void OnSelectionChanged(SelectionSnapshot snapshot)
        {
            if (snapshot == null || _ideEndpoint == null) return;
            _ideEndpoint.Broadcast("selection_changed", snapshot.ToNotificationPayload());
        }

        internal void Broadcast(string method, object payload)
        {
            if (_ideEndpoint == null) return;
            _ideEndpoint.Broadcast(method, payload);
        }

        internal async Task<IList<string>> GetAdvertisedFoldersAsync()
        {
            if (_bridge == null) return new List<string>();
            return await _bridge.GetWorkspaceFoldersAsync().ConfigureAwait(false);
        }

        private void AdviseSolutionEvents()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            _solution = GetService(typeof(SVsSolution)) as IVsSolution;
            if (_solution == null) return;

            _solution.AdviseSolutionEvents(new SolutionEventSink(this), out _solutionEventsCookie);
        }

        /// <summary>
        /// Rewrites the lock file so that the folders it advertises match the solution that is
        /// open now. Without this, opening a different solution would leave Claude Code matching
        /// against the previous one.
        /// </summary>
        internal void RefreshWorkspaceFolders()
        {
            if (_lockFile == null || _bridge == null) return;

            var ignored = JoinableTaskFactory.RunAsync(async delegate
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync();
                try
                {
                    _lockFile.Refresh(_bridge.GetWorkspaceFoldersOnUiThread());
                }
                catch (Exception ex)
                {
                    Log.Error("Could not refresh the lock file", ex);
                }
            });
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try
                {
                    if (_selectionWatcher != null) _selectionWatcher.Dispose();

                    if (_solution != null && _solutionEventsCookie != 0)
                    {
                        _solution.UnadviseSolutionEvents(_solutionEventsCookie);
                        _solutionEventsCookie = 0;
                    }

                    // The lock file goes first: it is what advertises this instance, and leaving
                    // it behind would point the CLI at a socket that is about to close.
                    if (_lockFile != null) _lockFile.Dispose();
                    if (_ideEndpoint != null) _ideEndpoint.Dispose();
                    if (_debugEndpoint != null) _debugEndpoint.Dispose();

                    if (_debugger != null)
                    {
                        _debugger.StateChanged -= OnDebuggerStateChanged;
                        _debugger.Dispose();
                    }

                    if (_bridge != null) _bridge.Dispose();
                }
                catch (Exception ex)
                {
                    Log.Error("Shutdown was not clean", ex);
                }
            }

            base.Dispose(disposing);
        }

        private sealed class SolutionEventSink : IVsSolutionEvents
        {
            private readonly ClaudeCodePackage _owner;

            public SolutionEventSink(ClaudeCodePackage owner)
            {
                _owner = owner;
            }

            public int OnAfterOpenSolution(object pUnkReserved, int fNewSolution)
            {
                _owner.RefreshWorkspaceFolders();
                return VSConstants.S_OK;
            }

            public int OnAfterCloseSolution(object pUnkReserved)
            {
                _owner.RefreshWorkspaceFolders();
                return VSConstants.S_OK;
            }

            public int OnAfterOpenProject(IVsHierarchy pHierarchy, int fAdded) { return VSConstants.S_OK; }
            public int OnQueryCloseProject(IVsHierarchy pHierarchy, int fRemoving, ref int pfCancel) { return VSConstants.S_OK; }
            public int OnBeforeCloseProject(IVsHierarchy pHierarchy, int fRemoved) { return VSConstants.S_OK; }
            public int OnAfterLoadProject(IVsHierarchy pStubHierarchy, IVsHierarchy pRealHierarchy) { return VSConstants.S_OK; }
            public int OnQueryUnloadProject(IVsHierarchy pRealHierarchy, ref int pfCancel) { return VSConstants.S_OK; }
            public int OnBeforeUnloadProject(IVsHierarchy pRealHierarchy, IVsHierarchy pStubHierarchy) { return VSConstants.S_OK; }
            public int OnQueryCloseSolution(object pUnkReserved, ref int pfCancel) { return VSConstants.S_OK; }
            public int OnBeforeCloseSolution(object pUnkReserved) { return VSConstants.S_OK; }
        }
    }
}
