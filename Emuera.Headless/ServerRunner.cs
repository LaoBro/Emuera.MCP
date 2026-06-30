using MinorShift.Emuera.Runtime.Utils;
using MinorShift.Emuera.Server;
using System;

namespace MinorShift.Emuera;

internal static class ServerRunner
{
    public static void Run(int port)
    {
        Console.Error.WriteLine($"[server] Emuera {AssemblyData.EmueraVersionText} 服务器模式启动");
        Console.Error.WriteLine($"[server] 监听端口: {port}");

        using var server = new HttpGameServer(port);
        server.Start();

        Console.Error.WriteLine("[server] 按 Enter 键停止服务器...");
        Console.ReadLine();
    }
}
