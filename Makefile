# RazorForge Build System
# ======================
# Cross-platform build automation for RazorForge compiler

# Configuration
BUILD_TYPE ?= Release
JOBS ?= $(shell nproc 2>/dev/null || sysctl -n hw.ncpu 2>/dev/null || echo 4)
PROJECT_NAME = RazorForge
VERSION = 1.0.0

# Directories
SRC_DIR = src
TEST_DIR = tests
BUILD_DIR = build
BIN_DIR = bin
DIST_DIR = dist
NATIVE_DIR = native

# Platform detection
UNAME_S := $(shell uname -s)
ifeq ($(UNAME_S),Linux)
	PLATFORM = linux
	LIB_EXT = .so
endif
ifeq ($(UNAME_S),Darwin)
	PLATFORM = macos
	LIB_EXT = .dylib
endif
ifeq ($(OS),Windows_NT)
	PLATFORM = windows
	LIB_EXT = .dll
	SHELL_EXT = .bat
else
	SHELL_EXT = .sh
endif

# Default target
.PHONY: all
all: build

# Help target
.PHONY: help
help:
	@echo "RazorForge Build System"
	@echo "======================="
	@echo ""
	@echo "Common targets:"
	@echo "  all            - Build everything (default)"
	@echo "  build          - Build compiler and runtime"
	@echo "  clean          - Clean build artifacts"
	@echo "  test           - Run all tests"
	@echo "  format         - Format source code"
	@echo "  lint           - Run linters"
	@echo "  check          - Format + lint + test"
	@echo "  package        - Create distribution packages"
	@echo "  install        - Install to system"
	@echo "  dev-setup      - Setup development environment"
	@echo ""
	@echo "Advanced targets:"
	@echo "  native         - Build native libraries only"
	@echo "  compiler       - Build C# compiler only"
	@echo "  docs           - Generate documentation"
	@echo "  examples       - Build example projects"
	@echo "  benchmark      - Run performance benchmarks"
	@echo ""
	@echo "Configuration:"
	@echo "  BUILD_TYPE     - Release (default) or Debug"
	@echo "  JOBS           - Number of parallel jobs (default: $(JOBS))"
	@echo ""
	@echo "Examples:"
	@echo "  make BUILD_TYPE=Debug"
	@echo "  make test JOBS=8"
	@echo "  make check && make package"

# Build targets
.PHONY: build
build: native compiler

.PHONY: native
native:
	@echo "Building native libraries..."
	@if [ "$(OS)" = "Windows_NT" ]; then \
		cmd /c "$(NATIVE_DIR)\build.bat"; \
	else \
		bash $(NATIVE_DIR)/build.sh --$(shell echo $(BUILD_TYPE) | tr A-Z a-z) -j $(JOBS); \
	fi

.PHONY: compiler
compiler:
	@echo "Building $(PROJECT_NAME) compiler..."
	@dotnet build --configuration $(BUILD_TYPE) --verbosity minimal

# Clean targets
.PHONY: clean
clean:
	@echo "Cleaning build artifacts..."
	@rm -rf $(BUILD_DIR) $(BIN_DIR) obj
	@dotnet clean --verbosity minimal

.PHONY: deep-clean
deep-clean: clean
	@echo "Deep cleaning..."
	@rm -rf $(NATIVE_DIR)/build $(DIST_DIR)
	@find . -name "*.user" -delete
	@find . -name "bin" -type d -exec rm -rf {} + 2>/dev/null || true
	@find . -name "obj" -type d -exec rm -rf {} + 2>/dev/null || true

# Test targets
.PHONY: test
test: build
	@echo "Running tests..."
	@dotnet test --configuration $(BUILD_TYPE) --no-build --verbosity minimal

.PHONY: test-verbose
test-verbose: build
	@echo "Running tests (verbose)..."
	@dotnet test --configuration $(BUILD_TYPE) --no-build --verbosity normal

.PHONY: test-coverage
test-coverage: build
	@echo "Running tests with coverage..."
	@dotnet test --configuration $(BUILD_TYPE) --no-build --collect:"XPlat Code Coverage"

# Code quality targets
.PHONY: format
format:
	@echo "Formatting C# code..."
	@dotnet format --verbosity minimal

.PHONY: lint
lint:
	@echo "Running C# analyzer..."
	@dotnet build --configuration $(BUILD_TYPE) --verbosity minimal -p:TreatWarningsAsErrors=true

.PHONY: check
check: format lint test
	@echo "All checks passed!"

# Development setup
.PHONY: dev-setup
dev-setup:
	@echo "Setting up development environment..."
	@dotnet tool restore
	@echo "Installing git hooks..."
	@cp scripts/pre-commit.sh .git/hooks/pre-commit 2>/dev/null || echo "No pre-commit hook found"
	@chmod +x .git/hooks/pre-commit 2>/dev/null || true
	@echo "Development environment setup complete!"

# Package and distribution
.PHONY: package
package: build
	@echo "Creating distribution packages..."
	@mkdir -p $(DIST_DIR)
	@dotnet publish --configuration $(BUILD_TYPE) --output $(DIST_DIR)/$(PROJECT_NAME)
	@cp -r stdlib $(DIST_DIR)/$(PROJECT_NAME)/
	@cp -r examples $(DIST_DIR)/$(PROJECT_NAME)/
	@cp README.md LICENSE $(DIST_DIR)/$(PROJECT_NAME)/
	@echo "Package created in $(DIST_DIR)/$(PROJECT_NAME)"

.PHONY: install
install: package
	@echo "Installing $(PROJECT_NAME)..."
	@echo "Note: Install target needs to be customized for your system"
	@echo "Package is ready in $(DIST_DIR)/$(PROJECT_NAME)"

# Documentation
.PHONY: docs
docs:
	@echo "Generating documentation..."
	@echo "Note: Add documentation generation commands here"

# Examples and benchmarks
.PHONY: examples
examples: build
	@echo "Building examples..."
	@for example in examples/*/; do \
		if [ -f "$$example/build.sh" ]; then \
			echo "Building $$example"; \
			(cd "$$example" && bash build.sh); \
		fi; \
	done

.PHONY: benchmark
benchmark: build
	@echo "Running benchmarks..."
	@echo "Note: Add benchmark commands here"

# Watch mode for development
.PHONY: watch
watch:
	@echo "Watching for changes... (Press Ctrl+C to stop)"
	@while true; do \
		inotifywait -r -e modify,create,delete $(SRC_DIR) 2>/dev/null || \
		fswatch -1 -r $(SRC_DIR) 2>/dev/null || \
		(echo "Install inotifywait or fswatch for file watching" && sleep 5); \
		make build; \
	done

# IDE integration helpers
.PHONY: vscode-setup
vscode-setup:
	@echo "Setting up VS Code configuration..."
	@mkdir -p .vscode
	@echo "VS Code tasks and launch configurations can be added here"

.PHONY: rider-setup
rider-setup:
	@echo "JetBrains Rider setup..."
	@echo "Run configurations can be added to .idea/runConfigurations/"

# Version and info
.PHONY: version
version:
	@echo "$(PROJECT_NAME) v$(VERSION)"
	@echo "Build Type: $(BUILD_TYPE)"
	@echo "Platform: $(PLATFORM)"
	@echo "Jobs: $(JOBS)"

.PHONY: info
info: version
	@echo ""
	@echo "Build Environment:"
	@dotnet --version 2>/dev/null || echo "dotnet: not found"
	@cmake --version 2>/dev/null | head -n1 || echo "cmake: not found"
	@gcc --version 2>/dev/null | head -n1 || echo "gcc: not found"
	@clang --version 2>/dev/null | head -n1 || echo "clang: not found"