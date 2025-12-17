# MAPM - Mike's Arbitrary Precision Math Library
# https://github.com/LuaDist/mapm
# License: Freeware (see LICENSE in source)
#
# To use MAPM:
# 1. Clone the repository: git clone https://github.com/LuaDist/mapm.git
# 2. Copy the sources here
#
# MAPM provides:
# - Arbitrary precision decimal floating-point arithmetic
# - Trigonometric functions (sin, cos, tan, etc.)
# - Exponential and logarithmic functions
# - Square root, factorial, and more
# - Used by: RazorForge's decimal/bigdecimal types

# Check for MAPM source structure from GitHub
if(EXISTS "${CMAKE_CURRENT_SOURCE_DIR}/m_apm.h" OR
   EXISTS "${CMAKE_CURRENT_SOURCE_DIR}/mapm.h")
    # MAPM sources detected
    message(STATUS "Found MAPM sources")

    # Collect MAPM source files
    file(GLOB MAPM_SOURCES
        "mapm*.c"
        "m_apm*.c"
        "mapm_*.c"
    )

    # Alternative naming patterns
    if(NOT MAPM_SOURCES)
        file(GLOB MAPM_SOURCES "*.c")
        # Exclude test and example files
        list(FILTER MAPM_SOURCES EXCLUDE REGEX ".*test.*\\.c$")
        list(FILTER MAPM_SOURCES EXCLUDE REGEX ".*example.*\\.c$")
        list(FILTER MAPM_SOURCES EXCLUDE REGEX ".*demo.*\\.c$")
    endif()

    if(MAPM_SOURCES)
        add_library(mapm STATIC ${MAPM_SOURCES})

        target_include_directories(mapm PUBLIC ${CMAKE_CURRENT_SOURCE_DIR})

        # Platform-specific configurations
        if(WIN32)
            target_compile_definitions(mapm PRIVATE _CRT_SECURE_NO_WARNINGS)
        endif()

        # Link math library on Unix
        if(UNIX AND NOT APPLE)
            target_link_libraries(mapm m)
            target_compile_options(mapm PUBLIC -fPIC)
        endif()

        set(HAVE_MAPM TRUE PARENT_SCOPE)
    else()
        add_library(mapm INTERFACE)
        set(HAVE_MAPM FALSE PARENT_SCOPE)
    endif()

else()
    # No MAPM sources found - create interface library
    add_library(mapm INTERFACE)
    set(HAVE_MAPM FALSE PARENT_SCOPE)

    message(STATUS "")
    message(STATUS "MAPM not found. To enable arbitrary precision decimals:")
    message(STATUS "  Clone MAPM to native/mapm/")
    message(STATUS "  git clone https://github.com/LuaDist/mapm.git native/mapm")
    message(STATUS "")
endif()
