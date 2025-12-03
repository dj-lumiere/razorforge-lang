cmake_minimum_required(VERSION 3.20)
project(RazorForgeNative C CXX)

set(CMAKE_C_STANDARD 11)
set(CMAKE_CXX_STANDARD 17)

# Export all symbols on Windows (generates .lib import library)
set(CMAKE_WINDOWS_EXPORT_ALL_SYMBOLS ON)

# Options
option(ENABLE_INTELDECIMAL "Enable IEEE 754 decimal floating point support via Intel DFP" ON)

# Platform-specific library suffix
if(WIN32)
    set(LIB_SUFFIX ".dll")
    set(LIB_PREFIX "")
elseif(APPLE)
    set(LIB_SUFFIX ".dylib")
    set(LIB_PREFIX "lib")
else()
    set(LIB_SUFFIX ".so")
    set(LIB_PREFIX "lib")
endif()

# Include directories
include_directories(include)
include_directories(libtommath)
include_directories(mapm)

# ============================================================================
# Intel Decimal Floating-Point Math Library (libbid)
# IEEE 754-2008 decimal floating point (cross-platform)
# ============================================================================
add_subdirectory(inteldecimal)

# ============================================================================
# LibTomMath - Arbitrary precision integer arithmetic
# https://github.com/libtom/libtommath (Public Domain)
# ============================================================================
add_subdirectory(libtommath)

# ============================================================================
# MAPM - Arbitrary precision decimal arithmetic
# https://github.com/LuaDist/mapm (Freeware)
# ============================================================================
add_subdirectory(mapm)

# ============================================================================
# Runtime library for memory management, error handling, and math
# ============================================================================
add_library(razorforge_runtime SHARED
    runtime/runtime.c
    runtime/memory.c
    runtime/stacktrace.c
    runtime/math_functions.c
    runtime/decimal_functions.c
    runtime/bignum_functions.c
    runtime/f16_functions.c
    runtime/types.h
)

target_include_directories(razorforge_runtime PUBLIC include)

# Link math library
if(UNIX AND NOT APPLE)
    target_link_libraries(razorforge_runtime m)
endif()

# Link Intel Decimal if available
if(HAVE_INTELDECIMAL)
    target_compile_definitions(razorforge_runtime PRIVATE
        HAVE_INTELDECIMAL
        DECIMAL_CALL_BY_REFERENCE=0
        DECIMAL_GLOBAL_ROUNDING=1
        DECIMAL_GLOBAL_EXCEPTION_FLAGS=1
    )
    target_link_libraries(razorforge_runtime inteldecimal)
endif()

# Link LibTomMath if available
if(HAVE_LIBTOMMATH)
    target_compile_definitions(razorforge_runtime PRIVATE HAVE_LIBTOMMATH)
    target_link_libraries(razorforge_runtime tommath)
endif()

# Link MAPM if available
if(HAVE_MAPM)
    target_compile_definitions(razorforge_runtime PRIVATE HAVE_MAPM)
    target_link_libraries(razorforge_runtime mapm)
endif()

# Set output directory
set_target_properties(razorforge_runtime PROPERTIES
    RUNTIME_OUTPUT_DIRECTORY ${CMAKE_BINARY_DIR}/bin
    LIBRARY_OUTPUT_DIRECTORY ${CMAKE_BINARY_DIR}/lib
    ARCHIVE_OUTPUT_DIRECTORY ${CMAKE_BINARY_DIR}/lib
)

# ============================================================================
# Math library (standalone, for static linking)
# ============================================================================
add_library(razorforge_math STATIC
    runtime/math_functions.c
    runtime/decimal_functions.c
    runtime/bignum_functions.c
    runtime/f16_functions.c
)

target_include_directories(razorforge_math PUBLIC include)

if(UNIX AND NOT APPLE)
    target_link_libraries(razorforge_math m)
endif()

if(HAVE_INTELDECIMAL)
    target_compile_definitions(razorforge_math PRIVATE
        HAVE_INTELDECIMAL
        DECIMAL_CALL_BY_REFERENCE=0
        DECIMAL_GLOBAL_ROUNDING=1
        DECIMAL_GLOBAL_EXCEPTION_FLAGS=1
    )
    target_link_libraries(razorforge_math inteldecimal)
endif()

if(HAVE_LIBTOMMATH)
    target_compile_definitions(razorforge_math PRIVATE HAVE_LIBTOMMATH)
    target_link_libraries(razorforge_math tommath)
endif()

if(HAVE_MAPM)
    target_compile_definitions(razorforge_math PRIVATE HAVE_MAPM)
    target_link_libraries(razorforge_math mapm)
endif()

set_target_properties(razorforge_math PROPERTIES
    ARCHIVE_OUTPUT_DIRECTORY ${CMAKE_BINARY_DIR}/lib
)

# ============================================================================
# Install targets
# ============================================================================
install(TARGETS razorforge_runtime razorforge_math
    RUNTIME DESTINATION bin
    LIBRARY DESTINATION lib
    ARCHIVE DESTINATION lib
)

install(FILES include/razorforge_math.h
    DESTINATION include
)

# Print configuration summary
message(STATUS "")
message(STATUS "RazorForge Native Configuration:")
message(STATUS "  Intel Decimal (IEEE 754): ${HAVE_INTELDECIMAL}")
message(STATUS "  LibTomMath (bigint):      ${HAVE_LIBTOMMATH}")
message(STATUS "  MAPM (bigdecimal):        ${HAVE_MAPM}")
message(STATUS "  Platform: ${CMAKE_SYSTEM_NAME}")
message(STATUS "")

#target_compile_options(razorforge PUBLIC
#  -fPIC
#  )

