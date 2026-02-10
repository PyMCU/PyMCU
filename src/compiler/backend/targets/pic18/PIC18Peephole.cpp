#include "PIC18Peephole.h"
#include <format>
#include <algorithm>
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

std::vector<PIC18AsmLine> PIC18Peephole::optimize(const std::vector<PIC18AsmLine>& lines) {
    std::vector<PIC18AsmLine> result = lines;
    bool changed = true;

    while (changed) {
        changed = false;
        std::vector<PIC18AsmLine> next;
        
        // Track state
        std::optional<std::string> w_lit;
        std::optional<std::string> w_var;
        std::optional<int> current_bsr;

        for (size_t i = 0; i < result.size(); ++i) {
            auto& current = result[i];

            if (current.type == PIC18AsmLine::LABEL) {
                w_lit.reset(); w_var.reset();
                current_bsr.reset();
                next.push_back(current);
                continue;
            }
            if (current.type != PIC18AsmLine::INSTRUCTION) {
                next.push_back(current);
                continue;
            }

            // Redundant MOVFF optimization
            if (current.mnemonic == "MOVFF" && current.op1 == current.op2) {
                changed = true;
                continue;
            }

            // Redundant MOVLB (Bank Select)
            if (current.mnemonic == "MOVLB") {
                try {
                    int bank = std::stoi(current.op1);
                    if (current_bsr && *current_bsr == bank) {
                        changed = true;
                        continue;
                    }
                    current_bsr = bank;
                } catch (...) {
                    current_bsr.reset();
                }
            }

            // State tracking for WREG
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
            } else if (current.mnemonic == "BRA" || current.mnemonic == "GOTO" || current.mnemonic == "CALL" || current.mnemonic == "RETURN") {
                w_lit.reset(); w_var.reset();
                current_bsr.reset();
            } else {
                // Most other instructions affect flags and potentially WREG
                // For simplicity, we reset on unknown instructions
                if (current.mnemonic != "BSF" && current.mnemonic != "BCF" && current.mnemonic != "BTG") {
                    w_lit.reset();
                    w_var.reset();
                }
            }

            next.push_back(current);
        }
        
        if (result.size() == next.size() && !changed) break;
        result = next;
    }

    return result;
}
