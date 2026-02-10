cmake_minimum_required(VERSION 4.1)
project(pymcu VERSION 0.1.0)

set(CMAKE_CXX_STANDARD 23)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

include(FetchContent)

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
set(INSTALL_GTEST OFF CACHE BOOL "" FORCE)
FetchContent_MakeAvailable(googletest)

add_library(common INTERFACE
        src/common/Errors.h
        src/common/Utils.h
        src/common/DeviceConfig.h
)
target_include_directories(common INTERFACE src/common)

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
        src/compiler/ir/Optimizer.cpp
        src/compiler/ir/Optimizer.h
        src/compiler/backend/analysis/StackAllocator.cpp
        src/compiler/backend/analysis/StackAllocator.h
        src/compiler/backend/targets/pic14/PIC14Peephole.cpp
        src/compiler/backend/targets/pic14/PIC14Peephole.h
        src/compiler/backend/targets/avr/AVRCodeGen.cpp
        src/compiler/backend/targets/avr/AVRCodeGen.h
        src/compiler/backend/targets/avr/AVRPeephole.cpp
        src/compiler/backend/targets/avr/AVRPeephole.h
)

target_link_libraries(core PUBLIC common)
target_include_directories(core PUBLIC src/compiler)

add_executable(pymcuc
        src/compiler/main.cpp
        src/compiler/Diagnostic.h
)
target_link_libraries(pymcuc PRIVATE core argparse::argparse)

set_target_properties(pymcuc PROPERTIES RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin")

set_target_properties(pymcuc PROPERTIES RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin")

if(SKBUILD)
    install(TARGETS pymcuc RUNTIME DESTINATION .)
endif()

# Testing Configuration
enable_testing()
add_executable(tests
        tests/unit/test_lexer.cpp
        tests/unit/test_parser.cpp
        tests/unit/test_utils.cpp
        tests/unit/test_ir_generator.cpp
        tests/unit/test_codegen.cpp
        tests/unit/test_optimizer.cpp
        tests/unit/test_arguments.cpp
        tests/unit/test_peephole.cpp
        tests/unit/test_optimizer_advanced.cpp
        tests/unit/test_avr_codegen.cpp
)
target_link_libraries(tests PRIVATE
        core
        GTest::gtest_main
)

include(GoogleTest)
gtest_discover_tests(tests)