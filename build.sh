#!/bin/bash
# RazorForge CMake Build Script for Unix systems

set -e

# Default build configuration
BUILD_TYPE="Release"
BUILD_DIR="build"
CLEAN_BUILD=false
VERBOSE=false
INSTALL=false
PACKAGE=false
RUN_TESTS=false
JOBS=$(nproc 2>/dev/null || sysctl -n hw.ncpu 2>/dev/null || echo 4)

# Function to show help
show_help() {
    echo "RazorForge CMake Build Script"
    echo ""
    echo "Usage: $0 [options]"
    echo ""
    echo "Options:"
    echo "  --debug      Build in Debug mode (default: Release)"
    echo "  --release    Build in Release mode"
    echo "  --clean      Clean build directory before building"
    echo "  --verbose    Enable verbose build output"
    echo "  --install    Install after building"
    echo "  --package    Create package after building"
    echo "  --test       Run tests after building"
    echo "  -j N         Use N parallel jobs (default: $JOBS)"
    echo "  -h, --help   Show this help message"
    echo ""
    echo "Examples:"
    echo "  $0 --debug --test"
    echo "  $0 --release --install --package"
    echo "  $0 --clean --verbose -j 8"
}

# Parse command line arguments
while [[ $# -gt 0 ]]; do
    case $1 in
        --debug)
            BUILD_TYPE="Debug"
            shift
            ;;
        --release)
            BUILD_TYPE="Release"
            shift
            ;;
        --clean)
            CLEAN_BUILD=true
            shift
            ;;
        --verbose)
            VERBOSE=true
            shift
            ;;
        --install)
            INSTALL=true
            shift
            ;;
        --package)
            PACKAGE=true
            shift
            ;;
        --test)
            RUN_TESTS=true
            shift
            ;;
        -j)
            if [[ -n $2 && $2 =~ ^[0-9]+$ ]]; then
                JOBS=$2
                shift 2
            else
                echo "Error: -j requires a numeric argument"
                exit 1
            fi
            ;;
        -h|--help)
            show_help
            exit 0
            ;;
        *)
            echo "Error: Unknown option $1"
            show_help
            exit 1
            ;;
    esac
done

echo "======================================"
echo "RazorForge CMake Build Script"
echo "======================================"
echo "Build Type: $BUILD_TYPE"
echo "Build Directory: $BUILD_DIR"
echo "Clean Build: $CLEAN_BUILD"
echo "Verbose: $VERBOSE"
echo "Parallel Jobs: $JOBS"
echo ""

# Check for required tools
if ! command -v cmake &> /dev/null; then
    echo "ERROR: CMake not found in PATH"
    echo "Please install CMake and add it to your PATH"
    exit 1
fi

if ! command -v dotnet &> /dev/null; then
    echo "ERROR: .NET SDK not found in PATH"
    echo "Please install .NET 9.0+ SDK and add it to your PATH"
    exit 1
fi

# Clean build directory if requested
if [ "$CLEAN_BUILD" = true ]; then
    echo "Cleaning build directory..."
    rm -rf "$BUILD_DIR"
fi

# Create build directory
mkdir -p "$BUILD_DIR"

# Configure with CMake
echo "Configuring with CMake..."
cd "$BUILD_DIR"

CMAKE_ARGS="-DCMAKE_BUILD_TYPE=$BUILD_TYPE"

if [ "$VERBOSE" = true ]; then
    CMAKE_ARGS="$CMAKE_ARGS -DCMAKE_VERBOSE_MAKEFILE=ON"
fi

# Add platform-specific optimizations
case "$(uname -s)" in
    Linux*)
        CMAKE_ARGS="$CMAKE_ARGS -DCMAKE_EXE_LINKER_FLAGS=-Wl,--as-needed"
        ;;
    Darwin*)
        CMAKE_ARGS="$CMAKE_ARGS -DCMAKE_OSX_DEPLOYMENT_TARGET=10.15"
        ;;
esac

cmake $CMAKE_ARGS ..

# Build
echo "Building..."
if [ "$VERBOSE" = true ]; then
    cmake --build . --config "$BUILD_TYPE" --parallel "$JOBS" --verbose
else
    cmake --build . --config "$BUILD_TYPE" --parallel "$JOBS"
fi

# Run tests if requested
if [ "$RUN_TESTS" = true ]; then
    echo "Running tests..."
    if ! ctest --build-config "$BUILD_TYPE" --output-on-failure --parallel "$JOBS"; then
        echo "WARNING: Some tests failed"
    fi
fi

# Install if requested
if [ "$INSTALL" = true ]; then
    echo "Installing..."
    cmake --install . --config "$BUILD_TYPE"
fi

# Create package if requested
if [ "$PACKAGE" = true ]; then
    echo "Creating package..."
    cpack --config "$BUILD_TYPE"
fi

echo ""
echo "======================================"
echo "Build completed successfully!"
echo "======================================"
echo "Build artifacts are in: $BUILD_DIR/bin"
echo ""

# Run additional tasks if requested via environment variables
if [ "$RUN_TESTS_AFTER_BUILD" = "true" ]; then
    echo "Running comprehensive tests..."
    bash scripts/run-tests.sh
fi

if [ "$FORMAT_CODE" = "true" ]; then
    echo "Formatting code..."
    bash scripts/format-code.sh
fi

if [ "$CREATE_PACKAGE" = "true" ]; then
    echo "Creating packages..."
    bash scripts/package.sh
fi

echo "Available additional commands:"
echo "  make help       - Show all build system options"
echo "  make test       - Run comprehensive tests"
echo "  make format     - Format all source code"
echo "  make package    - Create distribution packages"

cd ..