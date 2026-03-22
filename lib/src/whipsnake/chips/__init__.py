# -----------------------------------------------------------------------------
# Whipsnake Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the Whipsnake Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------
#
# chips/__init__.py -- compile-time chip descriptor stub
#
# The compiler injects __CHIP__ as a compile-time constant that carries the
# selected chip's metadata (name, arch, ram_size).  HAL modules use it like:
#
#   from whipsnake.chips import __CHIP__
#
#   match __CHIP__.arch:
#       case "avr":
#           ...
#       case "pic14":
#           ...
#
# The `match` / `if` branches that do not match the target architecture are
# dead-code-eliminated by the ConditionalCompilator before code generation.
#
# This module defines a typed _ChipInfo stub so that IDEs (PyCharm, Pylance,
# Pyright, mypy) resolve __CHIP__.arch / .name / .ram_size without errors.
# The runtime singleton is never used; the compiler replaces every reference
# to __CHIP__ with the actual target values at compile time.


class _ChipInfo:
    """Compile-time chip descriptor.  Attributes are constant strings/ints
    resolved by the compiler before any code is generated.

    IDEs use this class to type-check HAL code that branches on __CHIP__.arch
    or __CHIP__.name.  The runtime instance is never accessed on real hardware.
    """

    # Human-readable chip identifier, e.g. "atmega328p", "pic16f877a".
    name: str = ""

    # Architecture family, e.g. "avr", "pic12", "pic14", "pic14e",
    # "pic18", "riscv", "pio".
    arch: str = ""

    # Total SRAM in bytes as reported by device_info().
    ram_size: int = 0


# Singleton used as a type anchor for `from whipsnake.chips import __CHIP__`.
# The compiler replaces every __CHIP__ reference; this object is only here
# so that static analysers see a well-typed value instead of NameError.
__CHIP__: _ChipInfo = _ChipInfo()
