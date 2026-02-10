#include "PIC14Peephole.h"
#include <format>
#include <algorithm>
#include <optional>

std::string PIC14AsmLine::to_string() const {
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

std::vector<PIC14AsmLine> PIC14Peephole::optimize(const std::vector<PIC14AsmLine>& lines) {
    std::vector<PIC14AsmLine> result = lines;
    bool changed = true;

    while (changed) {
        changed = false;
        std::vector<PIC14AsmLine> next;
        std::optional<std::string> w_lit;
        std::optional<std::string> w_var;
        std::optional<bool> rp0;
        std::optional<bool> rp1;

        for (size_t i = 0; i < result.size(); ++i) {
            auto& current = result[i];

            if (current.type == PIC14AsmLine::LABEL) {
                w_lit.reset(); w_var.reset();
                rp0.reset(); rp1.reset();
                next.push_back(current);
                continue;
            }
            if (current.type != PIC14AsmLine::INSTRUCTION) {
                next.push_back(current);
                continue;
            }

            // --- Bank tracking ---
            if (current.mnemonic == "BSF" && current.op1 == "STATUS" && current.op2 == "5") {
                if (rp0 && *rp0 == true) { changed = true; continue; }
                rp0 = true;
            } else if (current.mnemonic == "BCF" && current.op1 == "STATUS" && current.op2 == "5") {
                if (rp0 && *rp0 == false) { changed = true; continue; }
                rp0 = false;
            } else if (current.mnemonic == "BSF" && current.op1 == "STATUS" && current.op2 == "6") {
                if (rp1 && *rp1 == true) { changed = true; continue; }
                rp1 = true;
            } else if (current.mnemonic == "BCF" && current.op1 == "STATUS" && current.op2 == "6") {
                if (rp1 && *rp1 == false) { changed = true; continue; }
                rp1 = false;
            }

            // --- State tracking optimizations ---

            if (current.mnemonic == "MOVLW") {
                if (w_lit && *w_lit == current.op1) {
                    changed = true;
                    continue; // Skip redundant MOVLW
                }
                w_lit = current.op1;
                w_var.reset();
            } else if (current.mnemonic == "MOVF" && current.op2 == "W") {
                if (w_var && *w_var == current.op1) {
                    changed = true;
                    continue; // Skip redundant MOVF
                }
                w_var = current.op1;
                w_lit.reset();
            } else if (current.mnemonic == "MOVWF") {
                w_var = current.op1; 
                // w_lit remains valid!
            } else if (current.mnemonic == "CLRF") {
                if (w_lit && (*w_lit == "0" || *w_lit == "0x00")) {
                    w_var = current.op1;
                } else {
                    // We don't know what's in x now relative to W, 
                    // unless we track all variables.
                }
            } else if (current.mnemonic == "IORLW" && current.op1 == "0") {
                bool redundant = false;
                for (int j = (int)next.size() - 1; j >= 0; --j) {
                    if (next[j].type == PIC14AsmLine::LABEL) break;
                    if (next[j].type == PIC14AsmLine::INSTRUCTION) {
                        if (next[j].mnemonic == "MOVF") redundant = true;
                        break;
                    }
                }
                if (redundant) {
                    changed = true;
                    continue;
                }
            } else if (current.mnemonic == "GOTO") {
                bool redundant = false;
                for (size_t j = i + 1; j < result.size(); ++j) {
                    if (result[j].type == PIC14AsmLine::LABEL) {
                        if (result[j].label == current.op1) {
                            redundant = true;
                            break;
                        }
                        // Continue looking through other labels
                    } else if (result[j].type == PIC14AsmLine::COMMENT || result[j].type == PIC14AsmLine::EMPTY) {
                        continue;
                    } else {
                        break;
                    }
                }
                if (redundant) {
                    changed = true;
                    continue;
                }
                w_lit.reset(); w_var.reset();
                rp0.reset(); rp1.reset();
            } else if (current.mnemonic == "CALL" || current.mnemonic == "RETURN" || current.mnemonic == "RETFIE") {
                w_lit.reset(); w_var.reset();
                rp0.reset(); rp1.reset();
            } else {
                // Arithmetic instructions typically change W and flags
                w_lit.reset(); w_var.reset();
            }

            next.push_back(current);
        }
        result = next;
    }

    return result;
}
