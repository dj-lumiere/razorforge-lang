@echo off
setlocal

echo Building RazorForge Native Libraries...

if not exist build mkdir build
cd build

echo Configuring with CMake...
cmake .. -G "Ninja" -DCMAKE_C_COMPILER="C:/Program Files/LLVM/bin/clang.exe" -DCMAKE_CXX_COMPILER="C:/Program Files/LLVM/bin/clang++.exe" -DCMAKE_BUILD_TYPE=Release

if %ERRORLEVEL% neq 0 (
    echo CMake configuration failed!
    exit /b 1
)

echo Building libraries...
cmake --build . --config Release

if %ERRORLEVEL% neq 0 (
    echo Build failed!
    exit /b 1
)

echo Copying libraries to project directories...
if not exist ..\..\bin\Debug\net9.0 mkdir ..\..\bin\Debug\net9.0
if not exist ..\..\bin\Release\net9.0 mkdir ..\..\bin\Release\net9.0

copy lib\Release\*.dll ..\..\bin\Debug\net9.0\ 2>nul
copy lib\Release\*.dll ..\..\bin\Release\net9.0\ 2>nul
copy bin\Release\*.dll ..\..\bin\Debug\net9.0\ 2>nul
copy bin\Release\*.dll ..\..\bin\Release\net9.0\ 2>nul

echo Native libraries built successfully!