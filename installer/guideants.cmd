@echo off
setlocal EnableExtensions
cd /d "%~dp0"

rem Clear Mark-of-the-Web from extracted release scripts, then run the launcher.
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command ^
  "Get-ChildItem -LiteralPath '%CD%' -Recurse -Filter '*.ps1' | Unblock-File"
if errorlevel 1 (
  echo [guideants][error] Failed to unblock PowerShell scripts.
  exit /b 1
)

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\guideants-launcher.ps1" %*
exit /b %ERRORLEVEL%
