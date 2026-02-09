cmake_minimum_required(VERSION 4.1)
project(pymcu VERSION 0.1.0)

set(CMAKE_CXX_STANDARD 23)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

include(FetchContent)

# Install TOML++
FetchContent_Declare(
        tomlplusplus
        GIT_REPOSITORY https://github.com/marzer/tomlplusplus.git
        GIT_TAG        master
)
FetchContent_MakeAvailable(tomlplusplus)

# Install ArgParse
FetchContent_Declare(
        argparse
        GIT_REPOSITORY https://github.com/p-ranav/argparse.git
        GIT_TAG        master
)
FetchContent_MakeAvailable(argparse)

# Install Google Test
FetchContent_Declare(
        googletest
        URL https://github.com/google/googletest/archive/refs/heads/main.zip
)
set(gtest_force_shared_crt ON CACHE BOOL "" FORCE)
FetchContent_MakeAvailable(googletest)

add_library(core STATIC
        src/compiler/frontend/Lexer.cpp
        tests/unit/test_lexer.cpp
        # src/compiler/frontend/Lexer.cpp
        # src/compiler/frontend/Parser.cpp
        # src/compiler/backend/CodeGen.cpp
)

target_include_directories(core PUBLIC
        src/compiler
        src/shared
)

add_library(pymcu_shared INTERFACE)
target_include_directories(pymcu_shared INTERFACE src/shared)

add_executable(pymcuc src/compiler/main.cpp)
target_link_libraries(pymcuc PRIVATE core pymcu_shared argparse::argparse)

add_executable(pymcu
        src/driver/main.cpp
        #src/driver/Toolchain.cpp
)
target_link_libraries(pymcu PRIVATE pymcu_shared tomlplusplus::tomlplusplus argparse::argparse)

set_target_properties(pymcu pymcuc PROPERTIES RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin")

# Testing Configuration
enable_testing()
add_executable(tests
        tests/unit/test_lexer.cpp
        tests/unit/test_parser.cpp
)
target_link_libraries(tests PRIVATE
        core
        GTest::gtest_main
)

include(GoogleTest)
gtest_discover_tests(tests)