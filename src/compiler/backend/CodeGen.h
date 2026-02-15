/*
 * Copyright (c) 2024 Begeistert and/or its affiliates.
 *
 * This file is part of PyMCU.
 *
 * PyMCU is free software: you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * PyMCU is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with PyMCU.  If not, see <https://www.gnu.org/licenses/>.
 */

#ifndef CODEGEN_H
#define CODEGEN_H

#pragma once
#include <iostream>

#include "../ir/Tacky.h"

class CodeGen {
 public:
  virtual ~CodeGen() = default;

  virtual void compile(const tacky::Program &program, std::ostream &os) = 0;

  // Interrupt Support
  virtual void emit_context_save() = 0;

  virtual void emit_context_restore() = 0;

  virtual void emit_interrupt_return() = 0;
};

#endif  // CODEGEN_H