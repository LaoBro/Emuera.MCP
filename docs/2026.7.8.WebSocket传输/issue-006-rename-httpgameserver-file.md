# Issue 006 — 文件名对齐：HttpGameServer.cs → KestrelGameServer.cs（follow-up）

> **Parent**: [PRD-WebSocket传输.md](./PRD-WebSocket传输.md) · Out of Scope（命名清理小债）
> **Part of**: WebSocket 传输实现（纵向切片 6/6，独立 follow-up）

## What to build

纯命名清理，**不属 WebSocket 功能范围**，但 PRD 将其记为独立 follow-up 以免遗忘。
当前 `Emuera.Headless/Server/HttpGameServer.cs` 文件内的类已被重写为
`KestrelGameServer`（用 `WebApplication.CreateBuilder()` + `UseKestrel()`），但**文件名
未改**——文件名与类/事实不符。将其改名以对齐。

- 将 `HttpGameServer.cs` 重命名为 `KestrelGameServer.cs`（类名为 `KestrelGameServer`）。
- 同步更新 `.csproj` / `ServerRunner.cs` 中任何按文件名引用的地方（如有）。
- 确保 `dotnet build` 通过、无残留编译引用。

## User stories covered

- （维护性）消除命名不一致小债，避免后续维护者误判传输层仍是 HttpListener。

## Acceptance criteria

- [ ] 文件重命名为 `KestrelGameServer.cs`，类 `KestrelGameServer` 位于其中。
- [ ] 全仓库无遗留指向旧文件名 `HttpGameServer` 的引用（类/文件名统一）。
- [ ] `dotnet build Emuera.Headless/Emuera.Headless.csproj -c Debug` 成功。
- [ ] `tests/run_all.py` 通过（确认仅为重命名、无行为变化）。

## Blocked by

None — 与 WS 功能切片正交，可随时单独进行（PRD 明确列为 Out of Scope）。
