@echo off
REM ===== 白屏取证脚本：安装 APK -> 启动 -> 抓取 logcat =====
REM 用法: collect_whitescreen_logs.bat <apk路径>
set APK=%1
if "%APK%"=="" (
  echo 用法: collect_whitescreen_logs.bat ^<apk路径^>
  exit /b 1
)

echo [1/5] 清理旧 logcat...
adb logcat -c 2>nul

echo [2/5] 安装 APK: %APK%
adb install -r "%APK%"

echo [3/5] 启动应用...
adb shell am force-stop com.emuera.maui 2>nul
adb shell monkey -p com.emuera.maui -c android.intent.category.LAUNCHER 1

echo [4/5] 等待 8 秒让应用加载...
timeout /t 8 /nobreak >nul

echo [5/5] 抓取日志（过滤关键标签）...
adb logcat -d -v time ^
  | findstr /i "EmueraMaui EmueraWV chromium AndroidRuntime MonoDroid Dotnet monodroid FATAL" ^
  > logcat_whitescreen.txt

echo ===== 完成，日志已保存到 logcat_whitescreen.txt =====
echo ----- 关键内容预览 -----
findstr /i "FATAL error exception chromium" logcat_whitescreen.txt | findstr /v "Dynamically" | head -50
