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