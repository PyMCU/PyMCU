/*
 * PyMCU Compiler — AVR Register Allocator
 * Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
 *
 * Licensed under the GNU General Public License v3. See LICENSE for details.
 *
 * Greedy register allocator for named local variables on AVR.
 *
 * AVR has 32 general-purpose registers (R0-R31). The current codegen reserves:
 *   R16-R17 : ISR context save scratch
 *   R18-R19 : secondary operand pair
 *   R24-R25 : accumulator / return value pair
 *   R28-R29 : Y frame pointer
 *
 * This leaves R4-R15 (12 registers) available as variable storage, eliminating
 * the LDD/STD Y+offset round-trips for the most frequently accessed variables.
 *
 * Strategy:
 *   1. Count uses of each named Variable (not temporaries) across all IR
 *   2. Sort by use-count descending (most-used gets a register first)
 *   3. Assign variables greedily to R4..R15 (8-bit) or R4:R5..R14:R15 (16-bit)
 *   4. Return the assignment map — codegen uses it in load/store operations
 */

#ifndef AVR_REGISTER_ALLOCATOR_H
#define AVR_REGISTER_ALLOCATOR_H

#pragma once
#include <map>
#include <string>

#include "ir/Tacky.h"

class AVRRegisterAllocator {
 public:
  // Allocate named variables to AVR scratch registers R4-R15.
  // Returns: map<variable_name, base_register_name>
  //   8-bit  variable "x" → "R4"   (single register)
  //   16-bit variable "y" → "R4"   (R4 = low byte, R5 = high byte)
  // Variables not in the map are spilled to the Y-frame (stack_layout).
  static std::map<std::string, std::string> allocate(
      const tacky::Program &program);
};

#endif  // AVR_REGISTER_ALLOCATOR_H
