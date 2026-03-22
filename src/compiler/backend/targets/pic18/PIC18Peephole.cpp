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

#include "PIC18Peephole.h"

#include <algorithm>
#include <format>
#include <optional>
#include <set>

std::string PIC18AsmLine::to_string() const {
  switch (type) {
    case INSTRUCTION:
      if (op3.empty()) {
        if (op2.empty()) {
          if (op1.empty()) return std::format("\t{}", mnemonic);
          return std::format("\t{}\t{}", mnemonic, op1);
        }
        return std::format("\t{}\t{}, {}", mnemonic, op1, op2);
      }
      return std::format("\t{}\t{}, {}, {}", mnemonic, op1, op2, op3);
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

std::vector<PIC18AsmLine> PIC18Peephole::optimize(
    const std::vector<PIC18AsmLine> &lines) {
  std::vector<PIC18AsmLine> source = lines;
  std::vector<PIC18AsmLine> result;
  bool changed = true;

  while (changed) {
    changed = false;
    result.clear();

    std::optional<int> current_bsr;
    bool dead_code_mode = false;

    for (size_t i = 0; i < source.size(); ++i) {
      const auto &current = source[i];

      if (current.type == PIC18AsmLine::LABEL) {
        dead_code_mode = false;
        current_bsr.reset();
        result.push_back(current);
        continue;
      }

      if (dead_code_mode) {
        if (current.type == PIC18AsmLine::INSTRUCTION) {
          changed = true;
          continue;
        }
      }

      if (current.type != PIC18AsmLine::INSTRUCTION) {
        result.push_back(current);
        continue;
      }

      if (current.mnemonic == "MOVFF" && current.op1 == current.op2) {
        changed = true;
        continue;
      }

      if (current.mnemonic == "MOVLB") {
        try {
          int bank = std::stoi(current.op1);
          if (current_bsr.has_value() && current_bsr.value() == bank) {
            changed = true;
            continue;
          }
          current_bsr = bank;
        } catch (...) {
          current_bsr.reset();
        }
      }

      if (current.mnemonic == "RETURN" || current.mnemonic == "RETFIE" ||
          current.mnemonic == "GOTO" || current.mnemonic == "BRA") {
        dead_code_mode = true;
      }

      result.push_back(current);
    }

    if (changed) {
      source = result;
    }
  }

  return result;
}