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
        src/compiler/backend/CodeGen.h
        src/compiler/backend/CodeGenFactory.h
        src/compiler/backend/VirtualStack.h
        src/compiler/backend/targets/pic14/PIC14CodeGen.cpp
        src/compiler/backend/targets/pic14/PIC14CodeGen.h
        src/compiler/frontend/Lexer.cpp
        src/compiler/frontend/Token.h
        src/compiler/frontend/Ast.h
        src/compiler/frontend/Parser.cpp
        src/compiler/frontend/Parser.h
        src/compiler/frontend/Lexer.h
        src/compiler/ir/Tacky.h
        src/compiler/ir/IRGenerator.cpp
        src/compiler/ir/IRGenerator.h
)

target_include_directories(core PUBLIC
        src/compiler
        src/common
)

add_library(common INTERFACE
        src/common/Errors.h
        src/common/Utils.h
)
target_include_directories(common INTERFACE src/common)

add_executable(pymcuc
        src/compiler/main.cpp
        src/compiler/Diagnostic.h
)
target_link_libraries(pymcuc PRIVATE core common argparse::argparse)

add_executable(pymcu
        src/driver/main.cpp
        #src/driver/Toolchain.cpp
)
target_link_libraries(pymcu PRIVATE common tomlplusplus::tomlplusplus argparse::argparse)

set_target_properties(pymcu pymcuc PROPERTIES RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin")

# Testing Configuration
enable_testing()
add_executable(tests
        tests/unit/test_lexer.cpp
        tests/unit/test_parser.cpp
        tests/unit/test_utils.cpp
        tests/unit/test_ir_generator.cpp
        tests/unit/test_codegen.cpp
)
target_link_libraries(tests PRIVATE
        core
        GTest::gtest_main
)

include(GoogleTest)
gtest_discover_tests(tests)