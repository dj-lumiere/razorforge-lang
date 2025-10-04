# FindDotNet.cmake - Find .NET SDK
#
# This module finds the .NET SDK installation and provides the following variables:
#   DOTNET_FOUND        - True if .NET SDK is found
#   DOTNET_EXECUTABLE   - Path to dotnet executable
#   DOTNET_VERSION      - Version of .NET SDK
#   DOTNET_ROOT         - Root directory of .NET SDK

find_program(DOTNET_EXECUTABLE
    NAMES dotnet dotnet.exe
    HINTS
        $ENV{DOTNET_ROOT}
        $ENV{DOTNET_ROOT}/bin
        $ENV{PATH}
    PATHS
        /usr/local/bin
        /usr/bin
        /opt/dotnet
        "C:/Program Files/dotnet"
        "C:/Program Files (x86)/dotnet"
    DOC "Path to dotnet executable"
)

if(DOTNET_EXECUTABLE)
    # Get .NET version
    execute_process(
        COMMAND ${DOTNET_EXECUTABLE} --version
        OUTPUT_VARIABLE DOTNET_VERSION
        ERROR_QUIET
        OUTPUT_STRIP_TRAILING_WHITESPACE
    )

    # Get .NET root directory
    execute_process(
        COMMAND ${DOTNET_EXECUTABLE} --info
        OUTPUT_VARIABLE DOTNET_INFO
        ERROR_QUIET
        OUTPUT_STRIP_TRAILING_WHITESPACE
    )

    # Extract base path from --info output
    string(REGEX MATCH "Base Path:[^\r\n]*" DOTNET_BASE_PATH_LINE "${DOTNET_INFO}")
    if(DOTNET_BASE_PATH_LINE)
        string(REGEX REPLACE "Base Path:[ \t]*" "" DOTNET_ROOT "${DOTNET_BASE_PATH_LINE}")
        string(STRIP "${DOTNET_ROOT}" DOTNET_ROOT)
    else()
        get_filename_component(DOTNET_ROOT "${DOTNET_EXECUTABLE}" DIRECTORY)
    endif()

    # Check if version meets minimum requirements (9.0+)
    if(DOTNET_VERSION VERSION_GREATER_EQUAL "9.0")
        set(DOTNET_VERSION_OK TRUE)
    else()
        set(DOTNET_VERSION_OK FALSE)
        message(WARNING ".NET SDK version ${DOTNET_VERSION} found, but version 9.0+ is required")
    endif()
endif()

include(FindPackageHandleStandardArgs)
find_package_handle_standard_args(DotNet
    REQUIRED_VARS DOTNET_EXECUTABLE DOTNET_VERSION_OK
    VERSION_VAR DOTNET_VERSION
    HANDLE_COMPONENTS
)

mark_as_advanced(DOTNET_EXECUTABLE DOTNET_ROOT)