using MinorShift.Emuera.Runtime.Utils;
using MinorShift.Emuera.Server;
using System;
using System.Threading;

namespace MinorShift.Emuera;

internal static class ServerRunner
{
    public static void Run(int port)
    {
        Console.Error.WriteLine($"[server] Emuera {AssemblyData.EmueraVersionText} 服务器模式启动");
        Console.Error.WriteLine($"[server] 监听端口: {port}");

        using var server = new HttpGameServer(port);
        server.Start();

        using var shutdown = new ManualResetEventSlim(false);
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            server.Dispose();
            shutdown.Set();
        };

        Console.Error.WriteLine("[server] 按 Enter 键或 Ctrl+C 停止服务器...");
        shutdown.Wait();
    }
}
