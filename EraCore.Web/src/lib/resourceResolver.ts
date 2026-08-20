/**
 * 游戏资源 URL 解析抽象（issue 04，spec Q6「resolveResource 抽象」）。
 *
 * 协议只传游戏根相对路径（如 `img/portrait.png`）；前端统一经本函数拼可加载 URL：
 * - Web 分支：`/assets/{path}`——C# Kestrel 资源通道（issue 03，含消毒/白名单/缓存/CORS）
 * - MAUI 分支（issue 05）：`https://game.local/{path}`——Windows 用 WebResourceRequested 拦截、
 *   安卓用 WebViewAssetLoader PathHandler（两端共用 AssetChannel，CORS/缓存头齐备，
 *   srcm canvas 读像素可用）。`isMauiEnvironment()` 按 location 判断，无注入 flag race。
 */
import { isMauiEnvironment } from './mauiBridge';

/** 与 C# 侧 GameAssetConstants.VirtualHostName 对齐（Windows/安卓统一域）。 */
export const MAUI_GAME_VIRTUAL_HOST = 'game.local';

export function resolveResource(path: string): string {
  if (isMauiEnvironment()) {
    return `https://${MAUI_GAME_VIRTUAL_HOST}/${path}`;
  }
  return `/assets/${path}`;
}
