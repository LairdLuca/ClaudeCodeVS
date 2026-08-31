using System;
using System.IO;
using System.Text;

namespace ClaudeCodeVS.Mcp
{
    /// <summary>
    /// The shared secret for the debugger endpoint.
    ///
    /// Unlike the IDE endpoint, whose token is regenerated per session and handed to the CLI
    /// through the lock file, this one has to survive restarts: it appears in a static MCP server
    /// configuration, and a token that changed every time Visual Studio started would break that
    /// configuration on every restart.
    ///
    /// It lives under the user profile, which is the same protection the lock file directory has.
    /// </summary>
    internal static class DebugTokenStore
    {
        public static string Directory
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ClaudeCodeVS");
            }
        }

        public static string FilePath
        {
            get { return Path.Combine(Directory, "debug-token"); }
        }

        public static string LoadOrCreate()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var existing = File.ReadAllText(FilePath).Trim();
                    if (existing.Length >= 32) return existing;
                }
            }
            catch (Exception ex)
            {
                Log.Error("Could not read the debugger token; a new one will be generated", ex);
            }

            var token = McpEndpoint.CreateToken();

            try
            {
                System.IO.Directory.CreateDirectory(Directory);
                File.WriteAllText(FilePath, token, new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                Log.Error("Could not persist the debugger token; it will change on the next restart", ex);
            }

            return token;
        }
    }
}
