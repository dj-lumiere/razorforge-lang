#!/bin/bash

# RazorForge Bootstrap Script
# Compiles RazorForge code to native executable

if [ "$#" -ne 1 ]; then
    echo "Usage: $0 <source_file.rf>"
    exit 1
fi

SOURCE_FILE=$1
BASE_NAME="${SOURCE_FILE%.rf}"

echo "=== RazorForge Bootstrap Compiler ==="
echo "Source: $SOURCE_FILE"

# Step 1: Run C# compiler to generate LLVM IR
echo "Step 1: Generating LLVM IR..."
dotnet run -- "$SOURCE_FILE"

if [ $? -ne 0 ]; then
    echo "Error: Failed to generate LLVM IR"
    exit 1
fi

# Step 2: Compile LLVM IR to object file
echo "Step 2: Compiling LLVM IR to object file..."
llc -filetype=obj "$BASE_NAME.ll" -o "$BASE_NAME.o"

if [ $? -ne 0 ]; then
    echo "Error: Failed to compile LLVM IR"
    exit 1
fi

# Step 3: Link with runtime library
echo "Step 3: Linking with runtime..."
gcc "$BASE_NAME.o" \
    razorforge_minimal_implementation/runtime.c \
    razorforge_minimal_implementation/i8.c \
    -o "$BASE_NAME" \
    -lm

if [ $? -ne 0 ]; then
    echo "Error: Failed to link executable"
    exit 1
fi

echo "Success! Executable created: $BASE_NAME"
echo "Run with: ./$BASE_NAME"