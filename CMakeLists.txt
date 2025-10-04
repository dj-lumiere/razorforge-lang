cmake_minimum_required(VERSION 3.20)
project(RazorForge VERSION 1.0.0 LANGUAGES C CXX)

# Project description
set(PROJECT_DESCRIPTION "RazorForge Programming Language - High-performance systems programming with memory safety")
set(PROJECT_HOMEPAGE_URL "https://github.com/razorforge/razorforge")

# Set C and C++ standards
set(CMAKE_C_STANDARD 11)
set(CMAKE_CXX_STANDARD 17)
set(CMAKE_C_STANDARD_REQUIRED ON)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

# Build type configuration
if(NOT CMAKE_BUILD_TYPE)
    set(CMAKE_BUILD_TYPE Release CACHE STRING "Choose the type of build" FORCE)
    set_property(CACHE CMAKE_BUILD_TYPE PROPERTY STRINGS "Debug" "Release" "MinSizeRel" "RelWithDebInfo")
endif()

# Platform detection
if(WIN32)
    set(PLATFORM_NAME "windows")
    set(LIB_SUFFIX ".dll")
    set(LIB_PREFIX "")
    set(EXE_SUFFIX ".exe")
elseif(APPLE)
    set(PLATFORM_NAME "macos")
    set(LIB_SUFFIX ".dylib")
    set(LIB_PREFIX "lib")
    set(EXE_SUFFIX "")
else()
    set(PLATFORM_NAME "linux")
    set(LIB_SUFFIX ".so")
    set(LIB_PREFIX "lib")
    set(EXE_SUFFIX "")
endif()

# Architecture detection
if(CMAKE_SIZEOF_VOID_P EQUAL 8)
    set(ARCH_NAME "x64")
else()
    set(ARCH_NAME "x86")
endif()

# Output directories
set(CMAKE_RUNTIME_OUTPUT_DIRECTORY ${CMAKE_BINARY_DIR}/bin/${PLATFORM_NAME}/${ARCH_NAME})
set(CMAKE_LIBRARY_OUTPUT_DIRECTORY ${CMAKE_BINARY_DIR}/lib/${PLATFORM_NAME}/${ARCH_NAME})
set(CMAKE_ARCHIVE_OUTPUT_DIRECTORY ${CMAKE_BINARY_DIR}/lib/${PLATFORM_NAME}/${ARCH_NAME})

# Options
option(BUILD_NATIVE_RUNTIME "Build native runtime libraries" ON)
option(BUILD_MATH_LIBRARIES "Build mathematical computation libraries (libdfp, libbf, mafm)" ON)
option(BUILD_TESTS "Build and run tests" ON)
option(BUILD_LANGUAGE_SERVER "Build the Language Server Protocol implementation" ON)
option(BUILD_VSCODE_EXTENSION "Build VS Code extension" ON)
option(BUILD_DOCUMENTATION "Build documentation" OFF)
option(ENABLE_LTO "Enable Link Time Optimization" OFF)
option(ENABLE_SANITIZERS "Enable sanitizers (debug builds only)" OFF)

# Compiler-specific flags
if(MSVC)
    # MSVC-specific flags
    add_compile_options(/W4 /permissive-)
    if(ENABLE_LTO AND CMAKE_BUILD_TYPE STREQUAL "Release")
        add_compile_options(/GL)
        add_link_options(/LTCG)
    endif()
    # Enable static runtime in Release builds
    if(CMAKE_BUILD_TYPE STREQUAL "Release")
        set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded")
    else()
        set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreadedDebug")
    endif()
else()
    # GCC/Clang flags
    add_compile_options(-Wall -Wextra -Wpedantic)
    if(CMAKE_BUILD_TYPE STREQUAL "Debug")
        add_compile_options(-g -O0)
        if(ENABLE_SANITIZERS)
            add_compile_options(-fsanitize=address -fsanitize=undefined)
            add_link_options(-fsanitize=address -fsanitize=undefined)
        endif()
    else()
        add_compile_options(-O3 -DNDEBUG)
        if(ENABLE_LTO)
            add_compile_options(-flto)
            add_link_options(-flto)
        endif()
    endif()
endif()

# Add custom CMake modules path
list(APPEND CMAKE_MODULE_PATH "${CMAKE_SOURCE_DIR}/cmake")

# Find required packages
find_package(PkgConfig QUIET)
find_package(DotNet REQUIRED)

# Check for Node.js (for VS Code extension)
if(BUILD_VSCODE_EXTENSION)
    find_package(NodeJS QUIET)
    if(NOT NODEJS_FOUND)
        message(WARNING "Node.js/npm not found. VS Code extension build will be skipped.")
        set(BUILD_VSCODE_EXTENSION OFF)
    else()
        message(STATUS "Found Node.js version: ${NODEJS_VERSION}")
        message(STATUS "Found npm version: ${NPM_VERSION}")
    endif()
endif()

# Build native runtime libraries
if(BUILD_NATIVE_RUNTIME)
    message(STATUS "Building native runtime libraries")
    add_subdirectory(native)
endif()

# .NET Core main project
add_custom_target(razorforge_compiler ALL
    COMMAND ${DOTNET_EXECUTABLE} build --configuration ${CMAKE_BUILD_TYPE} --verbosity normal
    WORKING_DIRECTORY ${CMAKE_SOURCE_DIR}
    COMMENT "Building RazorForge .NET compiler"
    VERBATIM
)

# Make .NET build depend on native libraries
if(BUILD_NATIVE_RUNTIME)
    add_dependencies(razorforge_compiler razorforge_runtime)
endif()

# Language Server target
if(BUILD_LANGUAGE_SERVER)
    add_custom_target(razorforge_lsp ALL
        COMMAND ${DOTNET_EXECUTABLE} publish --configuration ${CMAKE_BUILD_TYPE} --output ${CMAKE_RUNTIME_OUTPUT_DIRECTORY}/lsp
        WORKING_DIRECTORY ${CMAKE_SOURCE_DIR}
        COMMENT "Building RazorForge Language Server"
        DEPENDS razorforge_compiler
        VERBATIM
    )
endif()

# VS Code extension target
if(BUILD_VSCODE_EXTENSION)
    add_custom_target(vscode_extension_install
        COMMAND ${NPM_EXECUTABLE} install
        WORKING_DIRECTORY ${CMAKE_SOURCE_DIR}/vscode-extension
        COMMENT "Installing VS Code extension dependencies"
        VERBATIM
    )

    add_custom_target(vscode_extension ALL
        COMMAND ${NPM_EXECUTABLE} run compile
        WORKING_DIRECTORY ${CMAKE_SOURCE_DIR}/vscode-extension
        COMMENT "Building VS Code extension"
        DEPENDS vscode_extension_install
        VERBATIM
    )

    add_custom_target(vscode_extension_package
        COMMAND ${NPM_EXECUTABLE} run vscode:prepublish
        WORKING_DIRECTORY ${CMAKE_SOURCE_DIR}/vscode-extension
        COMMENT "Packaging VS Code extension"
        DEPENDS vscode_extension
        VERBATIM
    )
endif()

# Test targets
if(BUILD_TESTS)
    enable_testing()

    # .NET tests
    add_custom_target(test_dotnet
        COMMAND ${DOTNET_EXECUTABLE} test --configuration ${CMAKE_BUILD_TYPE} --logger "console;verbosity=normal"
        WORKING_DIRECTORY ${CMAKE_SOURCE_DIR}
        COMMENT "Running .NET tests"
        DEPENDS razorforge_compiler
        VERBATIM
    )

    # VS Code extension tests
    if(BUILD_VSCODE_EXTENSION)
        add_custom_target(test_vscode_extension
            COMMAND ${NPM_EXECUTABLE} test
            WORKING_DIRECTORY ${CMAKE_SOURCE_DIR}/vscode-extension
            COMMENT "Running VS Code extension tests"
            DEPENDS vscode_extension
            VERBATIM
        )
    endif()

    # Integration tests target
    add_custom_target(test_integration
        COMMAND ${DOTNET_EXECUTABLE} test tests/ --configuration ${CMAKE_BUILD_TYPE} --logger "console;verbosity=normal"
        WORKING_DIRECTORY ${CMAKE_SOURCE_DIR}
        COMMENT "Running integration tests"
        DEPENDS razorforge_compiler
        VERBATIM
    )

    # All tests target
    add_custom_target(test_all
        COMMENT "Running all tests"
        DEPENDS test_dotnet test_integration
    )

    if(BUILD_VSCODE_EXTENSION)
        add_dependencies(test_all test_vscode_extension)
    endif()
endif()

# Clean target
add_custom_target(clean_all
    COMMAND ${DOTNET_EXECUTABLE} clean --configuration ${CMAKE_BUILD_TYPE}
    COMMAND ${CMAKE_COMMAND} -E remove_directory ${CMAKE_BINARY_DIR}/bin
    COMMAND ${CMAKE_COMMAND} -E remove_directory ${CMAKE_BINARY_DIR}/lib
    WORKING_DIRECTORY ${CMAKE_SOURCE_DIR}
    COMMENT "Cleaning all build artifacts"
    VERBATIM
)

# Documentation target
if(BUILD_DOCUMENTATION)
    find_program(DOXYGEN_EXECUTABLE doxygen)
    if(DOXYGEN_EXECUTABLE)
        add_custom_target(docs
            COMMAND ${DOXYGEN_EXECUTABLE} docs/Doxyfile
            WORKING_DIRECTORY ${CMAKE_SOURCE_DIR}
            COMMENT "Generating documentation with Doxygen"
            VERBATIM
        )
    else()
        message(WARNING "Doxygen not found. Documentation build will be skipped.")
    endif()
endif()

# Install targets
install(
    DIRECTORY ${CMAKE_RUNTIME_OUTPUT_DIRECTORY}/
    DESTINATION bin
    USE_SOURCE_PERMISSIONS
    PATTERN "*.pdb" EXCLUDE
)

# Package configuration
set(CPACK_PACKAGE_NAME "RazorForge")
set(CPACK_PACKAGE_VERSION ${PROJECT_VERSION})
set(CPACK_PACKAGE_DESCRIPTION_SUMMARY ${PROJECT_DESCRIPTION})
set(CPACK_PACKAGE_VENDOR "RazorForge Team")
set(CPACK_PACKAGE_CONTACT "support@razorforge.dev")
set(CPACK_RESOURCE_FILE_LICENSE "${CMAKE_SOURCE_DIR}/LICENSE")
set(CPACK_RESOURCE_FILE_README "${CMAKE_SOURCE_DIR}/README.md")

if(WIN32)
    set(CPACK_GENERATOR "NSIS;ZIP")
    set(CPACK_NSIS_ENABLE_UNINSTALL_BEFORE_INSTALL ON)
    set(CPACK_NSIS_MODIFY_PATH ON)
elseif(APPLE)
    set(CPACK_GENERATOR "DragNDrop;TGZ")
else()
    set(CPACK_GENERATOR "DEB;RPM;TGZ")
    set(CPACK_DEBIAN_PACKAGE_MAINTAINER ${CPACK_PACKAGE_CONTACT})
    set(CPACK_DEBIAN_PACKAGE_SECTION "devel")
    set(CPACK_RPM_PACKAGE_GROUP "Development/Languages")
endif()

include(CPack)

# Status summary
message(STATUS "")
message(STATUS "RazorForge Build Configuration Summary:")
message(STATUS "  Build Type:           ${CMAKE_BUILD_TYPE}")
message(STATUS "  Platform:             ${PLATFORM_NAME}")
message(STATUS "  Architecture:         ${ARCH_NAME}")
message(STATUS "  Native Runtime:       ${BUILD_NATIVE_RUNTIME}")
message(STATUS "  Math Libraries:       ${BUILD_MATH_LIBRARIES}")
message(STATUS "  Language Server:      ${BUILD_LANGUAGE_SERVER}")
message(STATUS "  VS Code Extension:    ${BUILD_VSCODE_EXTENSION}")
message(STATUS "  Tests:                ${BUILD_TESTS}")
message(STATUS "  Documentation:        ${BUILD_DOCUMENTATION}")
message(STATUS "  Link Time Opt:        ${ENABLE_LTO}")
message(STATUS "  Sanitizers:           ${ENABLE_SANITIZERS}")
message(STATUS "  .NET SDK:             ${DOTNET_VERSION}")
if(BUILD_VSCODE_EXTENSION AND NODEJS_FOUND)
    message(STATUS "  Node.js:              ${NODEJS_VERSION}")
    message(STATUS "  npm:                  ${NPM_VERSION}")
endif()
message(STATUS "")