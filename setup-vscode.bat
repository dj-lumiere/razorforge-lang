@echo off
REM RazorForge VSCode One-Click Setup Script
REM This script sets up the development environment for VSCode

echo ========================================
echo RazorForge VSCode Setup
echo ========================================
echo.

REM Check if VSCode is installed
where code >nul 2>&1
if %ERRORLEVEL% NEQ 0 (
    echo [ERROR] VSCode is not installed or not in PATH
    echo Please install VSCode from https://code.visualstudio.com/
    echo.
    pause
    exit /b 1
)
echo [OK] VSCode found

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
echo Installing recommended VSCode extensions...
echo.

REM Install C# extensions
echo [1/13] Installing C# extension...
call code --install-extension ms-dotnettools.csharp

echo [2/13] Installing C# Dev Kit...
call code --install-extension ms-dotnettools.csdevkit

REM Install C/C++ extensions
echo [3/13] Installing C/C++ extension...
call code --install-extension ms-vscode.cpptools

echo [4/13] Installing CMake Tools...
call code --install-extension ms-vscode.cmake-tools

echo [5/13] Installing clangd...
call code --install-extension llvm-vs-code-extensions.vscode-clangd

REM Install Git extensions
echo [6/13] Installing GitLens...
call code --install-extension eamodio.gitlens

REM Install Markdown extensions
echo [7/13] Installing Markdown All in One...
call code --install-extension yzhang.markdown-all-in-one

echo [8/13] Installing Markdown Lint...
call code --install-extension davidanson.vscode-markdownlint

REM Install code quality extensions
echo [9/13] Installing EditorConfig...
call code --install-extension editorconfig.editorconfig

REM Install utility extensions
echo [10/13] Installing Todo Tree...
call code --install-extension gruntfuggly.todo-tree

echo [11/13] Installing Better Comments...
call code --install-extension aaron-bond.better-comments

echo [12/13] Installing TODO Highlight...
call code --install-extension wayou.vscode-todo-highlight

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
    echo You can still open the project in VSCode and build from there.
    echo.
) else (
    echo.
    echo [OK] Build successful!
    echo.
)

echo ========================================
echo Setup Complete!
echo ========================================
echo.
echo Next steps:
echo 1. Open VSCode: code .
echo 2. Press Ctrl+Shift+B to build
echo 3. Press F5 to debug
echo 4. See VSCODE_SETUP.md for more information
echo.
echo Opening VSCode now...
echo.

REM Open VSCode in current directory
code .

echo.
echo Setup complete! Check VSCode for further instructions.
pause
