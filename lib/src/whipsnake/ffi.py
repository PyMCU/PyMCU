# PyMCU Foreign Function Interface
#
# Provides the @extern decorator for declaring C functions callable from
# PyMCU firmware. The decorator is handled entirely by the compiler; this
# module exists so that IDEs and type-checkers can resolve the import.
#
# Usage:
#
#   from whipsnake.ffi import extern
#   from whipsnake.types import uint8, uint16
#
#   # Declare an external C function -- body is ignored by the compiler.
#   @extern("my_c_function")
#   def my_c_function(a: uint8, b: uint16) -> uint8:
#       pass  # body is ignored by the compiler; pass or return 0 both work
#
#   # Call it like any other function.
#   result: uint8 = my_c_function(10, 1000)
#
# The C source files containing the implementation are listed in pyproject.toml:
#
#   [tool.whipsnake.ffi]
#   sources = ["src/c/mylib.c"]
#   include_dirs = ["src/c/include"]
#   cflags = ["-O2", "-std=c11"]
#
# The build driver (pymcu build) compiles those C files with avr-gcc and
# links the resulting ELF objects with the firmware via avr-ld.
#
# Requires: [tool.whipsnake.assembler] = "avr-as"  (or automatic detection when
# [tool.whipsnake.ffi] is present -- the build driver switches to the avr-as
# toolchain automatically when C sources are declared).

# @extern("symbol") is recognized syntactically by the compiler in
# parseFunction(). This stub exists so 'from whipsnake.ffi import extern'
# resolves without error. The compiler never calls this function at
# compile time -- it only reads its name from the import.
def extern(symbol):
    return symbol
