using System;
using System.Globalization;
using System.Windows.Threading;
using ClaudeCodeVS.Mcp;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace ClaudeCodeVS.Ide
{
    /// <summary>What the caret and selection look like at one moment.</summary>
    internal sealed class SelectionSnapshot
    {
        public string FilePath;
        public int StartLine;
        public int StartCharacter;
        public int EndLine;
        public int EndCharacter;
        public string Text;
        public bool IsEmpty;

        public string Key
        {
            get
            {
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}|{1}:{2}-{3}:{4}",
                    FilePath, StartLine, StartCharacter, EndLine, EndCharacter);
            }
        }

        /// <summary>
        /// Zero-based positions, matching the shape the CLI parses for <c>selection_changed</c>.
        /// Visual Studio counts lines and columns from one, so both are shifted here.
        /// </summary>
        public object ToNotificationPayload()
        {
            return Json.Obj(
                "selection", Json.Obj(
                    "start", Json.Obj("line", StartLine, "character", StartCharacter),
                    "end", Json.Obj("line", EndLine, "character", EndCharacter)),
                "text", Text ?? string.Empty,
                "filePath", FilePath ?? string.Empty);
        }

        public object ToToolResultPayload()
        {
            return Json.Obj(
                "success", true,
                "filePath", FilePath ?? string.Empty,
                "text", Text ?? string.Empty,
                "selection", Json.Obj(
                    "start", Json.Obj("line", StartLine, "character", StartCharacter),
                    "end", Json.Obj("line", EndLine, "character", EndCharacter),
                    "isEmpty", IsEmpty));
        }
    }

    /// <summary>
    /// Publishes the editor selection to the connected CLI.
    ///
    /// This polls rather than subscribing to editor events. Getting change notifications for
    /// whichever view happens to be active means tracking view creation, activation and
    /// disposal across every window; reading the active document's selection on a timer is a
    /// fraction of the code for the same result. The cost is bounded by only running while a
    /// client is actually attached, and by comparing positions before touching the selected
    /// text, which is the one part that can be large.
    /// </summary>
    internal sealed class SelectionWatcher : IDisposable
    {
        private const int MaxTextLength = 64 * 1024;
        private const int PollIntervalMilliseconds = 300;

        private readonly DispatcherTimer _timer;
        private readonly Func<bool> _hasListeners;
        private readonly Action<SelectionSnapshot> _onChanged;
        private string _lastKey;
        private int _disposed;

        public SelectionWatcher(Func<bool> hasListeners, Action<SelectionSnapshot> onChanged)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            _hasListeners = hasListeners;
            _onChanged = onChanged;
            _timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle)
            {
                Interval = TimeSpan.FromMilliseconds(PollIntervalMilliseconds)
            };
            _timer.Tick += OnTick;
        }

        public void Start()
        {
            _timer.Start();
        }

        private void OnTick(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                if (_hasListeners != null && !_hasListeners())
                {
                    _lastKey = null;
                    return;
                }

                var snapshot = Capture(includeText: false);
                if (snapshot == null)
                {
                    _lastKey = null;
                    return;
                }

                if (string.Equals(snapshot.Key, _lastKey, StringComparison.Ordinal)) return;
                _lastKey = snapshot.Key;

                var full = Capture(includeText: true);
                if (full != null && _onChanged != null) _onChanged(full);
            }
            catch (Exception ex)
            {
                Log.Error("Selection tracking failed", ex);
            }
        }

        /// <summary>Reads the active document's selection. Must run on the UI thread.</summary>
        public static SelectionSnapshot Capture(bool includeText)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                var dte = Package.GetGlobalService(typeof(SDTE)) as DTE2;
                if (dte == null) return null;

                Document document;
                try
                {
                    document = dte.ActiveDocument;
                }
                catch (Exception)
                {
                    // ActiveDocument throws rather than returning null when nothing is open.
                    return null;
                }

                if (document == null) return null;

                var selection = document.Selection as TextSelection;
                if (selection == null) return null;

                var top = selection.TopPoint;
                var bottom = selection.BottomPoint;
                if (top == null || bottom == null) return null;

                var snapshot = new SelectionSnapshot
                {
                    FilePath = document.FullName,
                    StartLine = Math.Max(0, top.Line - 1),
                    StartCharacter = Math.Max(0, top.LineCharOffset - 1),
                    EndLine = Math.Max(0, bottom.Line - 1),
                    EndCharacter = Math.Max(0, bottom.LineCharOffset - 1),
                    IsEmpty = selection.IsEmpty
                };

                if (includeText && !selection.IsEmpty)
                {
                    var text = selection.Text ?? string.Empty;
                    snapshot.Text = text.Length > MaxTextLength ? text.Substring(0, MaxTextLength) : text;
                }
                else
                {
                    snapshot.Text = string.Empty;
                }

                return snapshot;
            }
            catch (Exception)
            {
                return null;
            }
        }

        public void Dispose()
        {
            if (System.Threading.Interlocked.Exchange(ref _disposed, 1) != 0) return;

            try
            {
                _timer.Tick -= OnTick;
                _timer.Stop();
            }
            catch (Exception)
            {
                // Shutting down.
            }
        }
    }
}
