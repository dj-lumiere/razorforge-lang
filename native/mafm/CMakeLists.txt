# mafm - Multiple precision arithmetic library for decimals
# Place your mafm source files here

# Example library configuration - adjust based on actual mafm source structure
file(GLOB MAFM_SOURCES "*.c" "*.cpp")
file(GLOB MAFM_HEADERS "*.h" "*.hpp")

if(MAFM_SOURCES)
    add_library(mafm STATIC ${MAFM_SOURCES})
    
    target_include_directories(mafm PUBLIC .)
    
    # Platform-specific configurations
    if(WIN32)
        target_compile_definitions(mafm PRIVATE _CRT_SECURE_NO_WARNINGS)
    endif()
    
    # Export header to include directory
    configure_file(mafm.h ../include/mafm.h COPYONLY)
else()
    # Create dummy library if no sources found
    add_library(mafm INTERFACE)
    message(WARNING "No mafm sources found. Add your mafm source files to native/mafm/")
endif()