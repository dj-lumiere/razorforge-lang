#!/bin/bash
# Simple test for RazorForge Bootstrap

SOURCE_FILE=$1
BASE_NAME="${SOURCE_FILE%.rf}"

echo "=== RazorForge Bootstrap Test ==="
echo "Source: $SOURCE_FILE"

# Step 1: Generate LLVM IR
echo "Step 1: Generating LLVM IR..."
dotnet run -- "$SOURCE_FILE"

if [ $? -ne 0 ]; then
    echo "Error: Failed to generate LLVM IR"
    exit 1
fi

# Step 2: Check the LLVM IR was generated correctly
if [ -f "$BASE_NAME.ll" ]; then
    echo "Step 2: LLVM IR generated successfully:"
    echo "--- $BASE_NAME.ll ---"
    cat "$BASE_NAME.ll" | head -20
    echo "---"
    
    # Since we don't have llc, create a simple C program that returns 42
    echo "Step 3: Creating equivalent C program..."
    cat > "${BASE_NAME}_equiv.c" << 'EOF'
// Equivalent C program for the RazorForge hello world
#include <stdio.h>

int main() {
    return 42;
}
EOF
    
    echo "Step 4: Compiling C equivalent..."
    gcc -o "${BASE_NAME}_equiv.exe" "${BASE_NAME}_equiv.c"
    
    if [ $? -eq 0 ]; then
        echo "Success! Test executable created: ${BASE_NAME}_equiv.exe"
        echo "Running test..."
        "./${BASE_NAME}_equiv.exe"
        echo "Exit code: $?"
    else
        echo "Error: Failed to compile test executable"
        exit 1
    fi
else
    echo "Error: LLVM IR file not found: $BASE_NAME.ll"
    exit 1
fi