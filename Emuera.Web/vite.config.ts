import { defineConfig } from 'vite';
import vue from '@vitejs/plugin-vue';

// Vite 配置：dev server 监听 5173，/ws 代理到 C# Kestrel server (localhost:8080)，
// 避免开发时浏览器跨域。生产构建产物 dist/ 由 C# csproj pre-build target 复制到 wwwroot/。
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
    },
  },
});
