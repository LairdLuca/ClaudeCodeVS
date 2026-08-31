using System;
using System.Collections.Generic;
using System.Globalization;
using ClaudeCodeVS.Mcp;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Shell.TableControl;
using Microsoft.VisualStudio.Shell.TableManager;

namespace ClaudeCodeVS.Ide
{
    /// <summary>
    /// Supplies the Error List contents in the shape the Claude Code CLI expects: one entry per
    /// file, each with diagnostics carrying a zero-based LSP-style range.
    ///
    /// It subscribes to the data sources behind the errors table rather than reading the tool
    /// window. Reading the window looks simpler but only works once that window has been created
    /// and realised, and an integration driven from a terminal has no reason to have opened it;
    /// the first version of this class did exactly that and reported no diagnostics at all even
    /// with real compile errors present. Subscribing to the sources gets the same data straight
    /// from the producers, whether or not the Error List is ever shown.
    ///
    /// Diagnostics arrive on background threads, so everything below the subscription is guarded
    /// and nothing here requires the UI thread except the initial service lookups.
    /// </summary>
    internal sealed class DiagnosticsReader : IDisposable
    {
        private readonly object _gate = new object();
        private readonly Dictionary<ITableDataSource, Subscription> _subscriptions =
            new Dictionary<ITableDataSource, Subscription>();

        private IServiceProvider _serviceProvider;
        private ITableManager _manager;
        private bool _disposed;
        private int _lastLoggedCount = -1;

        private sealed class Subscription
        {
            public IDisposable Handle;
            public Sink Sink;
        }

        /// <summary>Must be called on the UI thread.</summary>
        public void Initialize(IServiceProvider serviceProvider)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _serviceProvider = serviceProvider;

            try
            {
                var componentModel = serviceProvider.GetService(typeof(SComponentModel)) as IComponentModel;
                if (componentModel == null)
                {
                    Log.Error("The component model is unavailable; diagnostics will be empty.", null);
                    return;
                }

                var provider = componentModel.GetService<ITableManagerProvider>();
                if (provider == null)
                {
                    Log.Error("No ITableManagerProvider was exported; diagnostics will be empty.", null);
                    return;
                }

                _manager = provider.GetTableManager(StandardTables.ErrorsTable);
                if (_manager == null)
                {
                    Log.Error("The errors table manager is unavailable; diagnostics will be empty.", null);
                    return;
                }

                // Sources register themselves as projects load and analyzers start, so the set is
                // far from complete at package load time.
                _manager.SourcesChanged += OnSourcesChanged;
                SyncSources();
            }
            catch (Exception ex)
            {
                Log.Error("Could not subscribe to the Error List data sources", ex);
            }
        }

        private void OnSourcesChanged(object sender, EventArgs e)
        {
            SyncSources();
        }

        /// <summary>
        /// Brings the subscriptions in line with the sources the table manager currently has.
        ///
        /// Called from <see cref="Read"/> as well as from the SourcesChanged event, and that is
        /// deliberate: relying on the event alone produced an integration that only ever saw the
        /// two sources present at shell start-up and never the Roslyn one, which registers later.
        /// Re-checking a short list on each read costs nothing and does not depend on an event
        /// arriving.
        /// </summary>
        private void SyncSources()
        {
            try
            {
                var manager = _manager;
                if (manager == null) return;

                IReadOnlyList<ITableDataSource> sources;
                try
                {
                    sources = manager.Sources;
                }
                catch (Exception ex)
                {
                    Log.Error("Could not enumerate the Error List sources", ex);
                    return;
                }

                var added = new List<ITableDataSource>();
                var removed = new List<ITableDataSource>();

                lock (_gate)
                {
                    if (_disposed) return;

                    foreach (var known in _subscriptions.Keys)
                    {
                        if (!Contains(sources, known)) removed.Add(known);
                    }

                    foreach (var source in sources)
                    {
                        if (source != null && !_subscriptions.ContainsKey(source)) added.Add(source);
                    }
                }

                foreach (var source in removed)
                {
                    Subscription subscription;
                    lock (_gate)
                    {
                        if (!_subscriptions.TryGetValue(source, out subscription)) continue;
                        _subscriptions.Remove(source);
                    }

                    try { subscription.Handle.Dispose(); } catch (Exception) { }
                    Log.Info("Dropped the Error List source " + Describe(source) + ".");
                }

                // Subscribing is done outside the lock: a source may publish its current state
                // synchronously from inside Subscribe.
                foreach (var source in added)
                {
                    var sink = new Sink();
                    IDisposable handle;
                    try
                    {
                        handle = source.Subscribe(sink);
                    }
                    catch (Exception ex)
                    {
                        Log.Error("Could not subscribe to the Error List source " + Describe(source), ex);
                        continue;
                    }

                    bool keep;
                    lock (_gate)
                    {
                        keep = !_disposed && !_subscriptions.ContainsKey(source);
                        if (keep) _subscriptions[source] = new Subscription { Handle = handle, Sink = sink };
                    }

                    if (keep) Log.Info("Subscribed to the Error List source " + Describe(source) + ".");
                    else try { handle.Dispose(); } catch (Exception) { }
                }
            }
            catch (Exception ex)
            {
                Log.Error("Could not refresh the Error List subscriptions", ex);
            }
        }

        private static bool Contains(IReadOnlyList<ITableDataSource> sources, ITableDataSource wanted)
        {
            for (int i = 0; i < sources.Count; i++)
            {
                if (ReferenceEquals(sources[i], wanted)) return true;
            }

            return false;
        }

        private static string Describe(ITableDataSource source)
        {
            try
            {
                return "'" + source.DisplayName + "' (type " + source.SourceTypeIdentifier +
                    ", id " + source.Identifier + ")";
            }
            catch (Exception)
            {
                return "'(undescribable)'";
            }
        }

        public List<object> Read(string fileUriFilter)
        {
            var wanted = NormalizePath(UriToPath(fileUriFilter));
            var byFile = new Dictionary<string, List<object>>(StringComparer.OrdinalIgnoreCase);

            SyncSources();

            Sink[] sinks;
            lock (_gate)
            {
                sinks = new Sink[_subscriptions.Count];
                int index = 0;
                foreach (var subscription in _subscriptions.Values) sinks[index++] = subscription.Sink;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            int rowsSeen = 0;

            foreach (var sink in sinks)
            {
                foreach (var entry in sink.Collect())
                {
                    rowsSeen++;
                    string documentName;
                    if (!TryGetString(entry, StandardTableKeyNames.DocumentName, out documentName) ||
                        string.IsNullOrEmpty(documentName))
                    {
                        continue;
                    }

                    var normalized = NormalizePath(documentName);
                    if (wanted != null && !string.Equals(normalized, wanted, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var diagnostic = BuildDiagnostic(entry);

                    // The same problem is often published by more than one source, for instance
                    // the live analyzer and the last build.
                    var identity = normalized + "|" + Json.Serialize(diagnostic);
                    if (!seen.Add(identity)) continue;

                    List<object> bucket;
                    if (!byFile.TryGetValue(normalized, out bucket))
                    {
                        bucket = new List<object>();
                        byFile[normalized] = bucket;
                    }

                    bucket.Add(diagnostic);
                }
            }

            if (byFile.Count == 0 && rowsSeen == 0)
            {
                // Nothing came through the sources: fall back to the tool window, which does
                // hold entries once a human has opened the Error List.
                ReadFromToolWindow(byFile, wanted);
            }

            // Logged only when the picture changes, so a CLI polling for diagnostics does not
            // fill the log with identical lines.
            if (rowsSeen != _lastLoggedCount)
            {
                _lastLoggedCount = rowsSeen;
                Log.Info("Error List: " + sinks.Length.ToString(CultureInfo.InvariantCulture) +
                    " source(s), " + rowsSeen.ToString(CultureInfo.InvariantCulture) +
                    " row(s), " + byFile.Count.ToString(CultureInfo.InvariantCulture) + " file(s) after filtering.");
            }

            var result = new List<object>(byFile.Count);
            foreach (var pair in byFile)
            {
                result.Add(Json.Obj("uri", PathToUri(pair.Key), "diagnostics", pair.Value));
            }

            // The CLI checks that a file-scoped request answers for that file, so a clean file
            // must come back explicitly clean rather than as an empty list.
            if (wanted != null && result.Count == 0)
            {
                result.Add(Json.Obj("uri", PathToUri(wanted), "diagnostics", new object[0]));
            }

            return result;
        }

        private void ReadFromToolWindow(Dictionary<string, List<object>> byFile, string wanted)
        {
            try
            {
                ThreadHelper.ThrowIfNotOnUIThread();

                var errorList = _serviceProvider == null
                    ? null
                    : _serviceProvider.GetService(typeof(SVsErrorList)) as IErrorList;
                var control = errorList == null ? null : errorList.TableControl;
                if (control == null) return;

                foreach (var handle in control.Entries)
                {
                    string documentName;
                    if (!handle.TryGetValue(StandardTableKeyNames.DocumentName, out documentName) ||
                        string.IsNullOrEmpty(documentName))
                    {
                        continue;
                    }

                    var normalized = NormalizePath(documentName);
                    if (wanted != null && !string.Equals(normalized, wanted, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    List<object> bucket;
                    if (!byFile.TryGetValue(normalized, out bucket))
                    {
                        bucket = new List<object>();
                        byFile[normalized] = bucket;
                    }

                    bucket.Add(BuildDiagnostic(new HandleEntry(handle)));
                }
            }
            catch (Exception ex)
            {
                Log.Error("The Error List tool window fallback failed", ex);
            }
        }

        private static Dictionary<string, object> BuildDiagnostic(IEntry entry)
        {
            string message;
            if (!TryGetString(entry, StandardTableKeyNames.Text, out message)) message = string.Empty;

            int line = TryGetInt(entry, StandardTableKeyNames.Line);
            int column = TryGetInt(entry, StandardTableKeyNames.Column);

            string source;
            TryGetString(entry, StandardTableKeyNames.BuildTool, out source);

            string code;
            TryGetString(entry, StandardTableKeyNames.ErrorCode, out code);

            var position = Json.Obj("line", Math.Max(0, line), "character", Math.Max(0, column));

            return Json.Obj(
                "message", message ?? string.Empty,
                "severity", ReadSeverity(entry),
                "range", Json.Obj("start", position, "end", position),
                "source", string.IsNullOrEmpty(source) ? "Visual Studio" : source,
                "code", string.IsNullOrEmpty(code) ? null : code);
        }

        /// <summary>
        /// The CLI maps these exact spellings onto its own severity scale and treats anything
        /// else as a hint, so they are not free-form labels.
        /// </summary>
        private static string ReadSeverity(IEntry entry)
        {
            object raw;
            if (entry.TryGetValue(StandardTableKeyNames.ErrorSeverity, out raw) && raw != null)
            {
                if (raw is __VSERRORCATEGORY)
                {
                    switch ((__VSERRORCATEGORY)raw)
                    {
                        case __VSERRORCATEGORY.EC_ERROR: return "Error";
                        case __VSERRORCATEGORY.EC_WARNING: return "Warning";
                        default: return "Info";
                    }
                }

                var text = Convert.ToString(raw, CultureInfo.InvariantCulture);
                if (text.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0) return "Error";
                if (text.IndexOf("warning", StringComparison.OrdinalIgnoreCase) >= 0) return "Warning";
            }

            return "Info";
        }

        private static bool TryGetString(IEntry entry, string key, out string value)
        {
            object raw;
            if (entry.TryGetValue(key, out raw) && raw != null)
            {
                value = raw as string ?? Convert.ToString(raw, CultureInfo.InvariantCulture);
                return true;
            }

            value = null;
            return false;
        }

        private static int TryGetInt(IEntry entry, string key)
        {
            object raw;
            if (!entry.TryGetValue(key, out raw) || raw == null) return 0;
            if (raw is int) return (int)raw;

            try
            {
                return Convert.ToInt32(raw, CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                return 0;
            }
        }

        private static string UriToPath(string uri)
        {
            if (string.IsNullOrEmpty(uri)) return null;

            if (uri.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    return new Uri(uri).LocalPath;
                }
                catch (UriFormatException)
                {
                    // The CLI builds these as "file://" + path, which is not a legal URI on
                    // Windows because a drive letter needs a third slash.
                    return uri.Substring("file://".Length).TrimStart('/').Replace('/', '\\');
                }
            }

            return uri;
        }

        private static string PathToUri(string path)
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

        private static string NormalizePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;

            try
            {
                return System.IO.Path.GetFullPath(path);
            }
            catch (Exception)
            {
                return path;
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;

                foreach (var subscription in _subscriptions.Values)
                {
                    try { subscription.Handle.Dispose(); } catch (Exception) { }
                }

                _subscriptions.Clear();
            }

            var manager = _manager;
            if (manager != null)
            {
                try { manager.SourcesChanged -= OnSourcesChanged; } catch (Exception) { }
            }
        }

        /// <summary>One row, whatever kind of container it arrived in.</summary>
        private interface IEntry
        {
            bool TryGetValue(string key, out object value);
        }

        private sealed class TableEntry : IEntry
        {
            private readonly ITableEntry _entry;

            public TableEntry(ITableEntry entry) { _entry = entry; }

            public bool TryGetValue(string key, out object value)
            {
                try { return _entry.TryGetValue(key, out value); }
                catch (Exception) { value = null; return false; }
            }
        }

        private sealed class SnapshotEntry : IEntry
        {
            private readonly ITableEntriesSnapshot _snapshot;
            private readonly int _index;

            public SnapshotEntry(ITableEntriesSnapshot snapshot, int index)
            {
                _snapshot = snapshot;
                _index = index;
            }

            public bool TryGetValue(string key, out object value)
            {
                try { return _snapshot.TryGetValue(_index, key, out value); }
                catch (Exception) { value = null; return false; }
            }
        }

        private sealed class HandleEntry : IEntry
        {
            private readonly ITableEntryHandle _handle;

            public HandleEntry(ITableEntryHandle handle) { _handle = handle; }

            public bool TryGetValue(string key, out object value)
            {
                try { return _handle.TryGetValue(key, out value); }
                catch (Exception) { value = null; return false; }
            }
        }

        /// <summary>
        /// Receives diagnostics from one source. Producers publish in three different ways;
        /// Roslyn uses factories, the build uses snapshots, so all three are kept.
        /// </summary>
        private sealed class Sink : ITableDataSink
        {
            private readonly object _sinkGate = new object();
            private readonly List<ITableEntry> _entries = new List<ITableEntry>();
            private readonly List<ITableEntriesSnapshot> _snapshots = new List<ITableEntriesSnapshot>();
            private readonly List<ITableEntriesSnapshotFactory> _factories = new List<ITableEntriesSnapshotFactory>();

            public bool IsStable { get; set; }

            public IEnumerable<IEntry> Collect()
            {
                ITableEntry[] entries;
                ITableEntriesSnapshot[] snapshots;
                ITableEntriesSnapshotFactory[] factories;

                lock (_sinkGate)
                {
                    entries = _entries.ToArray();
                    snapshots = _snapshots.ToArray();
                    factories = _factories.ToArray();
                }

                var result = new List<IEntry>();

                foreach (var entry in entries) result.Add(new TableEntry(entry));

                foreach (var snapshot in snapshots) AddSnapshotEntries(result, snapshot);

                foreach (var factory in factories)
                {
                    ITableEntriesSnapshot snapshot = null;
                    try { snapshot = factory.GetCurrentSnapshot(); } catch (Exception) { }
                    if (snapshot != null) AddSnapshotEntries(result, snapshot);
                }

                return result;
            }

            private static void AddSnapshotEntries(List<IEntry> result, ITableEntriesSnapshot snapshot)
            {
                int count;
                try { count = snapshot.Count; } catch (Exception) { return; }

                for (int i = 0; i < count; i++)
                {
                    result.Add(new SnapshotEntry(snapshot, i));
                }
            }

            public void AddEntries(IReadOnlyList<ITableEntry> newEntries, bool removeAllEntries)
            {
                lock (_sinkGate)
                {
                    if (removeAllEntries) _entries.Clear();
                    if (newEntries != null) _entries.AddRange(newEntries);
                }
            }

            public void RemoveEntries(IReadOnlyList<ITableEntry> oldEntries)
            {
                lock (_sinkGate)
                {
                    if (oldEntries == null) return;
                    foreach (var entry in oldEntries) _entries.Remove(entry);
                }
            }

            public void ReplaceEntries(IReadOnlyList<ITableEntry> oldEntries, IReadOnlyList<ITableEntry> newEntries)
            {
                RemoveEntries(oldEntries);
                AddEntries(newEntries, false);
            }

            public void RemoveAllEntries()
            {
                lock (_sinkGate) { _entries.Clear(); }
            }

            public void AddSnapshot(ITableEntriesSnapshot snapshot, bool removeAllSnapshots)
            {
                lock (_sinkGate)
                {
                    if (removeAllSnapshots) _snapshots.Clear();
                    if (snapshot != null) _snapshots.Add(snapshot);
                }
            }

            public void RemoveSnapshot(ITableEntriesSnapshot snapshot)
            {
                lock (_sinkGate) { _snapshots.Remove(snapshot); }
            }

            public void ReplaceSnapshot(ITableEntriesSnapshot oldSnapshot, ITableEntriesSnapshot newSnapshot)
            {
                lock (_sinkGate)
                {
                    _snapshots.Remove(oldSnapshot);
                    if (newSnapshot != null) _snapshots.Add(newSnapshot);
                }
            }

            public void RemoveAllSnapshots()
            {
                lock (_sinkGate) { _snapshots.Clear(); }
            }

            public void AddFactory(ITableEntriesSnapshotFactory factory, bool removeAllFactories)
            {
                lock (_sinkGate)
                {
                    if (removeAllFactories) _factories.Clear();
                    if (factory != null) _factories.Add(factory);
                }
            }

            public void RemoveFactory(ITableEntriesSnapshotFactory factory)
            {
                lock (_sinkGate) { _factories.Remove(factory); }
            }

            public void ReplaceFactory(ITableEntriesSnapshotFactory oldFactory, ITableEntriesSnapshotFactory newFactory)
            {
                lock (_sinkGate)
                {
                    _factories.Remove(oldFactory);
                    if (newFactory != null) _factories.Add(newFactory);
                }
            }

            public void FactorySnapshotChanged(ITableEntriesSnapshotFactory factory)
            {
                // The current snapshot is fetched on demand in Collect, so nothing to store.
            }

            public void RemoveAllFactories()
            {
                lock (_sinkGate) { _factories.Clear(); }
            }
        }
    }
}
