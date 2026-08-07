using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using MinorShift.Emuera.GameView;
using MinorShift.Emuera.Runtime.Utils;
using MinorShift.Emuera.Terminal.Platform;

namespace MinorShift.Emuera.Server;

/// <summary>
/// Session 生命周期管理（issue 03/05 重构）——从 <see cref="KestrelGameServer"/> 拆出。
/// 串行化 /session、/load-game、DELETE /session 三个会话变更操作与 WS 订阅的
/// accept→re-check→subscribe 原子序列。
///
/// 锁语义（与原 KestrelGameServer 一致）：
/// - 本类内所有变更操作在 <c>_sessionLock</c>（SemaphoreSlim(1,1)）内完成；
/// - <c>_session</c> 是 volatile，只读操作（/input、/snapshot 等）可无锁读；
/// - <c>_sessionHub</c> 与 <c>_session</c> 同生命周期，仅在锁内赋值/置空；
/// - 方法返回 outcome record 而非 IResult，测试可直接断言状态。
/// </summary>
internal sealed class SessionRegistry
{
    private readonly SemaphoreSlim _sessionLock = new(1, 1);
    private volatile Session? _session;
    private OutputHub? _sessionHub;
    private readonly ITerminalSetup _terminalSetup;
    private readonly GameConfigService _configService;

    public SessionRegistry(ITerminalSetup terminalSetup, GameConfigService configService)
    {
        _terminalSetup = terminalSetup ?? throw new ArgumentNullException(nameof(terminalSetup));
        _configService = configService ?? throw new ArgumentNullException(nameof(configService));
    }

    /// <summary>当前 session 引用（volatile 读）——仅供只读端点（/turn /input /snapshot）使用。</summary>
    public Session? CurrentSession => _session;

    /// <summary>
    /// POST /session（迁移自原 HandleCreateSessionAsync）。
    /// - <c>_session == null</c>（未加载游戏）→ <see cref="SessionCreateStatus.NoGameLoaded"/>
    /// - 已有活跃 session → <see cref="SessionCreateStatus.Conflict"/>
    /// - 旧 session 已结束（HasEnded）→ dispose 后重建新 session → <see cref="SessionCreateStatus.Created"/>
    /// </summary>
    public async Task<SessionCreateResult> CreateNewSessionAsync()
    {
        await _sessionLock.WaitAsync();
        try
        {
            // T-025 D4/D16：空闲态守卫——_session == null 表示尚未加载游戏（POST /load-game
            // 成功才会建 session）。503 语义早于「已有活跃会话 409」判断。
            if (_session == null)
                return new SessionCreateResult(SessionCreateStatus.NoGameLoaded, null, default, null);

            if (!_session.HasEnded)
                return new SessionCreateResult(SessionCreateStatus.Conflict, null, default, null);

            DestroySession();

            // issue 03：SessionRegistry 自持 OutputHub 引用，HttpSessionIO 只接 IOutputBroadcaster 抽象。
            var hub = new OutputHub();
            var io = new HttpSessionIO(hub);
            _sessionHub = hub;
            _session = new Session(io, _terminalSetup, _configService.Current);
            _session.Start();
            return new SessionCreateResult(SessionCreateStatus.Created, _session.Id, _session.CreatedAt, _session.StateString);
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    /// <summary>
    /// DELETE /session（迁移自原 HandleDeleteSessionAsync）。
    /// 存在 session → dispose 并置空 → <see cref="SessionDeleteStatus.Removed"/>；否则 NotFound。
    /// </summary>
    public async Task<SessionDeleteResult> DeleteSessionAsync()
    {
        await _sessionLock.WaitAsync();
        try
        {
            if (_session != null)
            {
                DestroySession();
                return new SessionDeleteResult(SessionDeleteStatus.Removed);
            }
            return new SessionDeleteResult(SessionDeleteStatus.NotFound);
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    /// <summary>
    /// POST /load-game（迁移自原 HandleLoadGameAsync）——原子重载游戏目录（issue 05）。
    /// 全程序在 _sessionLock 内完成（spec L224-229）：
    /// 1. 校验路径（拆旧前拦路径级错误）——GamePaths.Resolve + Validate，失败抛 GamePathValidationException
    /// 2. dispose 旧 Session（若有）
    /// 3. Preload.Clear()
    /// 4. GamePaths.Resolve 已在步骤 1 完成——GamePaths.Current 指向新目录
    /// 5. 重建 ConfigData：GameConfigService.Reload(exeDir)——LoadConfig + SetCurrent + 字段替换
    /// 6. Preload.Load 由新 Session 的 ConsoleStateManager.Initialize 异步执行（issue 11 D2）
    /// 7. 建新 Session（传入新 ConfigData）
    /// 8. 返 Success(sessionId, state, gameDir)
    ///
    /// 错误契约（spec L229）：
    /// - 路径级错误（DIR_NOT_FOUND / MISSING_CSV / MISSING_ERB）→ <see cref="LoadGameStatus.PathError"/>
    /// - 加载级失败（ConfigData.LoadConfig 等同步步骤异常、未预期异常）→ <see cref="LoadGameStatus.LoadFailed"/>
    ///   异步阶段（Preload.Load / Process.Initialize）失败由 ConsoleStateManager 置 State=Error。
    /// </summary>
    public async Task<LoadGameResult> ReplaceForLoadGameAsync(string gameDir)
    {
        await _sessionLock.WaitAsync();
        try
        {
            // 1. 校验路径——GamePathValidationException 转 PathError。
            //
            // 注意：GamePaths.Resolve(gameDir) 内部会先把静态 Current 指向新 paths 再返回。
            // 若 Validate 抛异常，Current 已被污染——spec L229 要求"拆旧前拦路径级错误"，
            // 即 server 状态不应被失败的 /load-game 改动。故在 catch 内回滚 Current 到旧值
            // （旧值 = 进入 /load-game 前的 GamePaths.Current，可能为 null 启动期场景）。
            GamePaths paths;
            GamePaths? previousPaths = GamePaths.Current;
            try
            {
                paths = GamePaths.Resolve(gameDir, new FileSystemGameDirAccessor());
                paths.Validate();
            }
            catch (GamePathValidationException ex)
            {
                // 回滚 Current 到校验前的值——避免 GET /state 返回被拒绝的非法路径
                if (previousPaths != null)
                {
                    GamePaths.SetCurrent(previousPaths);
                }
                return LoadGameResult.PathError(ex.Code, ex.Message);
            }

            // 2. dispose 旧 Session（若有）——Dispose 等待旧 GameLoopAsync 退出，scope 自动清理
            DestroySession();

            // 3. Preload.Clear —— 清空旧 ERB/CSV 文件缓存
            Preload.Clear();

            // 4. GamePaths.Resolve 已在步骤 1 完成——GamePaths.Current 指向新目录

            // 5. 重建 ConfigData 并 SetCurrent（HTTP 线程 AsyncLocal——主要供新 Session 异步
            //    Preload.Load 间接读）。注意：SetCurrent 与下方 new Session 必须同一 async 流。
            var newConfig = _configService.Reload(paths.ExeDir);

            // 6. Preload.Load 已下沉到 ConsoleStateManager.Initialize（issue 11 D2）——
            //    新 Session.Start() 排 GameLoopAsync 到独立 Task，_loading=true 让 StateString 返 "Loading"。

            // 7. 建新 Session——GameLoopAsync 内 GameLoopComposer.OpenScope(_configData) 注入新配置
            var hub = new OutputHub();
            var io = new HttpSessionIO(hub);
            _sessionHub = hub;
            var newSession = new Session(io, _terminalSetup, newConfig);
            newSession.Start();
            _session = newSession;

            // 8. Success——state="Loading"（issue 11 D1 落地后自动生效）
            return LoadGameResult.Success(newSession.Id, newSession.StateString, paths.ExeDir);
        }
        catch (Exception ex)
        {
            // 兜底：未预期异常归为 LOAD_FAILED
            EmueraLog.Error("load-game", "unexpected exception");
            EmueraLog.Error("load-game", ex.ToString());
            return LoadGameResult.LoadFailed(ex.Message);
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    /// <summary>
    /// GET /ws 订阅（迁移自原 HandleWebSocketAsync 的锁内段）。
    /// 锁内完成 accept→re-check→subscribe 原子序列，避免 session 被另一线程
    /// DELETE+POST 替换后仍从旧 hub 订阅。
    /// 无活跃 session（或已结束）→ 返回 null（调用方下发 4004 关闭码）。
    /// </summary>
    public async Task<WsSubscription?> TryGetWsSubscriptionAsync()
    {
        await _sessionLock.WaitAsync();
        try
        {
            var session = _session;
            if (session is { HasEnded: false })
            {
                // issue 03：hub 引用源为 SessionRegistry 自持的 _sessionHub——
                // 与 _session 在锁内同生命周期赋值/置空，此处持锁读取安全。
                var hub = _sessionHub;
                var reader = hub?.Subscribe();
                if (reader != null)
                    return new WsSubscription(session, reader, hub!);
            }
            return null;
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    /// <summary>整体释放（服务器关闭，迁移自原 Dispose 的 session 段）——先 join 游戏循环再释放。</summary>
    public void DisposeAll()
    {
        _sessionLock.Wait();
        try
        {
            DestroySession();
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    /// <summary>
    /// 销毁当前 session（若有）：Dispose（join 游戏循环，scope 自动清理）并置空 _session/_sessionHub。
    /// 三态同生命周期，仅应在持 _sessionLock 时调用。
    /// </summary>
    private void DestroySession()
    {
        _session?.Dispose();
        _session = null;
        _sessionHub = null;
    }
}

/// <summary>POST /session 结果状态。</summary>
internal enum SessionCreateStatus
{
    /// <summary>未加载游戏（_session == null）→ HTTP 503。</summary>
    NoGameLoaded,
    /// <summary>已有活跃 session → HTTP 409。</summary>
    Conflict,
    /// <summary>已结束 session 重建成功 → HTTP 201。</summary>
    Created,
}

/// <summary>POST /session 结果。</summary>
internal readonly record struct SessionCreateResult(SessionCreateStatus Status, string? SessionId, DateTimeOffset CreatedAt, string? State);

/// <summary>DELETE /session 结果状态。</summary>
internal enum SessionDeleteStatus
{
    /// <summary>已删除 → HTTP 200 {removed:true}。</summary>
    Removed,
    /// <summary>无 session → HTTP 404 {removed:false}。</summary>
    NotFound,
}

/// <summary>DELETE /session 结果。</summary>
internal readonly record struct SessionDeleteResult(SessionDeleteStatus Status);

/// <summary>POST /load-game 结果状态。</summary>
internal enum LoadGameStatus
{
    /// <summary>路径级错误（DIR_NOT_FOUND / MISSING_CSV / MISSING_ERB）→ HTTP 400。</summary>
    PathError,
    /// <summary>加载级失败/未预期异常 → HTTP 500 LOAD_FAILED。</summary>
    LoadFailed,
    /// <summary>成功 → HTTP 200。</summary>
    Success,
}

/// <summary>POST /load-game 结果。</summary>
internal readonly record struct LoadGameResult(LoadGameStatus Status, string? Code, string? Message, string? SessionId, string? State, string? GameDir)
{
    public static LoadGameResult PathError(string code, string message) => new(LoadGameStatus.PathError, code, message, null, null, null);
    public static LoadGameResult LoadFailed(string message) => new(LoadGameStatus.LoadFailed, null, message, null, null, null);
    public static LoadGameResult Success(string sessionId, string state, string gameDir) => new(LoadGameStatus.Success, null, null, sessionId, state, gameDir);
}

/// <summary>WS 订阅三元组：session + 独占 reader + 所属 hub（finally 退订用）。</summary>
internal sealed record WsSubscription(Session Session, ChannelReader<string> Reader, OutputHub Hub);
