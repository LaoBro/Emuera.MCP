<script setup lang="ts">
import { ref } from 'vue';
import { checkStoragePermission, requestStoragePermission } from '../lib/mauiBridge';

/**
 * game-library spec ID6：Android 存储权限引导页。
 *
 * 全屏叠加层，在 MAUI Android 平台权限未授权时展示。
 * - 说明需要存储权限的原因
 * - 「去授权」按钮 → 调 requestStoragePermission() → 跳系统设置
 * - 「已授权，重新扫描」按钮 → 调 checkStoragePermission() → C# 重检权限
 *
 * 拒绝后停留在引导页，不进入列表页（spec ID6）。
 * 授权后 permissionStatus→'granted'，此组件自动隐藏（父组件 v-if）。
 */

const operating = ref(false);

/** 点击「去授权」——跳系统设置。 */
function onGoSettings(): void {
  if (operating.value) return;
  operating.value = true;
  requestStoragePermission();
  // 用户跳转系统设置后，返回时通过「已授权，重新扫描」按钮重检
  // 不重置 operating——允许用户回来后再次点击
  setTimeout(() => { operating.value = false; }, 2000);
}

/** 点击「已授权，重新扫描」——重新检查权限。 */
function onRecheck(): void {
  if (operating.value) return;
  operating.value = true;
  checkStoragePermission();
  // checkPermission 回复 permissionStatus → useAppInit 更新 store
  // 不必等——permissionStatus 变化时父组件自动响应
  setTimeout(() => { operating.value = false; }, 3000);
}
</script>

<template>
  <div class="permission-guide-overlay">
    <div class="permission-guide-card">
      <div class="pg-icon">📂</div>
      <h2 class="pg-title">需要存储权限</h2>
      <p class="pg-description">
        Emuera 需要访问您设备上的文件，才能扫描并加载游戏目录。
      </p>
      <p class="pg-description">
        请点击下方按钮，在系统设置中允许「管理所有文件」权限。
      </p>
      <div class="pg-steps">
        <div class="pg-step">1. 点击「去授权」跳转到系统设置</div>
        <div class="pg-step">2. 打开「允许管理所有文件」开关</div>
        <div class="pg-step">3. 返回本应用，点击「已授权，重新扫描」</div>
      </div>
      <div class="pg-actions">
        <button
          class="pg-btn primary"
          :disabled="operating"
          @click="onGoSettings"
        >
          {{ operating ? '处理中…' : '去授权' }}
        </button>
        <button
          class="pg-btn secondary"
          :disabled="operating"
          @click="onRecheck"
        >
          已授权，重新扫描
        </button>
      </div>
    </div>
  </div>
</template>

<style scoped>
.permission-guide-overlay {
  position: fixed;
  inset: 0;
  background: #1e1e1e;
  display: flex;
  align-items: center;
  justify-content: center;
  z-index: 2000;
  padding: 24px;
}
.permission-guide-card {
  background: #252526;
  border: 1px solid #3c3c3c;
  border-radius: 8px;
  padding: 32px 24px;
  max-width: 400px;
  width: 100%;
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 12px;
}
.pg-icon {
  font-size: 48px;
  margin-bottom: 4px;
}
.pg-title {
  margin: 0;
  font-size: 18px;
  font-weight: 600;
  color: #e0e0e0;
}
.pg-description {
  margin: 0;
  font-size: 13px;
  color: #9aa0a6;
  text-align: center;
  line-height: 1.5;
}
.pg-steps {
  width: 100%;
  display: flex;
  flex-direction: column;
  gap: 6px;
  padding: 12px;
  background: #2d2d30;
  border-radius: 4px;
}
.pg-step {
  font-size: 12px;
  color: #dcdcdc;
  padding-left: 20px;
  position: relative;
  line-height: 1.4;
}
.pg-step::before {
  content: '→';
  position: absolute;
  left: 4px;
  color: #4ec9b0;
}
.pg-actions {
  width: 100%;
  display: flex;
  flex-direction: column;
  gap: 8px;
  margin-top: 8px;
}
.pg-btn {
  width: 100%;
  padding: 10px 16px;
  border-radius: 4px;
  border: none;
  cursor: pointer;
  font-size: 14px;
  font-family: inherit;
  text-align: center;
}
.pg-btn:disabled {
  opacity: 0.6;
  cursor: not-allowed;
}
.pg-btn.primary {
  background: #0e639c;
  color: #fff;
}
.pg-btn.primary:hover:not(:disabled) {
  background: #1177bb;
}
.pg-btn.secondary {
  background: #333;
  color: #ccc;
  border: 1px solid #444;
}
.pg-btn.secondary:hover:not(:disabled) {
  background: #444;
}
</style>
