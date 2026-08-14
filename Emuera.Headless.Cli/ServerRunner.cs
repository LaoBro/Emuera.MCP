#if !ANDROID_NO_SERVER
using MinorShift.Emuera.GameView;
using MinorShift.Emuera.Runtime.Utils;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Server;
using MinorShift.Emuera.Terminal.Platform;
using System;
using System.Threading.Tasks;

namespace MinorShift.Emuera;

internal static class ServerRunner
{
    public static async Task RunAsync(int port, ITerminalSetup terminalSetup, ConfigData configData, bool overrideDisplayReport = false)
    {
        // Server 模式无交互画面，终端即其日志——把终端阈值提到 Info，
        // 让启动横幅/端口提示等用户可见输出正常显示（CLI 交互保持默认 Warn）。
        EmueraLog.SetTerminalLevel(EmueraLog.Level.Info);
        EmueraLog.Info("server", $"Emuera {AssemblyData.EmueraVersionText} 服务器模式启动");
        EmueraLog.Info("server", $"监听端口: {port}");

        using var server = new KestrelGameServer(port, terminalSetup, configData, overrideDisplayReport);
        await server.StartAsync();
        await server.WaitForShutdownAsync();
    }
}
#endif
