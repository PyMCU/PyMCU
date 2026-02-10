#include "PIC14Peephole.h"
#include <format>
#include <algorithm>
#include <optional>
#include <set>

static std::vector<PIC14AsmLine> coalesce_bit_ops(const std::vector<PIC14AsmLine>& lines) {
    std::vector<PIC14AsmLine> result;
    for (size_t i = 0; i < lines.size(); ++i) {
        if (lines[i].type == PIC14AsmLine::INSTRUCTION &&
            (lines[i].mnemonic == "BSF" || lines[i].mnemonic == "BCF")) {

            std::string reg = lines[i].op1;
            std::string mnemonic = lines[i].mnemonic;

            if (reg == "STATUS") {
                result.push_back(lines[i]);
                continue;
            }

            std::vector<int> bits;
            size_t j = i;
            while (j < lines.size()) {
                if (lines[j].type == PIC14AsmLine::INSTRUCTION) {
                    if (lines[j].mnemonic == mnemonic && lines[j].op1 == reg) {
                        try {
                            bits.push_back(std::stoi(lines[j].op2));
                            j++;
                        } catch (...) { break; }
                    } else {
                        break;
                    }
                } else if (lines[j].type == PIC14AsmLine::COMMENT || lines[j].type == PIC14AsmLine::EMPTY) {
                    j++;
                } else {
                    break;
                }
            }

            if (bits.size() >= 3) {
                int mask = 0;
                for (int b : bits) mask |= (1 << b);

                if (mnemonic == "BSF") {
                    result.push_back(PIC14AsmLine::Instruction("MOVLW", std::format("0x{:02X}", mask)));
                    result.push_back(PIC14AsmLine::Instruction("IORWF", reg, "F"));
                } else {
                    result.push_back(PIC14AsmLine::Instruction("MOVLW", std::format("0x{:02X}", (unsigned char)~mask)));
                    result.push_back(PIC14AsmLine::Instruction("ANDWF", reg, "F"));
                }
                i = j - 1;
            } else {
                result.push_back(lines[i]);
            }
        } else {
            result.push_back(lines[i]);
        }
    }
    return result;
}

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
    std::vector<PIC14AsmLine> result = coalesce_bit_ops(lines);
    bool changed = true;

    while (changed) {
        changed = false;
        
        // --- Dead Label Elimination ---
        std::set<std::string> used_labels;
        for (const auto& line : result) {
            if (line.type == PIC14AsmLine::INSTRUCTION) {
                if (line.mnemonic == "GOTO" || line.mnemonic == "CALL") {
                    used_labels.insert(line.op1);
                }
            }
        }
        // Entry points and special labels
        used_labels.insert("main");
        used_labels.insert("__interrupt");

        std::vector<PIC14AsmLine> next;
        std::optional<std::string> w_lit;
        std::optional<std::string> w_var;
        std::optional<bool> rp0;
        std::optional<bool> rp1;

        for (size_t i = 0; i < result.size(); ++i) {
            auto& current = result[i];

            if (current.type == PIC14AsmLine::LABEL) {
                if (!used_labels.contains(current.label) && 
                    (current.label.starts_with("L.") || current.label.starts_with("L_"))) {
                    changed = true;
                    continue;
                }
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
                if (w_var && *w_var == current.op1) {
                    changed = true;
                    continue; // Redundant store
                }
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
                        const std::string& m = next[j].mnemonic;
                        if (m == "MOVF" || m == "ADDWF" || m == "SUBWF" || 
                            m == "ANDWF" || m == "IORWF" || m == "XORWF" ||
                            m == "INCF" || m == "DECF" || m == "ADDLW" ||
                            m == "SUBLW" || m == "ANDLW" || m == "XORLW" ||
                            m == "CLRF" || m == "CLRW") {
                            redundant = true;
                        }
                        break;
                    }
                }
                if (redundant || (w_lit && (*w_lit == "0" || *w_lit == "0x00"))) {
                    changed = true;
                    continue;
                }
            } else if (current.mnemonic == "ADDLW" && (current.op1 == "0" || current.op1 == "0x00")) {
                changed = true;
                continue;
            } else if (current.mnemonic == "XORLW" && (current.op1 == "0" || current.op1 == "0x00")) {
                changed = true;
                continue;
            } else if (current.mnemonic == "ANDLW" && (current.op1 == "255" || current.op1 == "0xFF")) {
                changed = true;
                continue;
            } else if (current.mnemonic == "BSF" || current.mnemonic == "BCF") {
                // Bit coalescing/redundancy
                bool redundant = false;
                for (int j = (int)next.size() - 1; j >= 0; --j) {
                    if (next[j].type == PIC14AsmLine::LABEL) break;
                    if (next[j].type == PIC14AsmLine::INSTRUCTION) {
                        if (next[j].op1 == current.op1 && next[j].op2 == current.op2) {
                            if (next[j].mnemonic == "BSF" || next[j].mnemonic == "BCF") {
                                if (next[j].mnemonic == current.mnemonic) {
                                    redundant = true;
                                }
                            }
                        }
                        break;
                    }
                }
                if (redundant) {
                    changed = true;
                    continue;
                }

                if (!next.empty() && next.back().type == PIC14AsmLine::INSTRUCTION &&
                    next.back().op1 == current.op1 && next.back().op2 == current.op2 &&
                    (next.back().mnemonic == "BSF" || next.back().mnemonic == "BCF")) {
                    if (next.back().mnemonic != current.mnemonic) {
                        next.pop_back();
                        changed = true;
                    }
                }
            } else if (current.mnemonic == "GOTO") {
                bool redundant = false;
                for (size_t j = i + 1; j < result.size(); ++j) {
                    if (result[j].type == PIC14AsmLine::LABEL) {
                        if (result[j].label == current.op1) {
                            redundant = true;
                            break;
                        }
                    } else if (result[j].type == PIC14AsmLine::COMMENT || result[j].type == PIC14AsmLine::EMPTY) {
                        continue;
                    } else {
                        break;
                    }
                }

                bool preceded_by_skip = false;
                if (!next.empty() && next.back().type == PIC14AsmLine::INSTRUCTION) {
                    const std::string& prev = next.back().mnemonic;
                    if (prev == "BTFSC" || prev == "BTFSS" || prev == "DECFSZ" || prev == "INCFSZ") {
                        preceded_by_skip = true;
                    }
                }

                if (redundant) {
                    changed = true;
                    if (preceded_by_skip) {
                        next.pop_back();
                    }
                    continue;
                }
                next.push_back(current);

                if (!preceded_by_skip) {
                    while (i + 1 < result.size() && result[i+1].type != PIC14AsmLine::LABEL) {
                        if (result[i+1].type == PIC14AsmLine::INSTRUCTION) {
                            changed = true;
                        } else {
                            next.push_back(result[i+1]);
                        }
                        i++;
                    }
                }
                w_lit.reset(); w_var.reset();
                rp0.reset(); rp1.reset();
                continue;
            } else if (current.mnemonic == "RETURN" || current.mnemonic == "RETFIE") {
                bool preceded_by_skip = false;
                if (!next.empty() && next.back().type == PIC14AsmLine::INSTRUCTION) {
                    const std::string& prev = next.back().mnemonic;
                    if (prev == "BTFSC" || prev == "BTFSS" || prev == "DECFSZ" || prev == "INCFSZ") {
                        preceded_by_skip = true;
                    }
                }
                next.push_back(current);
                if (!preceded_by_skip) {
                    while (i + 1 < result.size() && result[i+1].type != PIC14AsmLine::LABEL) {
                        if (result[i+1].type == PIC14AsmLine::INSTRUCTION) {
                            changed = true;
                        } else {
                            next.push_back(result[i+1]);
                        }
                        i++;
                    }
                }
                w_lit.reset(); w_var.reset();
                rp0.reset(); rp1.reset();
                continue;
            } else if (current.mnemonic == "CALL") {
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
