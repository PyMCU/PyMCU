# -----------------------------------------------------------------------------
# Whipsnake Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the Whipsnake Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------
#
# Whipsnake Foreign Function Interface
#
# Provides the @extern decorator for declaring C functions callable from
# Whipsnake firmware. The decorator is handled entirely by the compiler; this
# module exists so that IDEs and type-checkers can resolve the import without
# errors.
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
#   [tool.whip.ffi]
#   sources = ["src/c/mylib.c"]
#   include_dirs = ["src/c/include"]
#   cflags = ["-O2", "-std=c11"]
#
# The build driver (whip build) compiles those C files with avr-gcc and
# links the resulting ELF objects with the firmware via avr-ld.
#
# Note: [tool.whip.ffi] triggers automatic toolchain selection -- whip build
# switches to the avr-as / avr-ld pipeline when C sources are declared.

def extern(symbol: str):
    # @extern("symbol") is recognised syntactically by the compiler in
    # parseFunction(). This stub exists only so IDEs can resolve the import
    # and infer the correct return type of the decorated function.
    # The compiler never executes this code -- it reads the symbol name
    # from the AST directly.
    return lambda f: f
