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