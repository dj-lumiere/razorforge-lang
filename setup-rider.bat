@echo off
REM RazorForge Rider One-Click Setup Script
REM This script sets up the development environment for JetBrains Rider

echo ========================================
echo RazorForge Rider Setup
echo ========================================
echo.

REM Check if .NET 9.0 SDK is installed
dotnet --version >nul 2>&1
if %ERRORLEVEL% NEQ 0 (
    echo [ERROR] .NET SDK is not installed
    echo Please install .NET 9.0 SDK from https://dotnet.microsoft.com/download/dotnet/9.0
    echo.
    pause
    exit /b 1
)
echo [OK] .NET SDK found

REM Display .NET version
for /f "delims=" %%i in ('dotnet --version') do set DOTNET_VERSION=%%i
echo [INFO] .NET version: %DOTNET_VERSION%

echo.
echo ========================================
echo Building RazorForge...
echo ========================================
echo.

REM Build the project
echo [INFO] Running dotnet build...
dotnet build RazorForge.csproj

if %ERRORLEVEL% NEQ 0 (
    echo.
    echo [WARNING] Build failed! Please check the errors above.
    echo You can still open the project in Rider and build from there.
    echo.
) else (
    echo.
    echo [OK] Build successful!
    echo.
)

echo.
echo ========================================
echo Rider Configuration Summary
echo ========================================
echo.
echo The following have been pre-configured:
echo.
echo  ✓ Run Configurations (.idea/.idea.RazorForge/.idea/runConfigurations/)
echo    - Build RazorForge
echo    - Compile: Current File
echo    - Compile: Intrinsics Demo
echo    - Compile: Primitive Types Demo
echo    - Compile: Console Demo
echo    - Compile: Suflae Current File
echo    - Compile: Suflae Console Demo
echo    - Language Server
echo    - Run: All Tests
echo.
echo  ✓ File Templates (.idea/fileTemplates/)
echo    - RazorForge File.rf
echo    - RazorForge Record Type.rf
echo    - RazorForge Entity Type.rf
echo    - Suflae File.sf
echo    - Suflae Record Type.sf
echo    - Suflae Entity Type.sf
echo.
echo  ✓ Inspection Profile (.idea/.idea.RazorForge/.idea/inspectionProfiles/)
echo    - RazorForge.xml
echo.
echo ========================================
echo Setup Complete!
echo ========================================
echo.
echo Next steps:
echo 1. Open Rider (it will auto-detect the solution)
echo 2. Select a run configuration from the dropdown
echo 3. Press Shift+F10 to run or Shift+F9 to debug
echo 4. See RIDER_SETUP.md for complete documentation
echo.
echo Opening project in Rider...
echo.

REM Try to find and launch Rider
set "RIDER_PATH="

REM Check common Rider installation paths
if exist "C:\Program Files\JetBrains\JetBrains Rider 2024.3\bin\rider64.exe" (
    set "RIDER_PATH=C:\Program Files\JetBrains\JetBrains Rider 2024.3\bin\rider64.exe"
) else if exist "C:\Program Files\JetBrains\JetBrains Rider 2024.2\bin\rider64.exe" (
    set "RIDER_PATH=C:\Program Files\JetBrains\JetBrains Rider 2024.2\bin\rider64.exe"
) else if exist "C:\Program Files\JetBrains\JetBrains Rider 2024.1\bin\rider64.exe" (
    set "RIDER_PATH=C:\Program Files\JetBrains\JetBrains Rider 2024.1\bin\rider64.exe"
) else if exist "%LOCALAPPDATA%\JetBrains\Toolbox\apps\Rider\ch-0\*\bin\rider64.exe" (
    REM Toolbox installation - find the latest version
    for /f "delims=" %%i in ('dir /b /o-d "%LOCALAPPDATA%\JetBrains\Toolbox\apps\Rider\ch-0\*"') do (
        set "RIDER_PATH=%LOCALAPPDATA%\JetBrains\Toolbox\apps\Rider\ch-0\%%i\bin\rider64.exe"
        goto :found
    )
)

:found
if defined RIDER_PATH (
    echo [INFO] Launching Rider from: %RIDER_PATH%
    start "" "%RIDER_PATH%" "%CD%"
) else (
    echo [WARNING] Could not find Rider installation.
    echo Please open the project manually in Rider.
    echo.
    echo If Rider is installed, you can launch it with:
    echo   rider64.exe .
    echo.
)

echo.
echo Setup complete! See RIDER_SETUP.md for more information.
pause
