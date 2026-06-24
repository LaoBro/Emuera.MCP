namespace MinorShift.Emuera.GameView
{
    /// <summary>
    /// 封装 EmueraConsole 中供 Agent 轮询消费的状态视图。
    /// 核心语义为"读后清零"：调用方读取标记后由接口实现负责清零，
    /// 避免消费方遗漏清零导致重复刷新。同时封装 agent 缓冲区的追加写入，
    /// 消除 Agent 层对 EmueraConsole internal 字段的直接依赖，便于 mock 测试。
    /// </summary>
    internal interface IConsoleStateView
    {
        /// <summary>
        /// 消费"需要全量刷新"标记。返回 true 表示需要刷新，并自动清零标记。
        /// </summary>
        bool ConsumeNeedFullRefresh();

        /// <summary>
        /// 消费"待擦除行数"。返回当前待擦除行数，并自动清零计数。
        /// 返回 0 表示无需擦除。
        /// </summary>
        int ConsumePendingEraseRows();

        /// <summary>
        /// 追加文本到 agent 缓冲区（不追踪行数，用于输入回显等非显示行）。
        /// newLine=true 追加换行，false 不换行。
        /// </summary>
        void AppendToAgentBuffer(string text, bool newLine);
    }
}
