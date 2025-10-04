# libdfp - IEEE 754 decimal floating point library
# Place your libdfp source files here

# Example library configuration - adjust based on actual libdfp source structure
file(GLOB LIBDFP_SOURCES "*.c" "*.cpp")
file(GLOB LIBDFP_HEADERS "*.h" "*.hpp")

if(LIBDFP_SOURCES)
    add_library(dfp STATIC ${LIBDFP_SOURCES})
    
    target_include_directories(dfp PUBLIC .)
    
    # Platform-specific configurations
    if(WIN32)
        target_compile_definitions(dfp PRIVATE _CRT_SECURE_NO_WARNINGS)
    endif()
    
    # Export header to include directory
    configure_file(dfp.h ../include/dfp.h COPYONLY)
else()
    # Create dummy library if no sources found
    add_library(dfp INTERFACE)
    message(WARNING "No libdfp sources found. Add your libdfp source files to native/libdfp/")
endif()