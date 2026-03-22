/*
 * -----------------------------------------------------------------------------
 * Whisnake Compiler (whipc)
 * Copyright (C) 2026 Ivan Montiel Cardona and the Whisnake Project Authors
 *
 * SPDX-License-Identifier: MIT
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction, including without limitation the rights
 * to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
 * copies of the Software, and to permit persons to whom the Software is
 * furnished to do so, subject to the following conditions:
 *
 * The above copyright notice and this permission notice shall be included in
 * all copies or substantial portions of the Software.
 *
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
 * IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
 * FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
 * AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
 * LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
 * OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
 * THE SOFTWARE.
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