@echo off
chcp 65001 >nul
cd /d "%~dp0"
title CK3 启动器

set "PYW="
if exist "C:\Users\28718\.workbuddy\binaries\python\envs\default\Scripts\pythonw.exe" (
  set "PYW=C:\Users\28718\.workbuddy\binaries\python\envs\default\Scripts\pythonw.exe"
)
if not defined PYW (
  for /f "delims=" %%i in ('where pythonw 2^>nul') do (
    set "PYW=%%i"
    goto :found
  )
)
:found
if not defined PYW (
  echo.
  echo   未找到 Python。请安装 Python 3.10 或更高版本，
  echo   或在「启动器(调试).bat」里手动指定 python 路径。
  echo.
  pause
  exit /b 1
)

start "" "%PYW%" "%~dp0launcher.py"
exit
