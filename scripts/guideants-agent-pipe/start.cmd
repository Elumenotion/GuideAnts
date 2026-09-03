@echo off
REM Start Max named-pipe control listener detached (run on Max via R:).
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Start-MaxAgentPipe.ps1" -Detach
exit /b %ERRORLEVEL%
