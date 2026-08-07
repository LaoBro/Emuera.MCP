# Android Native AOT 真机验证记录

> 日期：2026-08-08
> 项目：`experiments/AndroidNativeAot`
> ABI：`android-arm64`

## 结论

实验项目发布的 Android Native AOT APK 已在实体 arm64 设备上成功运行。MAUI Activity、WebView 首帧和 Android SAF 文件选择器的最小 interop 链路均通过。

这证明当前 `Microsoft.Android` workload 的 MAUI Native AOT 路径可以在真实设备上运行；不等同于生产 `Emuera.Maui` 已完成 Native AOT 切换验收。

## 验证结果

| 项目 | 结果 |
|---|---|
| APK 安装与冷启动 | 通过，应用正常进入实验页 |
| WebView 首帧 | 通过，页面显示 `WebView OK` |
| SAF 打开文件选择器 | 通过 |
| SAF 取消 | 通过，返回应用后状态正常 |
| SAF 选择文件 | 通过，页面显示选中文件名 |

## 尚未覆盖

- 生产游戏目录选择、递归读取和存档写入；
- 完整回合输入、TINPUT、手势和返回键流程；
- 生产壳 IL 警告治理；
- 冷启动/二次启动、峰值内存和连续回合性能 A/B；
- 多 Android API 级别和其他 ABI 的设备矩阵。

因此当前门控结论为：**MAUI Native AOT 最小真机冒烟通过，生产切换门控未完成**。Avalonia 仍不承担“解锁 Native AOT”的职责，是否迁移应继续由内存、启动和渲染性能数据决定。

## 关联

- 实验项目说明：`experiments/AndroidNativeAot/README.md`
- Native AOT 验证总报告：`nativeaot-verify-report.md`
- 性能优化主计划：`android-perf-2.md` §3.4
