using System;
using System.Collections.Generic;
using System.ComponentModel;
using Microsoft.VisualStudio.Shell;

namespace ClaudeCodeVS
{
    /// <summary>
    /// Tools &gt; Options &gt; Claude Code &gt; General.
    /// </summary>
    public sealed class ClaudeCodeOptions : DialogPage
    {
        private bool _enabled = true;
        private bool _trackSelection = true;
        private bool _enableDebuggerBridge = true;
        private int _debuggerPort = 8375;
        private string _ideName = "Visual Studio";
        private string _additionalWorkspaceFolders = string.Empty;

        [Category("Connection")]
        [DisplayName("Enable the Claude Code bridge")]
        [Description("When off, Visual Studio publishes no lock file and the /ide command will not see it. Takes effect after a restart of Visual Studio.")]
        public bool Enabled
        {
            get { return _enabled; }
            set { _enabled = value; }
        }

        [Category("Connection")]
        [DisplayName("IDE name")]
        [Description("The label shown in the /ide picker. Must contain 'Visual Studio' so that stale lock files can be recognised and cleaned up.")]
        public string IdeName
        {
            get { return string.IsNullOrWhiteSpace(_ideName) ? "Visual Studio" : _ideName; }
            set { _ideName = value; }
        }

        [Category("Connection")]
        [DisplayName("Additional workspace folders")]
        [Description("Semicolon separated. Claude Code connects automatically only when one of the advertised folders contains the directory the CLI was started from. Add the repository root here when the solution lives in a subfolder of it.")]
        public string AdditionalWorkspaceFolders
        {
            get { return _additionalWorkspaceFolders ?? string.Empty; }
            set { _additionalWorkspaceFolders = value; }
        }

        [Category("Debugger")]
        [DisplayName("Enable debugger control")]
        [Description("Exposes breakpoints, stepping and variable inspection on a second MCP endpoint, so that Claude Code can drive a debug session. Takes effect after a restart of Visual Studio.")]
        public bool EnableDebuggerBridge
        {
            get { return _enableDebuggerBridge; }
            set { _enableDebuggerBridge = value; }
        }

        [Category("Debugger")]
        [DisplayName("Debugger endpoint port")]
        [Description("Fixed port for the debugger endpoint. It has to be fixed because it appears in the MCP server configuration, which must survive restarts. Change it only on a port conflict.")]
        public int DebuggerPort
        {
            get { return _debuggerPort < 1024 || _debuggerPort > 65535 ? 8375 : _debuggerPort; }
            set { _debuggerPort = value; }
        }

        [Category("Editor")]
        [DisplayName("Send the editor selection")]
        [Description("Keeps Claude Code aware of the text selected in the active editor. Polling only runs while a client is attached.")]
        public bool TrackSelection
        {
            get { return _trackSelection; }
            set { _trackSelection = value; }
        }

        public IEnumerable<string> GetAdditionalWorkspaceFolders()
        {
            var raw = AdditionalWorkspaceFolders;
            if (string.IsNullOrWhiteSpace(raw)) yield break;

            foreach (var part in raw.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = part.Trim().Trim('"');
                if (trimmed.Length > 0) yield return trimmed;
            }
        }
    }
}
