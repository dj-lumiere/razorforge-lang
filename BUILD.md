# RazorForge Build System

This document describes RazorForge's comprehensive build system with multiple entry points and automation tools.

## Quick Start

```bash
# Simple unified commands
./rf-build dev          # Development build + tests
./rf-build release      # Production build
./rf-build ci           # Full CI pipeline

# Or use Make for development
make build              # Build everything
make test               # Run all tests
make check              # Format + lint + test
```

## Build System Overview

RazorForge uses a **multi-layered build system**:

1. **Native Layer** (C/C++) - CMake for native libraries
2. **Compiler Layer** (C#) - dotnet CLI for RazorForge compiler
3. **Automation Layer** - Scripts for testing, formatting, packaging
4. **Integration Layer** - Unified commands and CI/CD support

## Entry Points

### 1. Unified Build Script (Recommended)

**`./rf-build [command]`** - Single entry point for all operations

```bash
./rf-build dev          # Debug build + run tests
./rf-build release      # Release build
./rf-build test         # Run comprehensive tests
./rf-build format       # Format all source code
./rf-build package      # Create distribution packages
./rf-build ci           # Complete CI pipeline
./rf-build clean        # Clean all artifacts
```

### 2. Make Targets (Development)

**`make [target]`** - Task runner for development workflows

```bash
# Essential targets
make build              # Build compiler and native libs
make test               # Run all tests
make clean              # Clean build artifacts
make format             # Format source code
make check              # Format + lint + test
make package            # Create distribution packages

# Development helpers
make dev-setup          # Setup development environment
make watch              # Watch for changes and rebuild
make examples           # Build example projects
make docs               # Generate documentation

# Platform-specific
make native             # Build native libraries only
make compiler           # Build C# compiler only
```

### 3. Traditional Build Scripts

**`./build.sh [options]`** - CMake-based build (your existing system)

```bash
./build.sh --release --test     # Release build with tests
./build.sh --debug --verbose    # Debug build with output
./build.sh --clean --install    # Clean build and install
```

**Environment variables for enhanced functionality:**

```bash
RUN_TESTS_AFTER_BUILD=true ./build.sh --release
FORMAT_CODE=true ./build.sh --debug
CREATE_PACKAGE=true ./build.sh --release
```

### 4. Direct Tool Commands

**dotnet CLI** - For C# development

```bash
dotnet build --configuration Release
dotnet test --configuration Release
dotnet format                        # Format C# code
dotnet publish --runtime win-x64     # Create platform builds
```

**Scripts** - Individual automation tools

```bash
bash scripts/run-tests.sh           # Comprehensive test runner
bash scripts/format-code.sh         # Multi-language formatter
bash scripts/package.sh             # Distribution packager
```

## Development Workflows

### Daily Development

```bash
# Setup (once)
make dev-setup

# Development cycle
make check              # Before committing
./rf-build dev          # Development testing
make watch              # Continuous building
```

### Testing

```bash
# All tests
make test

# Specific test types
bash scripts/run-tests.sh --unit-only
bash scripts/run-tests.sh --integration-only
bash scripts/run-tests.sh --compiler-only

# With coverage
bash scripts/run-tests.sh --coverage
```

### Code Quality

```bash
# Format all code
make format
bash scripts/format-code.sh

# Check without changing
bash scripts/format-code.sh --verify-only

# Lint and analyze
make lint
```

### Release Process

```bash
# Complete release pipeline
./rf-build ci

# Or step by step
make format
make build
make test
make package

# Create platform-specific packages
bash scripts/package.sh --platform win-x64 --format zip
bash scripts/package.sh --version 2.0.0 --include-sources
```

## Configuration

### Build Types

- **Debug** - Development builds with symbols and debugging info
- **Release** - Optimized production builds
- **MinSizeRel** - Size-optimized builds
- **RelWithDebInfo** - Release builds with debugging info

### Supported Platforms

- **win-x64** - Windows 64-bit
- **linux-x64** - Linux 64-bit
- **osx-x64** - macOS Intel
- **osx-arm64** - macOS Apple Silicon

### Package Formats

- **zip** - Windows-friendly archives
- **tar.gz** - Unix-friendly archives
- **deb** - Debian packages (planned)
- **rpm** - RedHat packages (planned)

## Environment Variables

```bash
# Build configuration
BUILD_TYPE=Release|Debug        # Build configuration
JOBS=N                         # Parallel build jobs
VERSION=1.0.0                  # Package version

# Feature flags
RUN_TESTS_AFTER_BUILD=true     # Auto-run tests after build
FORMAT_CODE=true               # Auto-format during build
CREATE_PACKAGE=true            # Auto-create packages
VERBOSE=true                   # Verbose output
COVERAGE=true                  # Generate coverage reports
```

## IDE Integration

### Visual Studio Code

```bash
make vscode-setup              # Setup VS Code configuration
```

### JetBrains Rider

```bash
make rider-setup               # Setup Rider configuration
```

### Command Palette Integration

VS Code tasks are configured for:

- Build (Ctrl+Shift+P → "Tasks: Run Task" → "Build")
- Test (Ctrl+Shift+P → "Tasks: Run Task" → "Test")
- Format (Ctrl+Shift+P → "Tasks: Run Task" → "Format")

## CI/CD Integration

### GitHub Actions

```yaml
- name: Build and Test
  run: ./rf-build ci

- name: Create Release Packages
  run: bash scripts/package.sh --sign
```

### Other CI Systems

```bash
# Full pipeline
./rf-build ci

# Individual steps
bash scripts/format-code.sh --verify-only
./build.sh --release
bash scripts/run-tests.sh
bash scripts/package.sh
```

## Troubleshooting

### Common Issues

**Build fails with missing tools:**

```bash
make info                      # Check tool versions
make dev-setup                 # Install development tools
```

**Tests timeout:**

```bash
bash scripts/run-tests.sh --timeout 600    # Increase timeout
```

**Formatting issues:**

```bash
bash scripts/format-code.sh --verbose      # See detailed output
```

**Package creation fails:**

```bash
bash scripts/package.sh --platform win-x64 # Try specific platform
```

### Clean Builds

```bash
make clean                     # Standard clean
make deep-clean                # Remove all generated files
./build.sh --clean             # CMake clean
```

### Getting Help

```bash
./rf-build                     # Show quick commands
make help                      # Show all Make targets
./build.sh --help              # Show CMake options
bash scripts/run-tests.sh --help     # Test runner options
bash scripts/package.sh --help       # Packaging options
```

## Advanced Usage

### Custom Build Configurations

```bash
# Development with specific settings
BUILD_TYPE=Debug JOBS=1 VERBOSE=true make build

# Release with packaging
BUILD_TYPE=Release CREATE_PACKAGE=true ./build.sh --release
```

### Parallel Development

```bash
# Watch mode for continuous building
make watch

# Parallel testing
bash scripts/run-tests.sh --parallel

# Fast development cycle
make check && echo "Ready for commit!"
```

### Cross-Platform Building

```bash
# Build for all platforms
bash scripts/package.sh

# Specific platform
bash scripts/package.sh --platform linux-x64

# Multiple formats
bash scripts/package.sh --format zip,tar.gz
```
