@echo off
rem ============================================================
rem  EraCore Web 模式一键启动（无需 MAUI，浏览器操作）
rem  双击本脚本 → 构建 → 启动 server → 自动打开默认浏览器
rem  关闭本窗口即停止 server。
rem  首次启动会自动构建（约几十秒），之后直接运行。
rem ============================================================
setlocal
cd /d "%~dp0"

if not exist EraCore.Cli\bin\Debug\net10.0\EraCore.Cli.dll (
  echo [run-web] 首次运行，正在构建（请稍候）...
  dotnet build EraCore.Cli/EraCore.Cli.csproj -c Debug -m:1 >nul
  if errorlevel 1 (
    echo [run-web] 构建失败，请检查 .NET 10 SDK 是否已安装。
    pause
    exit /b 1
  )
)

echo [run-web] 启动 EraCore server（--server --open-browser）...
dotnet exec EraCore.Cli\bin\Debug\net10.0\EraCore.Cli.dll --server --open-browser

echo.
echo [run-web] server 已停止。
pause
