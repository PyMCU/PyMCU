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

#include "PIOPeephole.h"

#include <format>
#include <optional>

std::string PIOAsmLine::to_string() const {
  std::string s;
  switch (type) {
    case INSTRUCTION:
      s = "    " + mnemonic;
      if (!op1.empty()) {
        s += " " + op1;
        if (!op2.empty()) {
          s += ", " + op2;
          if (!op3.empty()) {
            s += ", " + op3;
          }
        }
      }
      if (delay > 0) {
        s += std::format(" [{}]", delay);
      }
      return s;
    case LABEL:
      return std::format("{}:", label);
    case COMMENT:
      return std::format("; {}", content);
    case RAW:
      return content;
    case EMPTY:
      return "";
  }
  return "";
}

std::vector<PIOAsmLine> PIOPeephole::optimize(
    const std::vector<PIOAsmLine> &lines) {
  std::vector<PIOAsmLine> result;
  std::optional<std::string> x_val;
  std::optional<std::string> y_val;

  for (const auto &line : lines) {
    if (line.type == PIOAsmLine::INSTRUCTION) {
      // Remove redundant MOV
      if (line.mnemonic == "MOV" && line.op1 == line.op2) {
        continue;
      }

      // Simple state tracking for SET
      if (line.mnemonic == "SET") {
        if (line.op1 == "X") {
          if (x_val && *x_val == line.op2) continue;
          x_val = line.op2;
        } else if (line.op1 == "Y") {
          if (y_val && *y_val == line.op2) continue;
          y_val = line.op2;
        }
      } else if (line.mnemonic == "MOV") {
        if (line.op1 == "X") {
          if (line.op2 == "Y" && x_val && y_val && *x_val == *y_val) continue;
          x_val.reset();  // Unknown now
        } else if (line.op1 == "Y") {
          if (line.op2 == "X" && x_val && y_val && *x_val == *y_val) continue;
          y_val.reset();
        }
      } else {
        // Any other instruction might change state
        x_val.reset();
        y_val.reset();
      }
    } else if (line.type == PIOAsmLine::LABEL) {
      x_val.reset();
      y_val.reset();
    }
    result.push_back(line);
  }

  return result;
}