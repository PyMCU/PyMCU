/*
 * -----------------------------------------------------------------------------
 * PyMCU Compiler (pymcuc)
 * Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
 *
 * This file is part of the PyMCU Development Ecosystem.
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
 *
 * -----------------------------------------------------------------------------
 * SAFETY WARNING / HIGH RISK ACTIVITIES:
 * THE SOFTWARE IS NOT DESIGNED, MANUFACTURED, OR INTENDED FOR USE IN HAZARDOUS
 * ENVIRONMENTS REQUIRING FAIL-SAFE PERFORMANCE, SUCH AS IN THE OPERATION OF
 * NUCLEAR FACILITIES, AIRCRAFT NAVIGATION OR COMMUNICATION SYSTEMS, AIR
 * TRAFFIC CONTROL, DIRECT LIFE SUPPORT MACHINES, OR WEAPONS SYSTEMS.
 * -----------------------------------------------------------------------------
 */

#ifndef OPTIMIZER_H
#define OPTIMIZER_H

#include "Tacky.h"

class Optimizer {
 public:
  static tacky::Program optimize(const tacky::Program &program);

  static void fold_constants(tacky::Function &func);

  static void propagate_copies(tacky::Function &func);

  static void eliminate_dead_code(tacky::Function &func);

  static void coalesce_instructions(tacky::Function &func);

  // Bridges Binary(Equal/NE/LT/etc, a, b)→tmp followed by JumpIfZero/NotZero(tmp)
  // into a direct relational jump (JumpIfEqual/NotEqual/etc). This must run
  // before collapse_bit_checks so that BitCheck→Equal→JumpIfZero patterns become
  // BitCheck→JumpIfNotEqual, which collapse_bit_checks then folds to JumpIfBitSet.
  static void collapse_bool_jumps(tacky::Function &func);

  // Collapses BitCheck(src,bit)→tmp followed by JumpIfEqual/NotEqual(tmp,0/1,label)
  // into a single JumpIfBitSet/JumpIfBitClear instruction. This eliminates the
  // boolean materialization overhead and produces 3-instruction bit-poll loops
  // instead of 10-instruction ones (critical for UART/I2C wait loops).
  static void collapse_bit_checks(tacky::Function &func);

 private:
  static void optimize_function(tacky::Function &func);
};

#endif  // OPTIMIZER_H