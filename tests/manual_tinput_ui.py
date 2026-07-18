"""手动测试脚本：ADR-0016 TINPUT timer metadata UI 验证。

与 `test_tinput_timeout.py`（自动化断言）不同，本脚本只启动 C# headless server
并注入一份覆盖所有 ADR-0016 测试场景的 ERB，由开发者用浏览器手动验证 UI 行为。

用法
----
1. 构建 Debug exe（若尚未构建）：

       dotnet build Emuera.Headless/Emuera.Headless.csproj -c Debug

2. 启动前端 Vite dev server（另一终端，监听 5173）：

       cd Emuera.Web && npm run dev

3. 启动本脚本（注入 ERB + 启动 C# server，监听 8080）：

       python tests/manual_tinput_ui.py

4. 浏览器访问 http://localhost:5173，按 ERB 菜单选择测试场景。

   也可直接访问 http://localhost:8080/snapshot 查看原始 JSON，
   或用 curl GET /turn 验证 WS / HTTP 帧中的 4 个 timer 字段。

5. 验证完毕后按 Enter 关闭 server。

测试场景（ERB 内置菜单）
------------------------
- [0] 场景 A：5s 显示倒计时 + 超时
      验证 InputBar <progress> 倒计时 + timeoutNotice 横幅（timeUpMessage）
- [1] 场景 B：5s 显示倒计时 + 手动输入
      验证手动输入后下一帧无 timeoutNotice 横幅
- [2] 场景 C：3s 隐藏倒计时（disp=0）+ 超时
      验证 displayTime=false 时不显示 <progress>，但超时仍触发 timeUpMessage
- [3] 场景 D：30s 长倒计时（晚加入者测试）
      验证 GET /snapshot 携带 timer 字段，重连浏览器后倒计时继续显示
- [9] 退出游戏

每个场景结束后返回菜单，可重复测试。
"""
import shutil
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tests"))

from emuera_server import copy_test_game_with_erb, start_server


# 注入到临时游戏目录的 ERB。覆盖 ADR-0016 全部测试场景。
# TINPUT 语法：TINPUT time, def, disp, timeout, mouse
#   time    超时毫秒（>0）
#   def     超时后 RESULT 的默认值
#   disp    是否显示倒计时（1=显示，0=隐藏）
#   timeout 超时提示文案
#   mouse   是否允许鼠标输入（0=禁用）
ERB = """@SYSTEM_TITLE
$LOOP
PRINTL === ADR-0016 TINPUT 手动测试 ===
PRINTL
PRINTL 请选择测试场景：
PRINTL  [0] 场景 A：5s 显示倒计时 + 超时
PRINTL  [1] 场景 B：5s 显示倒计时 + 手动输入
PRINTL  [2] 场景 C：3s 隐藏倒计时 + 超时（disp=0）
PRINTL  [3] 场景 D：30s 长倒计时（晚加入者测试）
PRINTL  [9] 退出游戏
PRINTL
INPUT
IF RESULT == 0
\tPRINTL === 场景 A：5s 显示倒计时 + 超时 ===
\tPRINTL 期望：InputBar 显示倒计时（5.0s → 0s）；5s 后显示 timeUpMessage 横幅
\tPRINTL --- TINPUT 开始（不输入，等待超时）---
\tTINPUT 5000, 100, 1, "A 超时，默认值 100", 0
\tPRINTFORML RESULT = {RESULT}（期望：100）
\tPRINTL [0] 返回菜单
\tINPUT
ELSEIF RESULT == 1
\tPRINTL === 场景 B：5s 显示倒计时 + 手动输入 ===
\tPRINTL 期望：InputBar 显示倒计时；手动输入数字后无 timeUpMessage 横幅
\tPRINTL --- TINPUT 开始（请输入数字，如 42）---
\tTINPUT 5000, 0, 1, "B 超时（不应出现此横幅）", 0
\tPRINTFORML RESULT = {RESULT}
\tPRINTL [0] 返回菜单
\tINPUT
ELSEIF RESULT == 2
\tPRINTL === 场景 C：3s 隐藏倒计时 + 超时 ===
\tPRINTL 期望：InputBar 不显示倒计时（displayTime=false）；3s 后仍显示 timeUpMessage 横幅
\tPRINTL --- TINPUT 开始（不输入，等待超时）---
\tTINPUT 3000, 200, 0, "C 隐藏倒计时也超时", 0
\tPRINTFORML RESULT = {RESULT}（期望：200）
\tPRINTL [0] 返回菜单
\tINPUT
ELSEIF RESULT == 3
\tPRINTL === 场景 D：30s 长倒计时（晚加入者测试）===
\tPRINTL 期望：InputBar 显示倒计时；可刷新浏览器验证 GET /snapshot 携带 timer 字段
\tPRINTL --- TINPUT 开始（30s 超时或输入数字提前结束）---
\tTINPUT 30000, 0, 1, "D 30s 长倒计时结束", 0
\tPRINTFORML RESULT = {RESULT}
\tPRINTL [0] 返回菜单
\tINPUT
ELSEIF RESULT == 9
\tQUIT
ELSE
\tPRINTL 无效输入，请重试
ENDIF
GOTO LOOP
"""


def main():
    temp_dir = None
    server = None
    try:
        temp_dir, game_dir = copy_test_game_with_erb(ERB)
        # 固定 8080 端口：vite.config.ts 的 /ws /snapshot /session 代理都指向 8080
        server = start_server(game_dir, port=8080)

        status, body = server.create_session()
        if status != 201:
            print(f"ERROR: POST /session 返回 {status}: {body}")
            return 1

        print()
        print("=" * 64)
        print("ADR-0016 TINPUT 手动测试 server 已启动")
        print("=" * 64)
        print()
        print(f"  C# server      : {server.base_url}")
        print(f"  临时游戏目录   : {game_dir}")
        print()
        print("前端访问（推荐）：")
        print("  1. 在另一终端：cd Emuera.Web && npm run dev")
        print("  2. 浏览器访问 http://localhost:5173")
        print()
        print("协议层直接验证（无需前端）：")
        print(f"  - curl {server.base_url}/snapshot  # 应含 timeLimit/displayTime/timeUpMessage")
        print(f"  - curl {server.base_url}/turn       # WaitInput 帧应含 4 个 timer 字段")
        print()
        print("ERB 内置测试场景：")
        print("  [0] 场景 A：5s 显示倒计时 + 超时      → 验证 timeUpMessage 横幅")
        print("  [1] 场景 B：5s 显示倒计时 + 手动输入  → 验证无横幅")
        print("  [2] 场景 C：3s 隐藏倒计时 + 超时      → 验证 displayTime=false")
        print("  [3] 场景 D：30s 长倒计时              → 验证晚加入者 GET /snapshot")
        print("  [9] 退出游戏")
        print()
        print("验证项清单：")
        print("  [ ] InputBar <progress> 倒计时平滑递减（displayTime=true 时）")
        print("  [ ] displayTime=false 时不显示 <progress>")
        print("  [ ] 超时后下一帧显示 timeoutNotice 横幅（timeUpMessage）")
        print("  [ ] 手动输入后无 timeoutNotice 横幅")
        print("  [ ] WS 帧含 timeLimit/displayTime/timeUpMessage/timedOut 四字段")
        print("  [ ] timedOut: false 在非超时帧始终存在（非 nullable 字段）")
        print("  [ ] 协议版本 protocolVersion=6")
        print("  [ ] 晚加入者 GET /snapshot 携带 timer 字段")
        print()
        print("按 Enter 关闭 server（或 Ctrl+C 强制退出）...")
        try:
            input()
        except KeyboardInterrupt:
            pass
        return 0
    finally:
        if server is not None:
            try:
                server.delete_session()
            except Exception:
                pass
            server.close()
        if temp_dir is not None:
            shutil.rmtree(temp_dir, ignore_errors=True)


if __name__ == "__main__":
    sys.exit(main())
