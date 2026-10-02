@echo off
rem Fork helper entry point: rr help
setlocal
set "RR_PS=powershell"
where pwsh >nul 2>nul && set "RR_PS=pwsh"
%RR_PS% -NoProfile -ExecutionPolicy Bypass -File "%~dp0setup\rr.ps1" %*
exit /b %ERRORLEVEL%
