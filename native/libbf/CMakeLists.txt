# libbf - Arbitrary precision arithmetic library
# Place your libbf source files here

# Example library configuration - adjust based on actual libbf source structure
file(GLOB LIBBF_SOURCES "*.c" "*.cpp")
file(GLOB LIBBF_HEADERS "*.h" "*.hpp")

if(LIBBF_SOURCES)
    add_library(bf STATIC ${LIBBF_SOURCES})
    
    target_include_directories(bf PUBLIC .)
    
    # Platform-specific configurations
    if(WIN32)
        target_compile_definitions(bf PRIVATE _CRT_SECURE_NO_WARNINGS)
    endif()
    
    # Export header to include directory
    configure_file(libbf.h ../include/libbf.h COPYONLY)
else()
    # Create dummy library if no sources found
    add_library(bf INTERFACE)
    message(WARNING "No libbf sources found. Add your libbf source files to native/libbf/")
endif()