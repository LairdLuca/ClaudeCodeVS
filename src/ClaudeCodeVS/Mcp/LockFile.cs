using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace ClaudeCodeVS.Mcp
{
    /// <summary>
    /// Publishes and retracts the discovery file that lets the Claude Code CLI find this IDE.
    ///
    /// The CLI scans a fixed directory for <c>*.lock</c> files and takes the port from the file
    /// name, not from the JSON body. The body carries the process id, the folders this IDE has
    /// open, the transport and the shared secret. Claude Code offers an IDE in <c>/ide</c> when
    /// one of the workspace folders contains its working directory.
    /// </summary>
    internal sealed class LockFile : IDisposable
    {
        private readonly object _gate = new object();
        private string _path;
        private int _port;
        private string _authToken;
        private string _ideName;

        /// <summary>
        /// Always the user profile copy. Claude Code searches <c>CLAUDE_CONFIG_DIR/ide</c> and,
        /// whenever that variable is set, also <c>~/.claude/ide</c>. Writing to the profile path
        /// therefore covers both configurations, and it does not depend on environment variables
        /// that Visual Studio may not have inherited.
        /// </summary>
        public static string IdeDirectory
        {
            get
            {
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                return Path.Combine(Path.Combine(home, ".claude"), "ide");
            }
        }

        public string Path_ { get { lock (_gate) { return _path; } } }

        public void Publish(int port, string authToken, string ideName, IList<string> workspaceFolders)
        {
            lock (_gate)
            {
                _port = port;
                _authToken = authToken;
                _ideName = ideName;
                _path = System.IO.Path.Combine(
                    IdeDirectory,
                    port.ToString(CultureInfo.InvariantCulture) + ".lock");
            }

            Refresh(workspaceFolders);
        }

        /// <summary>Rewrites the body, typically because a different solution was opened.</summary>
        public void Refresh(IList<string> workspaceFolders)
        {
            string path;
            Dictionary<string, object> body;

            lock (_gate)
            {
                if (_path == null) return;
                path = _path;
                body = Json.Obj(
                    "pid", Process.GetCurrentProcess().Id,
                    "workspaceFolders", workspaceFolders ?? new string[0],
                    "ideName", _ideName,
                    "transport", "ws",
                    "runningInWindows", true,
                    "authToken", _authToken);
            }

            try
            {
                Directory.CreateDirectory(IdeDirectory);
                File.WriteAllText(path, Json.Serialize(body), new UTF8Encoding(false));
                Log.Info("Lock file published at " + path);
            }
            catch (Exception ex)
            {
                Log.Error("Could not write the lock file at " + path, ex);
            }
        }

        /// <summary>
        /// Removes lock files left behind by Visual Studio instances that are no longer running.
        /// Claude Code prunes dead entries itself, but only when it happens to scan; clearing our
        /// own leftovers keeps the /ide picker honest after a crash.
        /// </summary>
        public static void RemoveStaleEntries()
        {
            try
            {
                if (!Directory.Exists(IdeDirectory)) return;

                foreach (var file in Directory.GetFiles(IdeDirectory, "*.lock"))
                {
                    try
                    {
                        var parsed = Json.ParseObject(File.ReadAllText(file));
                        var ideName = Json.GetString(parsed, "ideName");
                        if (ideName == null || ideName.IndexOf("Visual Studio", StringComparison.OrdinalIgnoreCase) < 0)
                        {
                            continue;
                        }

                        var pid = Json.GetInt(parsed, "pid");
                        if (pid.HasValue && IsProcessAlive(pid.Value)) continue;

                        File.Delete(file);
                        Log.Info("Removed the stale lock file " + file);
                    }
                    catch (Exception)
                    {
                        // A malformed or locked file is not worth failing startup over.
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("Could not scan the lock file directory", ex);
            }
        }

        private static bool IsProcessAlive(int pid)
        {
            try
            {
                using (var process = Process.GetProcessById(pid))
                {
                    return !process.HasExited;
                }
            }
            catch (ArgumentException)
            {
                return false;
            }
            catch (Exception)
            {
                return true; // Cannot tell; leave the file alone.
            }
        }

        public void Dispose()
        {
            string path;
            lock (_gate)
            {
                path = _path;
                _path = null;
            }

            if (path == null) return;

            try
            {
                if (File.Exists(path)) File.Delete(path);
                Log.Info("Lock file removed.");
            }
            catch (Exception ex)
            {
                Log.Error("Could not remove the lock file at " + path, ex);
            }
        }
    }
}
