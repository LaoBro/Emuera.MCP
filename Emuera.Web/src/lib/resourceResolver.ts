/**
 * 游戏资源 URL 解析抽象（issue 04，spec Q6「resolveResource 抽象」）。
 *
 * 协议只传游戏根相对路径（如 `img/portrait.png`）；前端统一经本函数拼可加载 URL：
 * - Web 分支：`/assets/{path}`——C# Kestrel 资源通道（issue 03，含消毒/白名单/缓存/CORS）
 * - MAUI 分支（issue 05）：Windows 用 `game.local` 虚拟主机映射、安卓用
 *   WebViewAssetLoader 自定义域——05 时在此加平台分支（isMauiEnvironment），
 *   共享组件零改动
 */
export function resolveResource(path: string): string {
  return `/assets/${path}`;
}
