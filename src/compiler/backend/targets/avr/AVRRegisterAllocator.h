/\*
 * Whipsnake Compiler (whipc)
 * Copyright (C) 2026 Ivan Montiel Cardona and the Whipsnake Project Authors
 *
 * SPDX-License-Identifier: MIT
 * Licensed under the MIT License. See LICENSE for details.
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
