#!/bin/bash
# RazorForge Rider One-Click Setup Script (Linux/macOS)
# This script sets up the development environment for JetBrains Rider

echo "========================================"
echo "RazorForge Rider Setup"
echo "========================================"
echo ""

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
    echo "You can still open the project in Rider and build from there."
    echo ""
else
    echo ""
    echo "[OK] Build successful!"
    echo ""
fi

echo ""
echo "========================================"
echo "Rider Configuration Summary"
echo "========================================"
echo ""
echo "The following have been pre-configured:"
echo ""
echo "  ✓ Run Configurations (.idea/.idea.RazorForge/.idea/runConfigurations/)"
echo "    - Build RazorForge"
echo "    - Compile: Current File"
echo "    - Compile: Intrinsics Demo"
echo "    - Compile: Primitive Types Demo"
echo "    - Compile: Console Demo"
echo "    - Compile: Suflae Current File"
echo "    - Compile: Suflae Console Demo"
echo "    - Language Server"
echo "    - Run: All Tests"
echo ""
echo "  ✓ File Templates (.idea/fileTemplates/)"
echo "    - RazorForge File.rf"
echo "    - RazorForge Record Type.rf"
echo "    - RazorForge Entity Type.rf"
echo "    - Suflae File.sf"
echo "    - Suflae Record Type.sf"
echo "    - Suflae Entity Type.sf"
echo ""
echo "  ✓ Inspection Profile (.idea/.idea.RazorForge/.idea/inspectionProfiles/)"
echo "    - RazorForge.xml"
echo ""
echo "========================================"
echo "Setup Complete!"
echo "========================================"
echo ""
echo "Next steps:"
echo "1. Open Rider (it will auto-detect the solution)"
echo "2. Select a run configuration from the dropdown"
echo "3. Press Shift+F10 to run or Shift+F9 to debug"
echo "4. See RIDER_SETUP.md for complete documentation"
echo ""
echo "Opening project in Rider..."
echo ""

# Try to find and launch Rider
RIDER_PATH=""

# Check common Rider installation paths
if [ "$(uname)" == "Darwin" ]; then
    # macOS
    if [ -d "/Applications/Rider.app" ]; then
        RIDER_PATH="/Applications/Rider.app/Contents/MacOS/rider"
    fi
elif [ "$(expr substr $(uname -s) 1 5)" == "Linux" ]; then
    # Linux - check common locations
    if [ -f "$HOME/.local/share/JetBrains/Toolbox/apps/Rider/ch-0/*/bin/rider.sh" ]; then
        # Toolbox installation
        RIDER_PATH=$(ls -t "$HOME/.local/share/JetBrains/Toolbox/apps/Rider/ch-0/"/*/bin/rider.sh 2>/dev/null | head -1)
    elif command -v rider &> /dev/null; then
        RIDER_PATH="rider"
    fi
fi

if [ -n "$RIDER_PATH" ] && [ -e "$RIDER_PATH" ]; then
    echo "[INFO] Launching Rider from: $RIDER_PATH"
    "$RIDER_PATH" "$(pwd)" &
else
    echo "[WARNING] Could not find Rider installation."
    echo "Please open the project manually in Rider."
    echo ""
    echo "If Rider is installed, you can launch it with:"
    echo "  rider ."
    echo ""
fi

echo ""
echo "Setup complete! See RIDER_SETUP.md for more information."
