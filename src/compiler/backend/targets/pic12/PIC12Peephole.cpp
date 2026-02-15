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