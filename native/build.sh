#!/bin/bash
set -e

echo "Building RazorForge Native Libraries..."

mkdir -p build
cd build

echo "Configuring with CMake..."
cmake .. -DCMAKE_BUILD_TYPE=Release

echo "Building libraries..."
make -j$(nproc)

echo "Copying libraries to project directories..."
mkdir -p ../../bin/Debug/net9.0
mkdir -p ../../bin/Release/net9.0

cp lib/*.so ../../bin/Debug/net9.0/ 2>/dev/null || true
cp lib/*.so ../../bin/Release/net9.0/ 2>/dev/null || true
cp lib/*.dylib ../../bin/Debug/net9.0/ 2>/dev/null || true
cp lib/*.dylib ../../bin/Release/net9.0/ 2>/dev/null || true

echo "Native libraries built successfully!"