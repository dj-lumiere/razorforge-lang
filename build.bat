@echo off
REM RazorForge CMake Build Script for Windows

setlocal enabledelayedexpansion

REM Default build configuration
set BUILD_TYPE=Release
set BUILD_DIR=build
set CLEAN_BUILD=false
set VERBOSE=false
set INSTALL=false
set PACKAGE=false
set RUN_TESTS=false

REM Parse command line arguments
:parse_args
if "%~1"=="" goto :start_build
if /i "%~1"=="--debug" set BUILD_TYPE=Debug
if /i "%~1"=="--release" set BUILD_TYPE=Release
if /i "%~1"=="--clean" set CLEAN_BUILD=true
if /i "%~1"=="--verbose" set VERBOSE=true
if /i "%~1"=="--install" set INSTALL=true
if /i "%~1"=="--package" set PACKAGE=true
if /i "%~1"=="--test" set RUN_TESTS=true
if /i "%~1"=="--help" goto :show_help
if /i "%~1"=="-h" goto :show_help
shift
goto :parse_args

:show_help
echo RazorForge CMake Build Script
echo.
echo Usage: build.bat [options]
echo.
echo Options:
echo   --debug      Build in Debug mode (default: Release)
echo   --release    Build in Release mode
echo   --clean      Clean build directory before building
echo   --verbose    Enable verbose build output
echo   --install    Install after building
echo   --package    Create package after building
echo   --test       Run tests after building
echo   -h, --help   Show this help message
echo.
echo Examples:
echo   build.bat --debug --test
echo   build.bat --release --install --package
echo   build.bat --clean --verbose
goto :end

:start_build
echo ======================================
echo RazorForge CMake Build Script
echo ======================================
echo Build Type: %BUILD_TYPE%
echo Build Directory: %BUILD_DIR%
echo Clean Build: %CLEAN_BUILD%
echo Verbose: %VERBOSE%
echo.

REM Check for required tools
where cmake >nul 2>&1
if %ERRORLEVEL% neq 0 (
    echo ERROR: CMake not found in PATH
    echo Please install CMake and add it to your PATH
    goto :error
)

where dotnet >nul 2>&1
if %ERRORLEVEL% neq 0 (
    echo ERROR: .NET SDK not found in PATH
    echo Please install .NET 9.0+ SDK and add it to your PATH
    goto :error
)

REM Clean build directory if requested
if "%CLEAN_BUILD%"=="true" (
    echo Cleaning build directory...
    if exist "%BUILD_DIR%" (
        rmdir /s /q "%BUILD_DIR%"
    )
)

REM Create build directory
if not exist "%BUILD_DIR%" (
    mkdir "%BUILD_DIR%"
)

REM Configure with CMake
echo Configuring with CMake...
cd "%BUILD_DIR%"

set CMAKE_ARGS=-DCMAKE_BUILD_TYPE=%BUILD_TYPE%

if "%VERBOSE%"=="true" (
    set CMAKE_ARGS=%CMAKE_ARGS% -DCMAKE_VERBOSE_MAKEFILE=ON
)

cmake %CMAKE_ARGS% ..
if %ERRORLEVEL% neq 0 (
    echo ERROR: CMake configuration failed
    goto :error
)

REM Build
echo Building...
if "%VERBOSE%"=="true" (
    cmake --build . --config %BUILD_TYPE% --verbose
) else (
    cmake --build . --config %BUILD_TYPE%
)
if %ERRORLEVEL% neq 0 (
    echo ERROR: Build failed
    goto :error
)

REM Run tests if requested
if "%RUN_TESTS%"=="true" (
    echo Running tests...
    ctest --build-config %BUILD_TYPE% --output-on-failure
    if %ERRORLEVEL% neq 0 (
        echo WARNING: Some tests failed
    )
)

REM Install if requested
if "%INSTALL%"=="true" (
    echo Installing...
    cmake --install . --config %BUILD_TYPE%
    if %ERRORLEVEL% neq 0 (
        echo ERROR: Installation failed
        goto :error
    )
)

REM Create package if requested
if "%PACKAGE%"=="true" (
    echo Creating package...
    cpack --config %BUILD_TYPE%
    if %ERRORLEVEL% neq 0 (
        echo ERROR: Package creation failed
        goto :error
    )
)

echo.
echo ======================================
echo Build completed successfully!
echo ======================================
echo Build artifacts are in: %BUILD_DIR%\bin
echo.
goto :end

:error
echo.
echo ======================================
echo Build failed!
echo ======================================
cd ..
exit /b 1

:end
cd ..
endlocal