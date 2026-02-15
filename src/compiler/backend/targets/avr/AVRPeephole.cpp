#include "AVRPeephole.h"

#include <algorithm>
#include <format>
#include <optional>
#include <set>

std::string AVRAsmLine::to_string() const {
  switch (type) {
    case INSTRUCTION:
      if (op2.empty()) {
        if (op1.empty()) return std::format("\t{}", mnemonic);
        return std::format("\t{}\t{}", mnemonic, op1);
      }
      return std::format("\t{}\t{}, {}", mnemonic, op1, op2);
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

std::vector<AVRAsmLine> AVRPeephole::optimize(
    const std::vector<AVRAsmLine> &lines) {
  std::vector<AVRAsmLine> result = lines;
  bool changed = true;

  while (changed) {
    changed = false;
    std::vector<AVRAsmLine> next;

    // --- Dead Label Elimination ---
    std::set<std::string> used_labels;
    for (const auto &line : result) {
      if (line.type == AVRAsmLine::INSTRUCTION) {
        if (line.mnemonic == "RJMP" || line.mnemonic == "RCALL" ||
            line.mnemonic == "BREQ" || line.mnemonic == "BRNE" ||
            line.mnemonic == "BRLO" || line.mnemonic == "BRSH" ||
            line.mnemonic == "BRMI" || line.mnemonic == "BRPL") {
          used_labels.insert(line.op1);
        }
      }
    }
    used_labels.insert("main");
    used_labels.insert("__vector_default");  // Standard AVR interrupt entry

    std::optional<std::string> registers[32];  // Track R0-R31

    for (size_t i = 0; i < result.size(); ++i) {
      auto &current = result[i];

      if (current.type == AVRAsmLine::LABEL) {
        if (!used_labels.contains(current.label) &&
            (current.label.starts_with("L.") ||
             current.label.starts_with("L_"))) {
          changed = true;
          continue;
        }
        for (auto &r : registers) r.reset();
        next.push_back(current);
        continue;
      }

      if (current.type != AVRAsmLine::INSTRUCTION) {
        next.push_back(current);
        continue;
      }

      // --- LDI R, 0 -> CLR R ---
      if (current.mnemonic == "LDI" &&
          (current.op2 == "0" || current.op2 == "0x00")) {
        current.mnemonic = "CLR";
        current.op2 = "";
        changed = true;
      }

      // --- Redundant LDI ---
      if (current.mnemonic == "LDI") {
        try {
          int reg_idx = std::stoi(current.op1.substr(1));
          if (reg_idx >= 0 && reg_idx < 32) {
            if (registers[reg_idx] && *registers[reg_idx] == current.op2) {
              changed = true;
              continue;
            }
            registers[reg_idx] = current.op2;
          }
        } catch (...) {
          // Not a standard register name or some other error
        }
      } else if (current.mnemonic == "MOV") {
        try {
          int dst_idx = std::stoi(current.op1.substr(1));
          int src_idx = std::stoi(current.op2.substr(1));
          if (dst_idx >= 0 && dst_idx < 32 && src_idx >= 0 && src_idx < 32) {
            registers[dst_idx] = registers[src_idx];
          } else {
            if (dst_idx >= 0 && dst_idx < 32) registers[dst_idx].reset();
          }
        } catch (...) {
          for (auto &r : registers) r.reset();
        }
      } else if (current.mnemonic == "STS" || current.mnemonic == "OUT" ||
                 current.mnemonic == "CP" || current.mnemonic == "TST" ||
                 current.mnemonic == "STD" || current.mnemonic == "SBI" ||
                 current.mnemonic == "CBI" || current.mnemonic == "SBIS" ||
                 current.mnemonic == "SBIC") {
        // These don't modify general purpose registers
      } else if (current.mnemonic == "LDS" || current.mnemonic == "IN" ||
                 current.mnemonic == "LDD") {
        try {
          int reg_idx = std::stoi(current.op1.substr(1));
          if (reg_idx >= 0 && reg_idx < 32) registers[reg_idx].reset();
        } catch (...) {
          for (auto &r : registers) r.reset();
        }
      } else if (current.mnemonic == "CLR") {
        try {
          int reg_idx = std::stoi(current.op1.substr(1));
          if (reg_idx >= 0 && reg_idx < 32) registers[reg_idx] = "0";
        } catch (...) {
          for (auto &r : registers) r.reset();
        }
      } else if (current.mnemonic == "ADD" || current.mnemonic == "SUB" ||
                 current.mnemonic == "NEG" || current.mnemonic == "COM" ||
                 current.mnemonic == "ORI" || current.mnemonic == "ANDI") {
        try {
          int reg_idx = std::stoi(current.op1.substr(1));
          if (reg_idx >= 0 && reg_idx < 32) registers[reg_idx].reset();
        } catch (...) {
          for (auto &r : registers) r.reset();
        }
      } else if (current.mnemonic == "RJMP") {
        // --- RJMP to next instruction ---
        bool redundant = false;
        for (size_t j = i + 1; j < result.size(); ++j) {
          if (result[j].type == AVRAsmLine::LABEL) {
            if (result[j].label == current.op1) {
              redundant = true;
            }
            break;
          } else if (result[j].type == AVRAsmLine::COMMENT ||
                     result[j].type == AVRAsmLine::EMPTY) {
            continue;
          } else {
            break;
          }
        }
        if (redundant) {
          changed = true;
          continue;
        }
        next.push_back(current);
        // After RJMP, code is unreachable until next label
        while (i + 1 < result.size() &&
               result[i + 1].type != AVRAsmLine::LABEL) {
          if (result[i + 1].type == AVRAsmLine::INSTRUCTION) {
            changed = true;
          } else {
            next.push_back(result[i + 1]);
          }
          i++;
        }
        for (auto &r : registers) r.reset();
        continue;
      } else {
        // For most instructions, we reset tracking for safety
        // In a more advanced implementation, we'd know which registers are
        // modified
        for (auto &r : registers) r.reset();
      }

      next.push_back(current);
    }
    result = next;
  }

  return result;
}