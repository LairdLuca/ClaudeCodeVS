using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace ClaudeCodeVS
{
    /// <summary>
    /// Logging with no dependency on the Visual Studio shell: the package installs a sink that
    /// writes to an Output window pane, and everything is also appended to a file. Keeping the
    /// shell out of here is what lets the protocol layer run outside Visual Studio.
    ///
    /// The file matters more than it looks. When the extension misbehaves inside Visual Studio,
    /// the Output pane can only be read by a human sitting in front of the IDE, which makes a
    /// diagnosis cost a full rebuild-reinstall-restart cycle. The file can be read from anywhere.
    /// </summary>
    internal static class Log
    {
        private const long MaxFileBytes = 2 * 1024 * 1024;

        private static readonly object FileGate = new object();
        private static volatile Action<string> _sink;
        private static string _filePath;
        private static bool _fileDisabled;

        public static string FilePath
        {
            get
            {
                if (_filePath == null)
                {
                    var directory = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "ClaudeCodeVS");
                    _filePath = Path.Combine(directory, "claude-code-vs.log");
                }

                return _filePath;
            }
        }

        public static void SetSink(Action<string> sink)
        {
            _sink = sink;
        }

        public static void Info(string message)
        {
            Write("INFO ", message);
        }

        public static void Error(string message, Exception exception)
        {
            Write("ERROR", exception == null ? message : message + ": " + exception);
        }

        private static void Write(string level, string message)
        {
            var line = string.Format(
                CultureInfo.InvariantCulture,
                "[{0:yyyy-MM-dd HH:mm:ss}] {1} {2}",
                DateTime.Now,
                level,
                message);

            WriteToFile(line);

            var sink = _sink;
            if (sink != null)
            {
                try
                {
                    sink(line);
                    return;
                }
                catch (Exception)
                {
                    // Fall through to the debugger so a broken sink never swallows diagnostics.
                }
            }

            Debug.WriteLine("[ClaudeCodeVS] " + line);
        }

        private static void WriteToFile(string line)
        {
            if (_fileDisabled) return;

            try
            {
                lock (FileGate)
                {
                    var path = FilePath;
                    var directory = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

                    // Keep one generation so a long-running Visual Studio session cannot grow
                    // the log without bound.
                    var info = new FileInfo(path);
                    if (info.Exists && info.Length > MaxFileBytes)
                    {
                        var previous = path + ".1";
                        if (File.Exists(previous)) File.Delete(previous);
                        File.Move(path, previous);
                    }

                    File.AppendAllText(path, line + Environment.NewLine, new UTF8Encoding(false));
                }
            }
            catch (Exception)
            {
                // A log that cannot be written must never take the extension down with it.
                _fileDisabled = true;
            }
        }
    }
}
