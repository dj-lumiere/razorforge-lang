#!/bin/bash
# RazorForge VSCode One-Click Setup Script (Linux/macOS)
# This script sets up the development environment for VSCode

echo "========================================"
echo "RazorForge VSCode Setup"
echo "========================================"
echo ""

# Check if VSCode is installed
if ! command -v code &> /dev/null; then
    echo "[ERROR] VSCode is not installed or not in PATH"
    echo "Please install VSCode from https://code.visualstudio.com/"
    echo ""
    exit 1
fi
echo "[OK] VSCode found"

# Check if .NET SDK is installed
if ! command -v dotnet &> /dev/null; then
    echo "[ERROR] .NET SDK is not installed"
    echo "Please install .NET 9.0 SDK from https://dotnet.microsoft.com/download/dotnet/9.0"
    echo ""
    exit 1
fi
echo "[OK] .NET SDK found"

# Display .NET version
DOTNET_VERSION=$(dotnet --version)
echo "[INFO] .NET version: $DOTNET_VERSION"

echo ""
echo "Installing recommended VSCode extensions..."
echo ""

# Install C# extensions
echo "[1/13] Installing C# extension..."
code --install-extension ms-dotnettools.csharp

echo "[2/13] Installing C# Dev Kit..."
code --install-extension ms-dotnettools.csdevkit

# Install C/C++ extensions
echo "[3/13] Installing C/C++ extension..."
code --install-extension ms-vscode.cpptools

echo "[4/13] Installing CMake Tools..."
code --install-extension ms-vscode.cmake-tools

echo "[5/13] Installing clangd..."
code --install-extension llvm-vs-code-extensions.vscode-clangd

# Install Git extensions
echo "[6/13] Installing GitLens..."
code --install-extension eamodio.gitlens

# Install Markdown extensions
echo "[7/13] Installing Markdown All in One..."
code --install-extension yzhang.markdown-all-in-one

echo "[8/13] Installing Markdown Lint..."
code --install-extension davidanson.vscode-markdownlint

# Install code quality extensions
echo "[9/13] Installing EditorConfig..."
code --install-extension editorconfig.editorconfig

# Install utility extensions
echo "[10/13] Installing Todo Tree..."
code --install-extension gruntfuggly.todo-tree

echo "[11/13] Installing Better Comments..."
code --install-extension aaron-bond.better-comments

echo "[12/13] Installing TODO Highlight..."
code --install-extension wayou.vscode-todo-highlight

echo ""
echo "========================================"
echo "Building RazorForge..."
echo "========================================"
echo ""

# Build the project
echo "[INFO] Running dotnet build..."
dotnet build RazorForge.csproj

if [ $? -ne 0 ]; then
    echo ""
    echo "[WARNING] Build failed! Please check the errors above."
    echo "You can still open the project in VSCode and build from there."
    echo ""
else
    echo ""
    echo "[OK] Build successful!"
    echo ""
fi

echo "========================================"
echo "Setup Complete!"
echo "========================================"
echo ""
echo "Next steps:"
echo "1. Open VSCode: code ."
echo "2. Press Ctrl+Shift+B to build"
echo "3. Press F5 to debug"
echo "4. See VSCODE_SETUP.md for more information"
echo ""
echo "Opening VSCode now..."
echo ""

# Open VSCode in current directory
code .

echo ""
echo "Setup complete! Check VSCode for further instructions."
