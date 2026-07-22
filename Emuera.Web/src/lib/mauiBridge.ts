/**
 * MAUI WebView 桥接工具——issue 07 / spec ID7。
 *
 * Vue 端按 `window.location.protocol` 判断 MAUI 环境（不注入 `__MAUI__` flag，消除注入 race）：
 * - `ms-appx-web:` / `file:` → MAUI（Windows / Android）
 * - `http:` / `https:` → HTTP（开发/生产浏览器模式）
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
 */

/**
 * 判断当前是否运行在 MAUI WebView 内（spec ID7）。
 *
 * 检查 `window.location.protocol`：
 * - `ms-appx-web:` → Windows MAUI（WinUI WebView2 + MSIX 包）
 * - `file:` → Android MAUI（Android WebView + `file:///android_asset/`）
 * - 其他（`http:` / `https:`）→ HTTP 模式（浏览器 / Vite dev server）
 *
 * SSR / 非 browser 环境返 false（`window` 未定义）。
 */
export function isMauiEnvironment(): boolean {
  if (typeof window === 'undefined') return false;
  const proto = window.location.protocol;
  return proto === 'ms-appx-web:' || proto === 'file:';
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
  } else if (w.emueraBridge) {
    w.emueraBridge.postMessage(json);
  }
  // 两者都不存在时静默 no-op——MAUI 桥接未 Attach 时 Vue 可能已加载（race），不抛错让 Vue 继续渲染
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
