/// <reference types="vitest/config" />
import { defineConfig } from 'vite';
import vue from '@vitejs/plugin-vue';

// Vite 配置：dev server 监听 5173，/ws 代理到 C# Kestrel server (localhost:8080)，
// 避免开发时浏览器跨域。生产构建产物 dist/ 由 C# csproj pre-build target 复制到 wwwroot/。
//
// Vitest 配置合并到同一文件（spec.md：「vitest.config.ts（可合并到 vite.config.ts）」）。
// test.environment='node' 因被测对象是协议层纯函数，无 DOM 依赖。
export default defineConfig({
  plugins: [vue()],
  server: {
    port: 5173,
    proxy: {
      // spec.md user story 1+2：WS 旁路端点，开发期由 Vite 代理到 C# Kestrel。
      '/ws': {
        target: 'ws://localhost:8080',
        ws: true,
        changeOrigin: true,
      },
      // spec.md user story 6：晚加入者先调 GET /snapshot 拿初始全屏状态。
      '/snapshot': 'http://localhost:8080',
      // C# KestrelGameServer 协议契约：WS 升级前必须先 POST /session 创建会话，
      // 否则 server 接受 WS 升级后立即下发关闭码 4004 + reason "No active session"。
      // 此代理让 dev 模式下 fetch('/session') 直达 8080，避免 CORS。
      '/session': 'http://localhost:8080',
    },
  },
  test: {
    environment: 'node',
    include: ['src/**/__tests__/**/*.test.ts'],
  },
});
