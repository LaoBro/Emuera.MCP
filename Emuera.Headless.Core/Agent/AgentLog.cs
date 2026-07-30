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
                // 默认启用（v == null）；设 EMUERA_AGENT_LOG=0/false 可关闭
                Enabled = v == null || v == "1" || v == "true";
            }
            catch (Exception ex) { Enabled = false; Console.Error.WriteLine("[agent-log] env read failed: " + ex.Message); }

            if (Enabled)
            {
                try
                {
                    string dir = AppDataPaths.Directory;
                    Directory.CreateDirectory(dir);
                    FilePath = AppDataPaths.CombinePath("agent.log");
                    // append:false 覆盖式，每次进程启动清空，只保留最后一次运行日志
                    _writer = new StreamWriter(FilePath, append: false) { AutoFlush = false };
                    AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
                }
                catch (Exception ex) { FilePath = null; _writer = null; Console.Error.WriteLine("[agent-log] file init failed: " + ex.Message); }
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
            catch (Exception) { /* 写日志失败不能再调 AgentLog，避免递归 */ }
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
            catch (Exception) { /* ProcessExit 里调日志/Console 不安全，吞掉 */ }
        }

        public void Dispose()
        {
            OnProcessExit(this, EventArgs.Empty);
            AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
        }
    }
}
