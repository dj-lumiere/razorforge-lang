#!/bin/bash
# RazorForge Test Runner
# ===================
# Comprehensive test runner for all test types

set -e

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m' # No Color

# Configuration
BUILD_TYPE="${BUILD_TYPE:-Release}"
VERBOSE="${VERBOSE:-false}"
RUN_UNIT="${RUN_UNIT:-true}"
RUN_INTEGRATION="${RUN_INTEGRATION:-true}"
RUN_COMPILER="${RUN_COMPILER:-true}"
RUN_EXAMPLES="${RUN_EXAMPLES:-true}"
PARALLEL="${PARALLEL:-true}"
COVERAGE="${COVERAGE:-false}"
TIMEOUT="${TIMEOUT:-300}"

# Directories
PROJECT_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
TEST_DIR="$PROJECT_ROOT/tests"
EXAMPLES_DIR="$PROJECT_ROOT/examples"
BUILD_DIR="$PROJECT_ROOT/bin/$BUILD_TYPE/net9.0"
RESULTS_DIR="$PROJECT_ROOT/test-results"

# Create results directory
mkdir -p "$RESULTS_DIR"

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
    echo "RazorForge Test Runner"
    echo ""
    echo "Usage: $0 [options]"
    echo ""
    echo "Options:"
    echo "  --unit-only       Run only unit tests"
    echo "  --integration-only Run only integration tests"
    echo "  --compiler-only   Run only compiler tests"
    echo "  --examples-only   Run only example tests"
    echo "  --coverage        Generate code coverage report"
    echo "  --verbose         Enable verbose output"
    echo "  --no-parallel     Disable parallel test execution"
    echo "  --timeout N       Set test timeout in seconds (default: $TIMEOUT)"
    echo "  -h, --help        Show this help message"
    echo ""
    echo "Environment variables:"
    echo "  BUILD_TYPE        Build configuration (Release|Debug)"
    echo "  RUN_UNIT          Run unit tests (true|false)"
    echo "  RUN_INTEGRATION   Run integration tests (true|false)"
    echo "  RUN_COMPILER      Run compiler tests (true|false)"
    echo "  RUN_EXAMPLES      Run example tests (true|false)"
}

# Parse command line arguments
while [[ $# -gt 0 ]]; do
    case $1 in
        --unit-only)
            RUN_UNIT=true
            RUN_INTEGRATION=false
            RUN_COMPILER=false
            RUN_EXAMPLES=false
            shift
            ;;
        --integration-only)
            RUN_UNIT=false
            RUN_INTEGRATION=true
            RUN_COMPILER=false
            RUN_EXAMPLES=false
            shift
            ;;
        --compiler-only)
            RUN_UNIT=false
            RUN_INTEGRATION=false
            RUN_COMPILER=true
            RUN_EXAMPLES=false
            shift
            ;;
        --examples-only)
            RUN_UNIT=false
            RUN_INTEGRATION=false
            RUN_COMPILER=false
            RUN_EXAMPLES=true
            shift
            ;;
        --coverage)
            COVERAGE=true
            shift
            ;;
        --verbose)
            VERBOSE=true
            shift
            ;;
        --no-parallel)
            PARALLEL=false
            shift
            ;;
        --timeout)
            if [[ -n $2 && $2 =~ ^[0-9]+$ ]]; then
                TIMEOUT=$2
                shift 2
            else
                print_error "Error: --timeout requires a numeric argument"
                exit 1
            fi
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
print_info "RazorForge Test Runner"
print_info "========================================="
print_info "Build Type: $BUILD_TYPE"
print_info "Verbose: $VERBOSE"
print_info "Coverage: $COVERAGE"
print_info "Parallel: $PARALLEL"
print_info "Timeout: ${TIMEOUT}s"
echo ""

# Check if build exists
if [ ! -d "$BUILD_DIR" ]; then
    print_warning "Build directory not found. Building project..."
    cd "$PROJECT_ROOT"
    dotnet build --configuration "$BUILD_TYPE"
fi

# Test counters
TOTAL_TESTS=0
PASSED_TESTS=0
FAILED_TESTS=0
SKIPPED_TESTS=0

# Function to run dotnet tests with timeout
run_dotnet_tests() {
    local test_filter="$1"
    local test_name="$2"

    print_info "Running $test_name..."

    local cmd="dotnet test --configuration $BUILD_TYPE --no-build --logger trx --results-directory $RESULTS_DIR"

    if [ "$VERBOSE" = "true" ]; then
        cmd="$cmd --verbosity normal"
    else
        cmd="$cmd --verbosity minimal"
    fi

    # Note: --parallel flag causes issues with MSBuild, so we skip it for now
    # if [ "$PARALLEL" = "true" ]; then
    #     cmd="$cmd --parallel"
    # fi

    if [ -n "$test_filter" ]; then
        cmd="$cmd --filter $test_filter"
    fi

    if [ "$COVERAGE" = "true" ]; then
        cmd="$cmd --collect:\"XPlat Code Coverage\""
    fi

    # Run with timeout
    if timeout "$TIMEOUT" $cmd; then
        print_success "$test_name passed"
        return 0
    else
        local exit_code=$?
        if [ $exit_code -eq 124 ]; then
            print_error "$test_name timed out after ${TIMEOUT}s"
        else
            print_error "$test_name failed (exit code: $exit_code)"
        fi
        return $exit_code
    fi
}

# Function to run RazorForge compiler tests
run_compiler_tests() {
    print_info "Running RazorForge compiler tests..."

    local test_files=()
    while IFS= read -r -d '' file; do
        test_files+=("$file")
    done < <(find "$TEST_DIR" -name "*.rf" -print0)

    if [ ${#test_files[@]} -eq 0 ]; then
        print_warning "No RazorForge test files found"
        return 0
    fi

    local passed=0
    local failed=0

    for test_file in "${test_files[@]}"; do
        local test_name=$(basename "$test_file" .rf)
        print_info "Testing: $test_name"

        # Compile the test file
        if timeout "$TIMEOUT" "$BUILD_DIR/RazorForge" compile "$test_file" > "$RESULTS_DIR/${test_name}.out" 2>&1; then
            print_success "Compiled: $test_name"
            ((passed++))
        else
            print_error "Failed to compile: $test_name"
            if [ "$VERBOSE" = "true" ]; then
                cat "$RESULTS_DIR/${test_name}.out"
            fi
            ((failed++))
        fi
    done

    print_info "RazorForge compiler tests: $passed passed, $failed failed"
    TOTAL_TESTS=$((TOTAL_TESTS + passed + failed))
    PASSED_TESTS=$((PASSED_TESTS + passed))
    FAILED_TESTS=$((FAILED_TESTS + failed))

    [ $failed -eq 0 ]
}

# Function to run example tests
run_example_tests() {
    print_info "Running example tests..."

    if [ ! -d "$EXAMPLES_DIR" ]; then
        print_warning "Examples directory not found"
        return 0
    fi

    local passed=0
    local failed=0

    for example_dir in "$EXAMPLES_DIR"/*; do
        if [ -d "$example_dir" ]; then
            local example_name=$(basename "$example_dir")
            print_info "Testing example: $example_name"

            # Look for build script or main file
            if [ -f "$example_dir/build.sh" ]; then
                cd "$example_dir"
                if timeout "$TIMEOUT" bash build.sh > "$RESULTS_DIR/example_${example_name}.out" 2>&1; then
                    print_success "Example built: $example_name"
                    ((passed++))
                else
                    print_error "Example failed: $example_name"
                    if [ "$VERBOSE" = "true" ]; then
                        cat "$RESULTS_DIR/example_${example_name}.out"
                    fi
                    ((failed++))
                fi
                cd "$PROJECT_ROOT"
            else
                print_warning "No build script found for example: $example_name"
                ((SKIPPED_TESTS++))
            fi
        fi
    done

    print_info "Example tests: $passed passed, $failed failed"
    TOTAL_TESTS=$((TOTAL_TESTS + passed + failed))
    PASSED_TESTS=$((PASSED_TESTS + passed))
    FAILED_TESTS=$((FAILED_TESTS + failed))

    [ $failed -eq 0 ]
}

# Change to project root
cd "$PROJECT_ROOT"

# Track overall success
OVERALL_SUCCESS=true

# Run unit tests
if [ "$RUN_UNIT" = "true" ]; then
    if ! run_dotnet_tests "TestCategory=Unit" "Unit Tests"; then
        OVERALL_SUCCESS=false
    fi
fi

# Run integration tests
if [ "$RUN_INTEGRATION" = "true" ]; then
    if ! run_dotnet_tests "TestCategory=Integration" "Integration Tests"; then
        OVERALL_SUCCESS=false
    fi
fi

# Run compiler tests
if [ "$RUN_COMPILER" = "true" ]; then
    if ! run_compiler_tests; then
        OVERALL_SUCCESS=false
    fi
fi

# Run example tests
if [ "$RUN_EXAMPLES" = "true" ]; then
    if ! run_example_tests; then
        OVERALL_SUCCESS=false
    fi
fi

# Generate coverage report if requested
if [ "$COVERAGE" = "true" ]; then
    print_info "Generating coverage report..."
    # Add coverage report generation commands here
    # For example: reportgenerator -reports:**/coverage.cobertura.xml -targetdir:coverage-report
fi

# Print summary
print_info ""
print_info "========================================="
print_info "Test Summary"
print_info "========================================="
print_info "Total Tests: $TOTAL_TESTS"
print_success "Passed: $PASSED_TESTS"
print_error "Failed: $FAILED_TESTS"
print_warning "Skipped: $SKIPPED_TESTS"

if [ "$OVERALL_SUCCESS" = "true" ]; then
    print_success ""
    print_success "All tests passed! 🎉"
    exit 0
else
    print_error ""
    print_error "Some tests failed! ❌"
    exit 1
fi