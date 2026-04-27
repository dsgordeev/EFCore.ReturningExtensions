@echo off
setlocal enabledelayedexpansion

REM Output directory (default: .\nupkgs)
set OUTPUT_DIR=%~1
if "%OUTPUT_DIR%"=="" set OUTPUT_DIR=..\nupkgs

REM Create output directory if it doesn't exist
if not exist "%OUTPUT_DIR%" mkdir "%OUTPUT_DIR%"

set PACKED=0
set SKIPPED=0
set FAILED=0

REM Recursively find all .csproj files (each in its own folder)
for /r %%f in (*.csproj) do (
    set "PROJ=%%f"
    set "NAME=%%~nxf"

    REM Skip test projects (*.Tests.csproj)
    echo !NAME! | findstr /I /E /C:".Tests.csproj" >nul
    if !errorlevel! equ 0 (
        echo [SKIP] Test project: !NAME!
        set /a SKIPPED+=1
    ) else (
        echo [PACK] !NAME!  ^(%%~dpf^)
        dotnet pack "%%f" -c Release -o "%OUTPUT_DIR%"
        rem --include-symbols --include-source
        if !errorlevel! neq 0 (
            echo [ERROR] Failed to pack: %%f
            set /a FAILED+=1
        ) else (
            echo [OK] Packed: !NAME!
            set /a PACKED+=1
        )
    )
    echo.
)

echo ========================================
echo Packed:  %PACKED%
echo Skipped: %SKIPPED%
echo Failed:  %FAILED%
echo Output:  %OUTPUT_DIR%
echo ========================================

echo on