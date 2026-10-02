@echo off
REM Removes the fix, restoring original files. Close Lyricify first.
taskkill /IM Lyricify.exe /F >nul 2>&1
timeout /t 2 /nobreak >nul
copy /y "%~dp0Newtonsoft.Json.dll.orig" "D:\software\Lyricfy\Newtonsoft.Json.dll"
copy /y "%~dp0Lyricify.exe.config.bak" "D:\software\Lyricfy\Lyricify.exe.config"
del /f /q "D:\software\Lyricfy\HookTemplate.dll" 2>nul
del /f /q "D:\software\Lyricfy\LyricifyFix.dll" 2>nul
echo Fix removed, original files restored.
pause
