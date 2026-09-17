@echo off
setlocal
REM ReshadeController one-time permission fix.
REM Copy this file INTO the game folder (next to ffxiv_dx11.exe / ReShade.ini),
REM then double-click it and accept the UAC prompt. Run once, then delete it.

REM Self-elevate to admin (needed to change ACLs under Program Files).
net session >nul 2>&1
if %errorlevel% neq 0 (
    echo Requesting admin rights...
    powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)

set "GAMEDIR=%~dp0"
echo Game folder: "%GAMEDIR%"
echo.

if not exist "%GAMEDIR%ReShade.ini" if not exist "%GAMEDIR%reshade-presets" (
    echo WARNING: no ReShade.ini or reshade-presets found here.
    echo Make sure this file is inside the game folder, next to ffxiv_dx11.exe.
    echo.
)

REM Users group by SID (*S-1-5-32-545) so it works on non-English Windows too.
if exist "%GAMEDIR%reshade-presets" (
    echo Fixing reshade-presets ^(recursive^)...
    icacls "%GAMEDIR%reshade-presets" /grant *S-1-5-32-545:"(OI)(CI)M" /T /C
    echo.
)
if exist "%GAMEDIR%ReShade.ini" (
    echo Fixing ReShade.ini...
    icacls "%GAMEDIR%ReShade.ini" /grant *S-1-5-32-545:M /C
    echo.
)
if exist "%GAMEDIR%ReShade.log" (
    echo Fixing ReShade.log...
    icacls "%GAMEDIR%ReShade.log" /grant *S-1-5-32-545:M /C
    echo.
)

echo Done. You can now run the game WITHOUT admin and delete this file.
echo If a preset still fails to save, re-run this after creating new subfolders.
echo.
pause
