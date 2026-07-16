namespace MinorShift.Emuera.GameView;

/// <summary>
/// <see cref="CliRedrawCoordinator"/> 的渲染协作面（"interface 即测试面"）。
/// 协调器只经这些窄接口触达四个重绘协作者，测试可用 recording fake 替换，
/// 断言编排顺序与按 <see cref="RedrawKind"/> 的调用差异，无需真实 VT 屏幕 / EmueraConsole。
/// 具体类 <see cref="TerminalRenderer"/> / <see cref="CountdownRenderer"/> /
/// <see cref="ScrollStatusBarRenderer"/> / <see cref="ButtonSelectionMode"/> 各自实现对应接口。
/// </summary>

internal interface IRedrawRenderer
{
    void FullRefresh(string reason = "?");
    /// <summary>帧级刷新。返回 true 表示内容确有变更并重绘；false 表示无变化（no-op 帧），调用方可跳过 chrome 同步。</summary>
    bool FlushBuffer();
}

internal interface ICountdownRenderer
{
    void Update(long elapsedMs);
    void Overwrite(string newText);
    void Reset();
}

internal interface IScrollStatusBar
{
    void Render(int offset);
}

internal interface IButtonSelection
{
    void SyncButtonState();
    void RefreshButtonRegionsFromSnapshot(VtInputHandler vtInput, bool force = false);
}
