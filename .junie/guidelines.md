### Project Overview
`pymcu` is a Python-to-MCU compiler targeting Microchip PIC microcontrollers (initially PIC16F84A). It consists of a frontend (Lexer, Parser) and a driver that coordinates the compilation process.

### Build/Configuration Instructions
The project uses CMake (minimum version 4.1) and requires a C++23 compatible compiler.

#### Prerequisites
- **CMake** 4.1 or higher.
- **C++ Compiler** with C++23 support (e.g., GCC 13+, Clang 16+, or latest MSVC).
- **Ninja** (optional, but recommended as a build generator).
- **gpasm** (GPUTILS) for assembling the generated assembly code.

#### Build Steps
1. **Configure CMake**:
   ```bash
   cmake -B build -G Ninja
   ```
2. **Build All Targets**:
   ```bash
   cmake --build build
   ```
   This will build:
   - `core`: The core compiler library.
   - `pymcuc`: The standalone compiler executable.
   - `pymcu`: The driver executable.
   - `tests`: The unit test suite.

The binaries will be located in the `build/bin` directory.

### Testing Information
The project uses **GoogleTest** for unit testing.

#### Running Tests
To run all unit tests:
```bash
./build/bin/tests
```
You can also use `ctest` from the build directory:
```bash
cd build && ctest
```

To run specific tests, use the `--gtest_filter` flag:
```bash
./build/bin/tests --gtest_filter="LexerTest.*"
```

#### Adding New Tests
1. Create a new `.cpp` file in `tests/unit/`.
2. Include `<gtest/gtest.h>` and relevant project headers.
3. Use the `TEST()` or `TEST_F()` macros.
4. Add your new test file to the `add_executable(tests ...)` section in `CMakeLists.txt`.

#### Example Test
```cpp
#include <gtest/gtest.h>
#include "frontend/Lexer.h"

TEST(SimpleTest, LexerInitialization) {
    Lexer lexer("def main(): pass");
    EXPECT_NO_THROW(lexer.tokenize());
}
```

### Additional Development Information
#### Code Style
- **Standard**: C++23.
- **Naming**: 
  - Classes/Structs: `PascalCase`.
  - Methods/Functions/Variables: `snake_case`.
  - Constants: `SCREAMING_SNAKE_CASE`.
- **Indentation**: 4 spaces.
- **Headers**: Use `#ifndef HEADER_H` guards.
- **Features**: 
  - Use `std::format` for string formatting.
  - Use `std::string_view` for efficient read-only string passing.
  - Prefer `static` for file-local constants and helper functions.

#### Project Structure
- `src/compiler`: Core compiler logic (frontend/backend).
- `src/driver`: Driver executable that orchestrates `pymcuc` and `gpasm`.
- `src/common`: Common utilities and error definitions.
- `tests`: GTest unit tests and fixtures.
- `config`: Configuration files for different targets (TOML).
