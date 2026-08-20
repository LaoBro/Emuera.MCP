namespace MinorShift.Emuera.GameView
{
    /// <summary>
    /// 封装 EmueraConsole 中供 Agent 轮询消费的状态视图。
    /// 核心语义为"读后清零"：调用方读取标记后由接口实现负责清零，
    /// 避免消费方遗漏清零导致重复刷新。
    /// </summary>
    internal interface IConsoleStateView
    {
        bool ConsumeNeedFullRefresh();
        bool IsGameExited { get; }
        int DisplayLineCount { get; }
    }
}
