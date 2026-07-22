/**
 * MAUI WebView 桥接工具——issue 07 / spec ID7。
 *
 * Vue 端按 `window.location` 判断 MAUI 环境（不注入 `__MAUI__` flag，消除注入 race）：
 * - `ms-appx-web:` 协议 → Windows MAUI packaged（MSIX 包内）
 * - `file:` 协议 → Android MAUI（`file:///android_asset/`）
 * - `https:` 协议 + host `app.local` → Windows MAUI unpackaged（WebView2 虚拟主机映射）
 * - 其他 `http:` / `https:` → HTTP 模式（开发/生产浏览器模式）
 *
 * MAUI 模式下：
 * 1. 注册 `window.__emueraOnTurn = (turn) => applyTurn(JSON.stringify(turn))`——
 *    C# 侧 `IJsBridge.PostTurn` 调 `window.__emueraOnTurn(turnJson)` 作为 JS 字面量传参（JSON ⊂ JS 字面量），
 *    Vue 端 `JSON.stringify` 还原为字符串后调 `game.applyTurn(rawJson)` 复用 HTTP 模式的协议消费链路。
 * 2. `postInput` 用 feature detection 选平台——
 *    Windows: `window.chrome.webview.postMessage(json)`（CoreWebView2 WebMessageReceived）
 *    Android: `window.emueraBridge.postMessage(json)`（AddJavascriptInterface 注册的桥接对象）
 * 3. 启动后 `postMessage(JSON.stringify({type:'ready'}))`——
 *    C# 侧 `BridgeHost.OnInputFromJs` 收到后写日志（T07），T08 接入游戏循环后启动 `GameLoopComposer.RunAsync`。
 * 4. issue 09 文件选择器：`pickGameFolder()` 请求 C# 弹原生 FolderPicker →
 *    C# 经 `PostMessage` 推 `{"type":"folderPicked","path":...}` →
 *    `registerMessageHandler` 注册的 handler 收到后调 `loadGameFromPath(path)` →
 *    C# `HandleLoadGame` 触发 hot-swap reload（后台重新初始化运行时 + 重建 BridgeHost）。
 */

/**
 * Windows MAUI unpackaged 模式下的虚拟主机名（与 WindowsJsBridge.VirtualHostName 对齐）。
 * WebView2 的 SetVirtualHostNameToFolderMapping 把此主机映射到输出目录 wwwroot/。
 */
const MAUI_WINDOWS_VIRTUAL_HOST = 'app.local';

/**
 * 判断当前是否运行在 MAUI WebView 内（spec ID7）。
 *
 * 检查 `window.location`：
 * - `protocol === 'ms-appx-web:'` → Windows MAUI packaged（MSIX 包内）
 * - `protocol === 'file:'` → Android MAUI（`file:///android_asset/`）
 * - `protocol === 'https:' && hostname === 'app.local'` → Windows MAUI unpackaged（虚拟主机映射）
 * - 其他（`http:` / `https:` 非 app.local）→ HTTP 模式（浏览器 / Vite dev server）
 *
 * SSR / 非 browser 环境返 false（`window` 未定义）。
 */
export function isMauiEnvironment(): boolean {
  if (typeof window === 'undefined') return false;
  const { protocol, hostname } = window.location;
  if (protocol === 'ms-appx-web:' || protocol === 'file:') return true;
  // Windows unpackaged 模式：WebView2 虚拟主机映射 https://app.local/
  if (protocol === 'https:' && hostname === MAUI_WINDOWS_VIRTUAL_HOST) return true;
  return false;
}

/**
 * 向 C# 投递一条消息（JS → C# 方向）。
 *
 * Feature detection 选平台：
 * - `window.chrome.webview` 存在 → Windows，调 `chrome.webview.postMessage(json)`（CoreWebView2 WebMessageReceived）
 * - `window.emueraBridge` 存在 → Android，调 `emueraBridge.postMessage(json)`（AddJavascriptInterface 桥接对象）
 * - 两者都不存在 → 静默 no-op（MAUI 桥接未 Attach 时不抛错，避免 Vue 启动期 race）
 *
 * 与 C# `IJsBridge.InputReceived` 对齐——事件在 `BridgeHost.OnInputFromJs` 内识别 `{"type":"ready"}` / `{"type":"input","value":"..."}`。
 *
 * @param json 合法 JSON 字符串（调用方 `JSON.stringify` 后传入）。
 */
export function postInput(json: string): void {
  const w = window as any;
  if (w.chrome?.webview) {
    w.chrome.webview.postMessage(json);
    console.log('[mauiBridge] postInput via chrome.webview:', json);
  } else if (w.emueraBridge) {
    w.emueraBridge.postMessage(json);
    console.log('[mauiBridge] postInput via emueraBridge:', json);
  } else {
    // 两者都不存在时静默 no-op——MAUI 桥接未 Attach 时 Vue 可能已加载（race），不抛错让 Vue 继续渲染
    console.warn('[mauiBridge] postInput no bridge available (chrome.webview / emueraBridge both missing)');
  }
}

/**
 * 注册 C# → JS 的 turn 回调（spec ID5 / ID7）。
 *
 * C# 侧 `IJsBridge.PostTurn(turnJson)` 执行 `window.__emueraOnTurn(turnJson)`，
 * `turnJson` 作为 JS 字面量直接嵌入（JSON ⊂ JS 字面量，无需再 `JSON.stringify` 双重转义）。
 * Vue 端 handler 收到的是已解析的 JS 对象——`JSON.stringify` 还原为字符串后调 `applyTurn(rawJson)`，
 * 复用 HTTP 模式的 `parseTurnRecord → applyDiff` 协议消费链路（零改动）。
 *
 * @param handler 收到 turn JSON 字符串时的回调（通常 `(rawJson) => game.applyTurn(rawJson)`）。
 */
export function registerTurnHandler(handler: (turnJson: string) => void): void {
  (window as any).__emueraOnTurn = (turn: unknown) => {
    // C# 传 JS 字面量 → turn 已是 JS 对象 → 还原为字符串复用 applyTurn
    const rawJson = typeof turn === 'string' ? turn : JSON.stringify(turn);
    handler(rawJson);
  };
}

/**
 * 向 C# 发送 ready 信号（spec ID7）。
 *
 * Vue 启动后立即调用——C# 侧 `BridgeHost.OnInputFromJs` 收到 `{"type":"ready"}` 后：
 * - T07：写日志确认收到（本票验证项）
 * - T08：调 `BridgeHost.Start()` → `Task.Run(GameLoopComposer.RunAsync)` 启动游戏循环
 *
 * ready 是游戏循环启动的前置条件，保证第一帧 turn 不丢失（Vue 已准备好接收）。
 */
export function sendReady(): void {
  postInput(JSON.stringify({ type: 'ready' }));
}

/**
 * issue 09 文件选择器——请求 C# 弹出原生文件夹选择器。
 *
 * Vue 端调用后，C# `BridgeHost.HandlePickFolder` 调 `IJsBridge.PickFolderAsync`
 * （MAUI `FolderPicker.PickAsync`：Windows 用 Win32 dialog，Android 用 Storage Access Framework）。
 * 用户选中后，C# 经 `PostMessage` 推 `{"type":"folderPicked","path":...}` 回 Vue，
 * 由 `registerMessageHandler` 注册的 handler 接收。
 *
 * 用户取消时 C# 不推消息——Vue 端无需处理取消（原生 picker 模态结束后自然回到 UI）。
 */
export function pickGameFolder(): void {
  postInput(JSON.stringify({ type: 'pickFolder' }));
}

/**
 * issue 09 文件选择器——注册 C# → JS 的非 turn 消息回调。
 *
 * C# 侧 `IJsBridge.PostMessage(msgJson)` 执行 `window.__emueraOnMessage(msgJson)`，
 * `msgJson` 作为 JS 字面量直接嵌入（JSON ⊂ JS 字面量）。
 * Vue 端 handler 收到的是已解析的 JS 对象——按 `type` 字段分发：
 * - `{"type":"folderPicked","path":...}`——文件选择器成功，调 `loadGameFromPath(path)` 触发 hot-swap reload
 * - `{"type":"folderPicked","error":...}`——文件选择器失败，展示错误
 *
 * 与 `registerTurnHandler` 分流——turn 经 `__emueraOnTurn` 推 `applyTurn` 协议消费链路，
 * 非 turn 事件经 `__emueraOnMessage` 推此 handler，避免污染 turn 协议。
 *
 * @param handler 收到消息对象时的回调。
 */
export function registerMessageHandler(handler: (msg: unknown) => void): void {
  (window as any).__emueraOnMessage = (msg: unknown) => {
    handler(msg);
  };
}

/**
 * issue 09 文件选择器——请求 C# hot-swap reload 到指定游戏目录。
 *
 * Vue 端在 `folderPicked` 收到 path 后调此方法，C# `BridgeHost.HandleLoadGame` 收到后
 * 调 `_onReloadGame(path)` 回调，`MainPage.OnReloadGame` 在后台线程重新初始化运行时
 * （`EmueraRuntimeInitializer.Initialize` + `GamePaths.Validate`），成功后 UI 线程
 * `RecreateHost` + `Start`——Vue 已 ready，无需再等 ready 信号，新游戏循环首帧自然推来。
 *
 * @param path 用户选中的游戏目录绝对路径。
 */
export function loadGameFromPath(path: string): void {
  postInput(JSON.stringify({ type: 'loadGame', path }));
}
