# Intel Decimal Floating-Point Math Library (libbid)
# https://www.intel.com/content/www/us/en/developer/articles/tool/intel-decimal-floating-point-math-library.html
# License: BSD-3-Clause
#
# Provides IEEE 754-2008 decimal floating-point:
# - BID_UINT32 (decimal32), BID_UINT64 (decimal64), BID_UINT128 (decimal128)
# - All arithmetic, comparison, conversion functions
# - Cross-platform: Windows, Linux, macOS
#
# NOTE: The float128 emulation code (required for transcendental functions like sin, cos, exp, log)
# requires Intel's complex build system with generated table files.
# We only build the src/ directory which provides all basic arithmetic, comparisons, conversions.

set(INTELDECIMAL_SRC_DIR "${CMAKE_CURRENT_SOURCE_DIR}/LIBRARY/src")

if(EXISTS "${INTELDECIMAL_SRC_DIR}/bid_functions.h")
    message(STATUS "Found Intel Decimal Floating-Point Math Library")

    # Collect all source files from src directory (excludes float128 which needs special build)
    file(GLOB INTELDECIMAL_SOURCES "${INTELDECIMAL_SRC_DIR}/*.c")

    # Create the library with src sources only
    add_library(inteldecimal STATIC ${INTELDECIMAL_SOURCES})

    target_include_directories(inteldecimal PUBLIC
        ${INTELDECIMAL_SRC_DIR}
    )

    # Platform-specific definitions for src/ code
    if(WIN32)
        target_compile_definitions(inteldecimal PRIVATE
            _CRT_SECURE_NO_WARNINGS
            WINDOWS
        )
    elseif(UNIX AND NOT APPLE)
        target_compile_definitions(inteldecimal PRIVATE LINUX)
    elseif(APPLE)
        target_compile_definitions(inteldecimal PRIVATE MACH)
    endif()

    # Common definitions for building as a library
    # DECIMAL_CALL_BY_REFERENCE=0: Pass/return values directly (not by pointer)
    # DECIMAL_GLOBAL_ROUNDING=1: Use global rounding mode (simpler API)
    # DECIMAL_GLOBAL_EXCEPTION_FLAGS=1: Use global exception flags
    # USE_COMPILER_F128_TYPE=0: Disables native 128-bit float (we skip transcendentals for now)
    target_compile_definitions(inteldecimal PRIVATE
        DECIMAL_CALL_BY_REFERENCE=0
        DECIMAL_GLOBAL_ROUNDING=1
        DECIMAL_GLOBAL_EXCEPTION_FLAGS=1
        USE_COMPILER_F128_TYPE=0
        USE_COMPILER_F80_TYPE=0
    )

    # Compiler-specific settings
    if(CMAKE_C_COMPILER_ID STREQUAL "Clang")
        target_compile_options(inteldecimal PRIVATE
            -Wno-implicit-function-declaration
            -Wno-incompatible-pointer-types
            -Wno-parentheses
            -Wno-shift-op-parentheses
            -Wno-sometimes-uninitialized
            -Wno-unused-but-set-variable
            -Wno-unknown-pragmas
        )
    elseif(CMAKE_C_COMPILER_ID STREQUAL "GNU")
        target_compile_options(inteldecimal PRIVATE
            -Wno-implicit-function-declaration
            -Wno-incompatible-pointer-types
            -Wno-parentheses
            -Wno-unknown-pragmas
        )

      target_compile_options(inteldecimal PUBLIC
        -fPIC
        )

    elseif(MSVC)
        target_compile_options(inteldecimal PRIVATE /W0)
    endif()

    set(HAVE_INTELDECIMAL TRUE PARENT_SCOPE)
else()
    # No Intel Decimal sources found
    add_library(inteldecimal INTERFACE)
    set(HAVE_INTELDECIMAL FALSE PARENT_SCOPE)

    message(STATUS "")
    message(STATUS "Intel Decimal Floating-Point Math Library not found.")
    message(STATUS "  Download from: https://www.intel.com/content/www/us/en/developer/articles/tool/intel-decimal-floating-point-math-library.html")
    message(STATUS "  Extract to: native/inteldecimal/")
    message(STATUS "")
endif()
