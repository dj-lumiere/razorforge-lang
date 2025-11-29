cmake_minimum_required(VERSION 3.20)
project(RazorForgeNative C CXX)

set(CMAKE_C_STANDARD 11)
set(CMAKE_CXX_STANDARD 17)

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
include_directories(libdfp)
include_directories(libbf)
include_directories(mafm)

# Math libraries temporarily disabled until sources are added
# libdfp - IEEE 754 decimal floating point
add_subdirectory(libdfp)

# libbf - Arbitrary precision arithmetic
add_subdirectory(libbf)

# mafm - Multiple precision arithmetic
add_subdirectory(mafm)

# Runtime library for memory management and error handling
add_library(razorforge_runtime SHARED
    runtime/memory.c
    runtime/stacktrace.c
)

target_include_directories(razorforge_runtime PUBLIC include)

# Set output directory
set_target_properties(razorforge_runtime PROPERTIES
    RUNTIME_OUTPUT_DIRECTORY ${CMAKE_BINARY_DIR}/bin
    LIBRARY_OUTPUT_DIRECTORY ${CMAKE_BINARY_DIR}/lib
    ARCHIVE_OUTPUT_DIRECTORY ${CMAKE_BINARY_DIR}/lib
)

# Install targets
install(TARGETS razorforge_runtime
    RUNTIME DESTINATION bin
    LIBRARY DESTINATION lib
    ARCHIVE DESTINATION lib
)