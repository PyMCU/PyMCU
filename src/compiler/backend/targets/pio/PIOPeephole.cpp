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