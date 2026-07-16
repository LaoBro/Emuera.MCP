import { defineConfig } from 'vite';
import vue from '@vitejs/plugin-vue';

// Vite 配置：dev server 监听 5173，/ws 代理到 C# Kestrel server (localhost:8080)，
// 避免开发时浏览器跨域。生产构建产物 dist/ 由 C# csproj pre-build target 复制到 wwwroot/。
export default defineConfig({
  plugins: [vue()],
  server: {
    port: 5173,
    proxy: {
      '/ws': {
        target: 'ws://localhost:8080',
        ws: true,
        changeOrigin: true,
      },
      // 调试用：HTTP 端点（/snapshot、/session 等）也走代理，避免 CORS
      '/session': 'http://localhost:8080',
      '/turn': 'http://localhost:8080',
      '/input': 'http://localhost:8080',
      '/state': 'http://localhost:8080',
      '/snapshot': 'http://localhost:8080',
    },
  },
});
