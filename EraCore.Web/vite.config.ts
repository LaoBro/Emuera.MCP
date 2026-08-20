/// <reference types="vitest/config" />
/// <reference types="node" />
import { defineConfig } from 'vite';
import vue from '@vitejs/plugin-vue';

// Vite 配置：dev server 监听 5173，/ws 代理到 C# Kestrel server (localhost:8080)，
// 避免开发时浏览器跨域。生产构建产物 dist/ 由 C# csproj pre-build target 复制到 wwwroot/。
//
// Vitest 配置合并到同一文件（spec.md：「vitest.config.ts（可合并到 vite.config.ts）」）。
// test.environment='node' 因被测对象是协议层纯函数，无 DOM 依赖。
//
// base 路径：默认 '/'（Headless 同源服务），MAUI 场景由 build/VueBuild.targets 经
// VITE_BASE 环境变量（值 './'）+ vite build --base=./ 命令行参数双保险传入。
// 这里读 process.env.VITE_BASE 作 fallback，让 vite.config.ts 自身可独立 npm run build。
//
// assetsDir='static'：生产模式前端构建产物由 C# Kestrel wwwroot 同源服务，但 C# 侧
// 已有游戏图片通道路由 /assets/{**path}（spec Q3/Q5）——若前端产物仍放 /assets/ 会被
// 该路由抢先匹配（已匹配 endpoint 时 StaticFileMiddleware 跳过），导致 JS/CSS 全部 404。
// 故前端静态产物放到 /static/，与游戏图片通道 /assets/ 彻底分离，互不遮蔽。
export default defineConfig({
  plugins: [vue()],
  base: process.env.VITE_BASE ?? '/',
  build: {
    assetsDir: 'static',
  },
  server: {
    port: 5173,
    proxy: {
      // spec.md user story 1+2：WS 旁路端点，开发期由 Vite 代理到 C# Kestrel。
      '/ws': {
        target: 'ws://localhost:8080',
        ws: true,
        changeOrigin: true,
      },
      // C# KestrelGameServer 全部 HTTP 端点——dev 模式下前端 fetch 同源 5173，
      // 经 Vite 代理到 8080，避免 CORS。生产模式前端由 C# wwwroot 同源服务，无此问题。
      // 端点列表（KestrelGameServer.MapRoutes）：
      //   /session(POST/DELETE)、/turn(GET)、/input(POST)、/config(GET)、/snapshot(GET)、
      //   /load-game(POST, issue 05)、/state(GET, issue 05)、/native/pick-directory(POST, issue 05)、
      //   /game/scan(POST, Web 游戏扫描)、/game/dirs(POST, 目录浏览)
      '/session': 'http://localhost:8080',
      '/turn': 'http://localhost:8080',
      '/input': 'http://localhost:8080',
      '/config': 'http://localhost:8080',
      '/snapshot': 'http://localhost:8080',
      '/load-game': 'http://localhost:8080',
      '/state': 'http://localhost:8080',
      '/native': 'http://localhost:8080',
      '/game': 'http://localhost:8080',
    },
  },
  test: {
    environment: 'node',
    include: ['src/**/__tests__/**/*.test.ts'],
  },
});
