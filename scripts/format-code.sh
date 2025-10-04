#!/bin/bash
# RazorForge Code Formatter
# ========================
# Format and lint all source code

set -e

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m' # No Color

# Configuration
PROJECT_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DRY_RUN=false
VERIFY_ONLY=false
VERBOSE=false

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
    echo "RazorForge Code Formatter"
    echo ""
    echo "Usage: $0 [options]"
    echo ""
    echo "Options:"
    echo "  --dry-run         Show what would be changed without making changes"
    echo "  --verify-only     Check if code is properly formatted (exit 1 if not)"
    echo "  --verbose         Enable verbose output"
    echo "  -h, --help        Show this help message"
    echo ""
    echo "Examples:"
    echo "  $0                Format all source code"
    echo "  $0 --dry-run      Preview formatting changes"
    echo "  $0 --verify-only  Check formatting in CI"
}

# Parse command line arguments
while [[ $# -gt 0 ]]; do
    case $1 in
        --dry-run)
            DRY_RUN=true
            shift
            ;;
        --verify-only)
            VERIFY_ONLY=true
            shift
            ;;
        --verbose)
            VERBOSE=true
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
print_info "RazorForge Code Formatter"
print_info "========================================="
print_info "Project Root: $PROJECT_ROOT"
print_info "Dry Run: $DRY_RUN"
print_info "Verify Only: $VERIFY_ONLY"
print_info "Verbose: $VERBOSE"
echo ""

cd "$PROJECT_ROOT"

# Check if dotnet format is available
if ! command -v dotnet &> /dev/null; then
    print_error "dotnet CLI not found in PATH"
    exit 1
fi

# Format C# code
format_csharp() {
    print_info "Formatting C# code..."

    local cmd="dotnet format RazorForge.sln"

    if [ "$VERIFY_ONLY" = "true" ]; then
        cmd="$cmd --verify-no-changes"
    elif [ "$DRY_RUN" = "true" ]; then
        # dotnet format doesn't support --dry-run, so we'll just verify instead
        cmd="$cmd --verify-no-changes"
        print_info "Note: dotnet format doesn't support --dry-run, using --verify-no-changes instead"
    fi

    if [ "$VERBOSE" = "true" ]; then
        cmd="$cmd --verbosity diagnostic"
    else
        cmd="$cmd --verbosity minimal"
    fi

    if $cmd; then
        if [ "$VERIFY_ONLY" = "true" ]; then
            print_success "C# code is properly formatted"
        else
            print_success "C# code formatted successfully"
        fi
        return 0
    else
        if [ "$VERIFY_ONLY" = "true" ]; then
            print_error "C# code formatting check failed"
        else
            print_error "C# code formatting failed"
        fi
        return 1
    fi
}

# Format shell scripts
format_shell() {
    print_info "Checking shell scripts..."

    local shell_files=()
    while IFS= read -r -d '' file; do
        shell_files+=("$file")
    done < <(find . -name "*.sh" -not -path "./.git/*" -print0)

    if [ ${#shell_files[@]} -eq 0 ]; then
        print_info "No shell scripts found"
        return 0
    fi

    # Check if shfmt is available
    if command -v shfmt &> /dev/null; then
        for file in "${shell_files[@]}"; do
            if [ "$VERIFY_ONLY" = "true" ]; then
                if shfmt -d "$file" > /dev/null; then
                    print_success "Shell script formatted correctly: $file"
                else
                    print_error "Shell script needs formatting: $file"
                    return 1
                fi
            elif [ "$DRY_RUN" = "true" ]; then
                if ! shfmt -d "$file" > /dev/null; then
                    print_info "Would format: $file"
                    if [ "$VERBOSE" = "true" ]; then
                        shfmt -d "$file"
                    fi
                fi
            else
                if shfmt -w "$file"; then
                    print_success "Formatted shell script: $file"
                else
                    print_error "Failed to format shell script: $file"
                fi
            fi
        done
    else
        print_warning "shfmt not found - skipping shell script formatting"
        print_info "Install shfmt for shell script formatting: go install mvdan.cc/sh/v3/cmd/shfmt@latest"
    fi

    return 0
}

# Format CMake files
format_cmake() {
    print_info "Checking CMake files..."

    local cmake_files=()
    while IFS= read -r -d '' file; do
        cmake_files+=("$file")
    done < <(find . -name "CMakeLists.txt" -o -name "*.cmake" -not -path "./.git/*" -print0)

    if [ ${#cmake_files[@]} -eq 0 ]; then
        print_info "No CMake files found"
        return 0
    fi

    # Check if cmake-format is available
    if command -v cmake-format &> /dev/null; then
        for file in "${cmake_files[@]}"; do
            if [ "$VERIFY_ONLY" = "true" ]; then
                if cmake-format --check "$file"; then
                    print_success "CMake file formatted correctly: $file"
                else
                    print_error "CMake file needs formatting: $file"
                    return 1
                fi
            elif [ "$DRY_RUN" = "true" ]; then
                if ! cmake-format --check "$file" 2>/dev/null; then
                    print_info "Would format: $file"
                fi
            else
                if cmake-format -i "$file"; then
                    print_success "Formatted CMake file: $file"
                else
                    print_error "Failed to format CMake file: $file"
                fi
            fi
        done
    else
        print_warning "cmake-format not found - skipping CMake file formatting"
        print_info "Install cmake-format: pip install cmakelang"
    fi

    return 0
}

# Run all formatters
OVERALL_SUCCESS=true

if ! format_csharp; then
    OVERALL_SUCCESS=false
fi

if ! format_shell; then
    OVERALL_SUCCESS=false
fi

if ! format_cmake; then
    OVERALL_SUCCESS=false
fi

# Print summary
print_info ""
print_info "========================================="
print_info "Formatting Summary"
print_info "========================================="

if [ "$OVERALL_SUCCESS" = "true" ]; then
    if [ "$VERIFY_ONLY" = "true" ]; then
        print_success "All code is properly formatted! ✨"
    else
        print_success "All code has been formatted! ✨"
    fi
    exit 0
else
    if [ "$VERIFY_ONLY" = "true" ]; then
        print_error "Some code needs formatting! Please run: make format"
    else
        print_error "Some formatting operations failed!"
    fi
    exit 1
fi