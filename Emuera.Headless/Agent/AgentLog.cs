using System;
using System.IO;

namespace MinorShift.Emuera.GameView
{
    internal sealed class AgentLog : IDisposable
    {
        public readonly bool Enabled;
        public readonly string? FilePath;

        private static readonly Lazy<AgentLog> s_instance = new(() => new AgentLog());

        public static AgentLog Instance => s_instance.Value;

        private readonly StreamWriter? _writer;
        private readonly object _lock = new();
        private bool _disposed;

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
                    _writer = new StreamWriter(FilePath, append: true) { AutoFlush = false };
                    AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
                }
                catch { FilePath = null; _writer = null; }
            }
        }

        public void Write(string message)
        {
            if (!Enabled || _writer == null) return;
            try
            {
                lock (_lock)
                {
                    if (_disposed) return;
                    _writer.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {message}");
                }
            }
            catch { }
        }

        private void OnProcessExit(object? sender, EventArgs e)
        {
            try
            {
                lock (_lock)
                {
                    if (_disposed) return;
                    _writer?.Flush();
                    _writer?.Dispose();
                    _disposed = true;
                }
            }
            catch { }
        }

        public void Dispose()
        {
            OnProcessExit(this, EventArgs.Empty);
            AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
        }
    }
}
