@echo off
REM Re-applies the YesPlayMusic int64-id fix to Lyricify 3.8.8
REM Close Lyricify first, then double-click this file.
taskkill /IM Lyricify.exe /F >nul 2>&1
timeout /t 2 /nobreak >nul
copy /y "%~dp0known-good\Newtonsoft.Json.dll" "D:\software\Lyricfy\Newtonsoft.Json.dll"
copy /y "%~dp0known-good\HookTemplate.dll" "D:\software\Lyricfy\HookTemplate.dll"
copy /y "%~dp0known-good\Lyricify.exe.config" "D:\software\Lyricfy\Lyricify.exe.config"
del /f /q "D:\software\Lyricfy\LyricifyFix.dll" 2>nul
echo Fix installed. Start Lyricify and play a song.
pause
