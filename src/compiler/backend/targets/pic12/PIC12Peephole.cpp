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

#include "PIC12Peephole.h"

#include <optional>
#include <string>

std::vector<PIC12AsmLine> PIC12Peephole::optimize(
    const std::vector<PIC12AsmLine> &lines) {
  std::vector<PIC12AsmLine> result = lines;
  bool changed = true;

  while (changed) {
    changed = false;
    std::vector<PIC12AsmLine> next;

    std::optional<std::string> w_lit;
    std::optional<std::string> w_var;

    for (size_t i = 0; i < result.size(); ++i) {
      auto current = result[i];

      if (current.type == PIC12AsmLine::LABEL) {
        w_lit.reset();
        w_var.reset();
        next.push_back(current);
        continue;
      }

      if (current.type != PIC12AsmLine::INSTRUCTION) {
        next.push_back(current);
        continue;
      }

      // Redundant MOVLW
      if (current.mnemonic == "MOVLW") {
        if (w_lit && *w_lit == current.op1) {
          changed = true;
          continue;
        }
        w_lit = current.op1;
        w_var.reset();
      } else if (current.mnemonic == "MOVF" && current.op2 == "W") {
        if (w_var && *w_var == current.op1) {
          changed = true;
          continue;
        }
        w_var = current.op1;
        w_lit.reset();
      } else if (current.mnemonic == "MOVWF") {
        if (w_var && *w_var == current.op1) {
          changed = true;
          continue;
        }
        w_var = current.op1;
      } else if (current.mnemonic == "GOTO" || current.mnemonic == "CALL" ||
                 current.mnemonic == "RETLW") {
        w_lit.reset();
        w_var.reset();
      } else {
        // Unknown state change
        w_lit.reset();
        w_var.reset();
      }

      next.push_back(current);
    }

    if (changed) result = next;
  }

  return result;
}