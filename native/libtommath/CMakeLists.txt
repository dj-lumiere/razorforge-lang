# LibTomMath - Arbitrary precision integer arithmetic
# https://github.com/libtom/libtommath
# License: Public Domain (Unlicense)
#
# To use LibTomMath:
# 1. Clone the repository: git clone https://github.com/libtom/libtommath.git
# 2. Copy the sources here, or use USE_SYSTEM_LIBTOMMATH option
#
# LibTomMath provides:
# - Arbitrary precision integer arithmetic
# - Modular arithmetic, GCD, LCM
# - Prime number generation and testing
# - Used by: RazorForge's bigint type

# Check for LibTomMath source structure from GitHub
if(EXISTS "${CMAKE_CURRENT_SOURCE_DIR}/bn_mp_init.c" OR
   EXISTS "${CMAKE_CURRENT_SOURCE_DIR}/tommath.h")
    # LibTomMath sources detected
    message(STATUS "Found LibTomMath sources")

    # Collect all bn_*.c files (the main library sources)
    file(GLOB LIBTOMMATH_SOURCES "bn_*.c" "mp_*.c")

    # Also include tommath.c if it exists (single-file build)
    if(EXISTS "${CMAKE_CURRENT_SOURCE_DIR}/tommath.c")
        list(APPEND LIBTOMMATH_SOURCES "tommath.c")
    endif()

    if(LIBTOMMATH_SOURCES)
        add_library(tommath STATIC ${LIBTOMMATH_SOURCES})

        target_include_directories(tommath PUBLIC ${CMAKE_CURRENT_SOURCE_DIR})

        # Platform-specific configurations
        if(WIN32)
            target_compile_definitions(tommath PRIVATE _CRT_SECURE_NO_WARNINGS)
        endif()

        # Optimization for release builds
        if(CMAKE_BUILD_TYPE STREQUAL "Release")
            target_compile_definitions(tommath PRIVATE MP_NO_FILE)
        endif()

        set(HAVE_LIBTOMMATH TRUE PARENT_SCOPE)
    else()
        add_library(tommath INTERFACE)
        set(HAVE_LIBTOMMATH FALSE PARENT_SCOPE)
    endif()

else()
    # No LibTomMath sources found - create interface library
    add_library(tommath INTERFACE)
    set(HAVE_LIBTOMMATH FALSE PARENT_SCOPE)

    message(STATUS "")
    message(STATUS "LibTomMath not found. To enable arbitrary precision integers:")
    message(STATUS "  Option 1: Clone LibTomMath to native/libtommath/")
    message(STATUS "            git clone https://github.com/libtom/libtommath.git native/libtommath")
    message(STATUS "  Option 2: Install system libtommath and use -DUSE_SYSTEM_LIBTOMMATH=ON")
    message(STATUS "")
endif()
