/*
 * -----------------------------------------------------------------------------
 * PyMCU Compiler (pymcuc)
 * Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
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

#include "RISCVPeephole.h"

#include <algorithm>
#include <format>
#include <optional>
#include <set>

std::vector<RISCVAsmLine> RISCVPeephole::optimize(
    const std::vector<RISCVAsmLine> &lines) {
  std::vector<RISCVAsmLine> result = lines;
  bool changed = true;

  while (changed) {
    changed = false;
    std::vector<RISCVAsmLine> next;

    std::optional<std::string> t0_val;
    std::optional<std::string> t1_val;
    std::optional<std::string> a0_val;

    for (size_t i = 0; i < result.size(); ++i) {
      auto &current = result[i];

      if (current.type == RISCVAsmLine::LABEL) {
        t0_val.reset();
        t1_val.reset();
        a0_val.reset();
        next.push_back(current);
        continue;
      }
      if (current.type != RISCVAsmLine::INSTRUCTION) {
        next.push_back(current);
        continue;
      }

      // Redundant li optimization
      if (current.mnemonic == "li") {
        if (current.op1 == "t0" && t0_val && *t0_val == current.op2) {
          changed = true;
          continue;
        }
        if (current.op1 == "t1" && t1_val && *t1_val == current.op2) {
          changed = true;
          continue;
        }
        if (current.op1 == "a0" && a0_val && *a0_val == current.op2) {
          changed = true;
          continue;
        }

        if (current.op1 == "t0")
          t0_val = current.op2;
        else if (current.op1 == "t1")
          t1_val = current.op2;
        else if (current.op1 == "a0")
          a0_val = current.op2;
      } else if (current.mnemonic == "lw" || current.mnemonic == "sw" ||
                 current.mnemonic == "addi") {
        // These might change registers, for simplicity we reset if they target
        // our tracked regs
        if (current.op1 == "t0") t0_val.reset();
        if (current.op1 == "t1") t1_val.reset();
        if (current.op1 == "a0") a0_val.reset();
      } else if (current.mnemonic == "call" || current.mnemonic == "j" ||
                 current.mnemonic == "ret") {
        t0_val.reset();
        t1_val.reset();
        a0_val.reset();
      } else {
        // Any other instruction that might write to registers
        if (current.op1 == "t0") t0_val.reset();
        if (current.op1 == "t1") t1_val.reset();
        if (current.op1 == "a0") a0_val.reset();
      }

      next.push_back(current);
    }

    if (result.size() == next.size() && !changed) break;
    result = next;
  }

  return result;
}