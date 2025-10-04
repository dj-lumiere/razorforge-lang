@echo off
REM RazorForge Bootstrap Script for Windows
REM Compiles RazorForge code to native executable

if "%~1"=="" (
    echo Usage: bootstrap.bat ^<source_file.rf^>
    exit /b 1
)

set SOURCE_FILE=%~1
set BASE_NAME=%~n1

echo === RazorForge Bootstrap Compiler ===
echo Source: %SOURCE_FILE%

REM Step 1: Run C# compiler to generate LLVM IR
echo Step 1: Generating LLVM IR...
dotnet run -- "%SOURCE_FILE%"

if %ERRORLEVEL% neq 0 (
    echo Error: Failed to generate LLVM IR
    exit /b 1
)

REM Step 2: Compile LLVM IR to object file (requires LLVM installed)
echo Step 2: Compiling LLVM IR to object file...
llc -filetype=obj "%BASE_NAME%.ll" -o "%BASE_NAME%.obj"

if %ERRORLEVEL% neq 0 (
    echo Error: Failed to compile LLVM IR
    echo Make sure LLVM is installed and llc is in your PATH
    exit /b 1
)

REM Step 3: Link with runtime library (using cl.exe from Visual Studio)
echo Step 3: Linking with runtime...
cl /Fe"%BASE_NAME%.exe" "%BASE_NAME%.obj" ^
    razorforge_minimal_implementation\runtime.c ^
    razorforge_minimal_implementation\i8.c

if %ERRORLEVEL% neq 0 (
    echo Error: Failed to link executable
    echo Make sure Visual Studio Build Tools are installed
    exit /b 1
)

echo Success! Executable created: %BASE_NAME%.exe
echo Run with: %BASE_NAME%.exe