# FindNodeJS.cmake - Find Node.js and npm
#
# This module finds Node.js and npm installations and provides the following variables:
#   NODEJS_FOUND        - True if Node.js is found
#   NODEJS_EXECUTABLE   - Path to node executable
#   NODEJS_VERSION      - Version of Node.js
#   NPM_FOUND           - True if npm is found
#   NPM_EXECUTABLE      - Path to npm executable
#   NPM_VERSION         - Version of npm

find_program(NODEJS_EXECUTABLE
    NAMES node node.exe
    HINTS
        $ENV{NODE_PATH}
        $ENV{NODE_PATH}/bin
        $ENV{PATH}
    PATHS
        /usr/local/bin
        /usr/bin
        /opt/node/bin
        "C:/Program Files/nodejs"
        "C:/Program Files (x86)/nodejs"
    DOC "Path to node executable"
)

find_program(NPM_EXECUTABLE
    NAMES npm npm.exe npm.cmd
    HINTS
        $ENV{NODE_PATH}
        $ENV{NODE_PATH}/bin
        $ENV{PATH}
    PATHS
        /usr/local/bin
        /usr/bin
        /opt/node/bin
        "C:/Program Files/nodejs"
        "C:/Program Files (x86)/nodejs"
    DOC "Path to npm executable"
)

if(NODEJS_EXECUTABLE)
    # Get Node.js version
    execute_process(
        COMMAND ${NODEJS_EXECUTABLE} --version
        OUTPUT_VARIABLE NODEJS_VERSION
        ERROR_QUIET
        OUTPUT_STRIP_TRAILING_WHITESPACE
    )

    # Remove 'v' prefix if present
    string(REGEX REPLACE "^v" "" NODEJS_VERSION "${NODEJS_VERSION}")

    # Check if version meets minimum requirements (16.0+)
    if(NODEJS_VERSION VERSION_GREATER_EQUAL "16.0")
        set(NODEJS_VERSION_OK TRUE)
    else()
        set(NODEJS_VERSION_OK FALSE)
        message(WARNING "Node.js version ${NODEJS_VERSION} found, but version 16.0+ is recommended")
        set(NODEJS_VERSION_OK TRUE) # Allow older versions with warning
    endif()
endif()

if(NPM_EXECUTABLE)
    # Get npm version
    execute_process(
        COMMAND ${NPM_EXECUTABLE} --version
        OUTPUT_VARIABLE NPM_VERSION
        ERROR_VARIABLE NPM_VERSION_ERROR
        RESULT_VARIABLE NPM_VERSION_RESULT
        OUTPUT_STRIP_TRAILING_WHITESPACE
    )

    if(NPM_VERSION_RESULT EQUAL 0 AND NPM_VERSION)
        # Check if version meets minimum requirements (8.0+)
        if(NPM_VERSION VERSION_GREATER_EQUAL "8.0")
            set(NPM_VERSION_OK TRUE)
        else()
            set(NPM_VERSION_OK FALSE)
            message(WARNING "npm version ${NPM_VERSION} found, but version 8.0+ is recommended")
            set(NPM_VERSION_OK TRUE) # Allow older versions with warning
        endif()
    else()
        set(NPM_VERSION "unknown")
        set(NPM_VERSION_OK FALSE)
        message(WARNING "Could not determine npm version: ${NPM_VERSION_ERROR}")
    endif()
endif()

include(FindPackageHandleStandardArgs)

# Find Node.js
find_package_handle_standard_args(NodeJS
    REQUIRED_VARS NODEJS_EXECUTABLE NODEJS_VERSION_OK
    VERSION_VAR NODEJS_VERSION
)

# Set NPM_FOUND manually to avoid the naming issue
if(NPM_EXECUTABLE AND NPM_VERSION_OK)
    set(NPM_FOUND TRUE)
else()
    set(NPM_FOUND FALSE)
endif()

# Set overall NodeJS found status
if(NODEJS_FOUND AND NPM_FOUND)
    set(NODEJS_FOUND TRUE)
else()
    set(NODEJS_FOUND FALSE)
endif()

mark_as_advanced(NODEJS_EXECUTABLE NPM_EXECUTABLE)