@echo off
chcp 65001 >nul
cd /d "%~dp0"
title CK3 启动器 - 调试模式

echo ============================================
echo   CK3 启动器 调试模式
echo   出错信息会显示在这个窗口里，关窗即退出
echo ============================================
echo.

set "PY="
if exist "C:\Users\28718\.workbuddy\binaries\python\envs\default\Scripts\python.exe" (
  set "PY=C:\Users\28718\.workbuddy\binaries\python\envs\default\Scripts\python.exe"
)
if not defined PY (
  for /f "delims=" %%i in ('where python 2^>nul') do (
    set "PY=%%i"
    goto :found
  )
)
:found
if not defined PY (
  echo 未找到 Python。请安装 Python 3.10 或更高版本。
  pause
  exit /b 1
)

"%PY%" "%~dp0launcher.py"
echo.
echo 启动器已退出。
pause
