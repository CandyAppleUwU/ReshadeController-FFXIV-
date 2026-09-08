@echo off
setlocal

set INCLUDE_DIR=include
set SOURCES=dllmain.cpp
set OUTPUT=reshade_controller.addon

echo Building Reshade Controller Addon (MinGW-w64)...
echo.

if not exist "%INCLUDE_DIR%\reshade.hpp" (
    echo ERROR: ReShade headers not found in %INCLUDE_DIR%\
    exit /b 1
)

g++ -shared -o %OUTPUT% %SOURCES% ^
    -I"%INCLUDE_DIR%" ^
    -lole32 -luuid -lversion ^
    -static -static-libgcc -static-libstdc++ ^
    -std=c++17 -fms-extensions -Wno-attributes

if %errorlevel% equ 0 (
    echo.
    echo Build successful: %OUTPUT%
    echo.
    echo Copy %OUTPUT% to your game directory.
    echo Make sure ReShade is installed in the same game directory.
) else (
    echo.
    echo Build failed.
)

endlocal
