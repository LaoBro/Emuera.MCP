using MinorShift.Emuera.Runtime.Utils;
using MinorShift.Emuera.Server;
using MinorShift.Emuera.Terminal.Platform;
using System;
using System.Threading.Tasks;

namespace MinorShift.Emuera;

internal static class ServerRunner
{
    public static async Task RunAsync(int port, ITerminalSetup terminalSetup)
    {
        Console.Error.WriteLine($"[server] Emuera {AssemblyData.EmueraVersionText} 服务器模式启动");
        Console.Error.WriteLine($"[server] 监听端口: {port}");

        using var server = new KestrelGameServer(port, terminalSetup);
        await server.StartAsync();
        await server.WaitForShutdownAsync();
    }
}
