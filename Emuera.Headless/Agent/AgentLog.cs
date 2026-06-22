using System;
using System.IO;

namespace MinorShift.Emuera.GameView
{
    internal sealed class AgentLog
    {
        public readonly bool Enabled;
        public readonly string? FilePath;

        private static readonly Lazy<AgentLog> s_instance = new(() => new AgentLog());

        public static AgentLog Instance => s_instance.Value;

        private AgentLog()
        {
            try
            {
                string? v = Environment.GetEnvironmentVariable("EMUERA_AGENT_LOG");
                Enabled = v == "1" || v == "true";
            }
            catch { Enabled = false; }

            if (Enabled)
            {
                try
                {
                    string exeDir = Program.ExeDir;
                    if (string.IsNullOrEmpty(exeDir)) exeDir = AppContext.BaseDirectory;
                    string dir = Path.Combine(exeDir, "debug");
                    Directory.CreateDirectory(dir);
                    FilePath = Path.Combine(dir, "agent.log");
                }
                catch { FilePath = null; }
            }
        }

        public void Write(string message)
        {
            if (!Enabled || FilePath == null) return;
            try { File.AppendAllText(FilePath, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}"); }
            catch { }
        }
    }
}