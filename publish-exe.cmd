@echo off
rem Call publish-exe.ps1 with -ExecutionPolicy Bypass.
rem This machine blocks unsigned local scripts even though Get-ExecutionPolicy reports
rem RemoteSigned (even -ExecutionPolicy Unrestricted is rejected), so Bypass is the only
rem working switch. Arguments are forwarded as-is.
rem Keep this file ASCII-only: cmd.exe decodes .cmd using the OEM code page (936), so
rem UTF-8 Chinese comments would be garbled and may be executed as commands.
setlocal
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0publish-exe.ps1" %*
exit /b %ERRORLEVEL%
