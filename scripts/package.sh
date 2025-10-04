#!/bin/bash
# RazorForge Packaging Script
# ==========================
# Create distribution packages for multiple platforms

set -e

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m' # No Color

# Configuration
PROJECT_NAME="RazorForge"
VERSION="${VERSION:-1.0.0}"
BUILD_TYPE="${BUILD_TYPE:-Release}"
PROJECT_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DIST_DIR="$PROJECT_ROOT/dist"
BUILD_DIR="$PROJECT_ROOT/build"

# Package configurations
PLATFORMS=("win-x64" "linux-x64" "osx-x64" "osx-arm64")
PACKAGE_FORMATS=("zip" "tar.gz")
INCLUDE_SOURCES=false
INCLUDE_DOCS=true
INCLUDE_EXAMPLES=true
SIGN_PACKAGES=false

# Function to print colored output
print_status() {
    local color=$1
    local message=$2
    echo -e "${color}$message${NC}"
}

print_success() { print_status "$GREEN" "✓ $1"; }
print_error() { print_status "$RED" "✗ $1"; }
print_warning() { print_status "$YELLOW" "⚠ $1"; }
print_info() { print_status "$BLUE" "ℹ $1"; }

# Function to show help
show_help() {
    echo "RazorForge Packaging Script"
    echo ""
    echo "Usage: $0 [options]"
    echo ""
    echo "Options:"
    echo "  --version VERSION    Set package version (default: $VERSION)"
    echo "  --platform PLATFORM  Build for specific platform only"
    echo "  --format FORMAT      Package format: zip, tar.gz, deb, rpm (default: zip,tar.gz)"
    echo "  --include-sources    Include source code in package"
    echo "  --no-docs           Exclude documentation from package"
    echo "  --no-examples       Exclude examples from package"
    echo "  --sign              Sign packages (requires signing setup)"
    echo "  --clean             Clean dist directory before packaging"
    echo "  -h, --help          Show this help message"
    echo ""
    echo "Supported platforms:"
    for platform in "${PLATFORMS[@]}"; do
        echo "  $platform"
    done
    echo ""
    echo "Examples:"
    echo "  $0                                    # Package for all platforms"
    echo "  $0 --platform win-x64 --format zip   # Windows ZIP only"
    echo "  $0 --version 2.0.0 --include-sources # Include source code"
}

# Parse command line arguments
while [[ $# -gt 0 ]]; do
    case $1 in
        --version)
            if [[ -n $2 ]]; then
                VERSION=$2
                shift 2
            else
                print_error "Error: --version requires a version string"
                exit 1
            fi
            ;;
        --platform)
            if [[ -n $2 ]]; then
                PLATFORMS=("$2")
                shift 2
            else
                print_error "Error: --platform requires a platform name"
                exit 1
            fi
            ;;
        --format)
            if [[ -n $2 ]]; then
                IFS=',' read -ra PACKAGE_FORMATS <<< "$2"
                shift 2
            else
                print_error "Error: --format requires format specification"
                exit 1
            fi
            ;;
        --include-sources)
            INCLUDE_SOURCES=true
            shift
            ;;
        --no-docs)
            INCLUDE_DOCS=false
            shift
            ;;
        --no-examples)
            INCLUDE_EXAMPLES=false
            shift
            ;;
        --sign)
            SIGN_PACKAGES=true
            shift
            ;;
        --clean)
            print_info "Cleaning dist directory..."
            rm -rf "$DIST_DIR"
            shift
            ;;
        -h|--help)
            show_help
            exit 0
            ;;
        *)
            print_error "Error: Unknown option $1"
            show_help
            exit 1
            ;;
    esac
done

print_info "========================================="
print_info "RazorForge Packaging"
print_info "========================================="
print_info "Version: $VERSION"
print_info "Build Type: $BUILD_TYPE"
print_info "Platforms: ${PLATFORMS[*]}"
print_info "Formats: ${PACKAGE_FORMATS[*]}"
print_info "Include Sources: $INCLUDE_SOURCES"
print_info "Include Docs: $INCLUDE_DOCS"
print_info "Include Examples: $INCLUDE_EXAMPLES"
print_info "Sign Packages: $SIGN_PACKAGES"
echo ""

cd "$PROJECT_ROOT"

# Create dist directory
mkdir -p "$DIST_DIR"

# Function to build for specific platform
build_platform() {
    local platform=$1
    local build_dir="$DIST_DIR/build-$platform"
    local package_dir="$DIST_DIR/package-$platform"

    print_info "Building for platform: $platform"

    # Clean previous build
    rm -rf "$build_dir" "$package_dir"
    mkdir -p "$build_dir" "$package_dir"

    # Build the project for this platform
    if ! dotnet publish \
        --configuration "$BUILD_TYPE" \
        --runtime "$platform" \
        --self-contained true \
        --output "$build_dir" \
        --verbosity minimal; then
        print_error "Failed to build for platform: $platform"
        return 1
    fi

    # Create package directory structure
    local package_name="${PROJECT_NAME}-${VERSION}-${platform}"
    local target_dir="$package_dir/$package_name"
    mkdir -p "$target_dir"

    # Copy main application
    cp -r "$build_dir"/* "$target_dir/"

    # Copy standard library
    if [ -d "stdlib" ]; then
        cp -r stdlib "$target_dir/"
        print_success "Included standard library"
    fi

    # Copy documentation
    if [ "$INCLUDE_DOCS" = "true" ] && [ -d "docs" ]; then
        cp -r docs "$target_dir/"
        print_success "Included documentation"
    fi

    # Copy wiki
    if [ "$INCLUDE_DOCS" = "true" ] && [ -d "wiki" ]; then
        cp -r wiki "$target_dir/"
        print_success "Included wiki"
    fi

    # Copy examples
    if [ "$INCLUDE_EXAMPLES" = "true" ] && [ -d "examples" ]; then
        cp -r examples "$target_dir/"
        print_success "Included examples"
    fi

    # Copy source code if requested
    if [ "$INCLUDE_SOURCES" = "true" ]; then
        mkdir -p "$target_dir/source"
        cp -r src "$target_dir/source/"
        if [ -d "native" ]; then
            cp -r native "$target_dir/source/"
        fi
        cp *.csproj *.sln CMakeLists.txt "$target_dir/source/" 2>/dev/null || true
        print_success "Included source code"
    fi

    # Copy license and readme
    cp LICENSE "$target_dir/" 2>/dev/null || print_warning "No LICENSE file found"
    cp README.md "$target_dir/" 2>/dev/null || print_warning "No README.md file found"

    # Create platform-specific files
    case $platform in
        win-*)
            # Create Windows batch file
            cat > "$target_dir/razorforge.bat" << 'EOF'
@echo off
"%~dp0RazorForge.exe" %*
EOF
            chmod +x "$target_dir/razorforge.bat"
            ;;
        *)
            # Create Unix shell script
            cat > "$target_dir/razorforge" << 'EOF'
#!/bin/bash
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
exec "$SCRIPT_DIR/RazorForge" "$@"
EOF
            chmod +x "$target_dir/razorforge"
            ;;
    esac

    # Create version info file
    cat > "$target_dir/VERSION" << EOF
${PROJECT_NAME} ${VERSION}
Platform: ${platform}
Build Type: ${BUILD_TYPE}
Build Date: $(date -u +"%Y-%m-%d %H:%M:%S UTC")
EOF

    print_success "Package prepared for $platform"
    return 0
}

# Function to create archive
create_archive() {
    local platform=$1
    local format=$2
    local package_dir="$DIST_DIR/package-$platform"
    local package_name="${PROJECT_NAME}-${VERSION}-${platform}"

    cd "$package_dir"

    case $format in
        zip)
            local archive_path="$DIST_DIR/${package_name}.zip"
            if command -v zip &> /dev/null; then
                zip -r "$archive_path" "$package_name"
                print_success "Created ZIP: $(basename "$archive_path")"
            else
                print_warning "zip command not found, skipping ZIP creation"
                return 1
            fi
            ;;
        tar.gz)
            local archive_path="$DIST_DIR/${package_name}.tar.gz"
            tar -czf "$archive_path" "$package_name"
            print_success "Created tar.gz: $(basename "$archive_path")"
            ;;
        deb)
            print_warning "DEB package creation not yet implemented"
            return 1
            ;;
        rpm)
            print_warning "RPM package creation not yet implemented"
            return 1
            ;;
        *)
            print_error "Unknown package format: $format"
            return 1
            ;;
    esac

    # Sign package if requested
    if [ "$SIGN_PACKAGES" = "true" ]; then
        sign_package "$archive_path"
    fi

    cd "$PROJECT_ROOT"
    return 0
}

# Function to sign packages
sign_package() {
    local package_path=$1
    print_info "Signing package: $(basename "$package_path")"

    # Add signing logic here based on your signing setup
    # Example for GPG:
    # gpg --detach-sign --armor "$package_path"

    print_warning "Package signing not configured"
}

# Function to calculate checksums
calculate_checksums() {
    print_info "Calculating checksums..."

    local checksum_file="$DIST_DIR/checksums.txt"
    rm -f "$checksum_file"

    cd "$DIST_DIR"
    for file in *.zip *.tar.gz 2>/dev/null; do
        if [ -f "$file" ]; then
            if command -v sha256sum &> /dev/null; then
                sha256sum "$file" >> checksums.txt
            elif command -v shasum &> /dev/null; then
                shasum -a 256 "$file" >> checksums.txt
            else
                print_warning "No checksum tool found"
                break
            fi
        fi
    done

    if [ -f checksums.txt ]; then
        print_success "Checksums calculated: checksums.txt"
    fi

    cd "$PROJECT_ROOT"
}

# Build and package for each platform
OVERALL_SUCCESS=true

for platform in "${PLATFORMS[@]}"; do
    if build_platform "$platform"; then
        for format in "${PACKAGE_FORMATS[@]}"; do
            if ! create_archive "$platform" "$format"; then
                OVERALL_SUCCESS=false
            fi
        done
    else
        OVERALL_SUCCESS=false
    fi

    # Clean up intermediate directories
    rm -rf "$DIST_DIR/build-$platform" "$DIST_DIR/package-$platform"
done

# Calculate checksums
calculate_checksums

# Print summary
print_info ""
print_info "========================================="
print_info "Packaging Summary"
print_info "========================================="
print_info "Packages created in: $DIST_DIR"

ls -la "$DIST_DIR"/*.{zip,tar.gz} 2>/dev/null || print_info "No packages found"

if [ "$OVERALL_SUCCESS" = "true" ]; then
    print_success ""
    print_success "All packages created successfully! 📦"
    exit 0
else
    print_error ""
    print_error "Some packaging operations failed! ❌"
    exit 1
fi