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

#include "PIC14Peephole.h"

#include <algorithm>
#include <format>
#include <optional>
#include <set>

static std::vector<PIC14AsmLine> coalesce_bit_ops(
    const std::vector<PIC14AsmLine> &lines) {
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
            } catch (...) {
              break;
            }
          } else {
            break;
          }
        } else if (lines[j].type == PIC14AsmLine::COMMENT ||
                   lines[j].type == PIC14AsmLine::EMPTY) {
          j++;
        } else {
          break;
        }
      }

      if (bits.size() >= 3) {
        int mask = 0;
        for (int b : bits) mask |= (1 << b);

        if (mnemonic == "BSF") {
          result.push_back(PIC14AsmLine::Instruction(
              "MOVLW", std::format("0x{:02X}", mask)));
          result.push_back(PIC14AsmLine::Instruction("IORWF", reg, "F"));
        } else {
          result.push_back(PIC14AsmLine::Instruction(
              "MOVLW", std::format("0x{:02X}", (unsigned char)~mask)));
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

std::vector<PIC14AsmLine> PIC14Peephole::optimize(
    const std::vector<PIC14AsmLine> &lines) {
  std::vector<PIC14AsmLine> result = coalesce_bit_ops(lines);
  bool changed = true;

  while (changed) {
    changed = false;

    // --- Dead Label Elimination ---
    std::set<std::string> used_labels;
    for (const auto &line : result) {
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
    std::optional<int> current_movlb;  // Track MOVLB for PIC14E

    for (size_t i = 0; i < result.size(); ++i) {
      auto &current = result[i];

      // --- Copy Propagation (Task 1) ---
      // Pattern: MOVWF T -> MOVF T, W -> MOVWF D
      // Optimization: ... -> MOVWF D (Skip T if T is tmp)
      if (current.type == PIC14AsmLine::INSTRUCTION &&
          current.mnemonic == "MOVWF" && current.op1.starts_with("tmp.")) {
        std::string tmp_reg = current.op1;
        size_t j = i + 1;
        auto skip = [&]() {
          while (j < result.size() &&
                 (result[j].type == PIC14AsmLine::COMMENT ||
                  result[j].type == PIC14AsmLine::EMPTY))
            j++;
        };
        skip();

        if (j < result.size() && result[j].type == PIC14AsmLine::INSTRUCTION &&
            result[j].mnemonic == "MOVF" && result[j].op1 == tmp_reg &&
            result[j].op2 == "W") {
          size_t k = j + 1;
          while (k < result.size() &&
                 (result[k].type == PIC14AsmLine::COMMENT ||
                  result[k].type == PIC14AsmLine::EMPTY))
            k++;

          if (k < result.size() &&
              result[k].type == PIC14AsmLine::INSTRUCTION &&
              result[k].mnemonic == "MOVWF") {
            // Found sequence: MOVWF T, MOVF T,W, MOVWF D
            // We can skip the first two instructions.
            // The value is already in W (implied by MOVWF T).
            // So we just proceed to MOVWF D.
            // We advance i to point to MOVF T,W so the loop logic
            // will increment it to MOVWF D next.
            // Actually, let's just push nothing and advance i.

            i = j;  // Skip MOVWF T and MOVF T,W.
            // Next iteration will process result[k] (MOVWF D)
            // because loop increments i. Wait, loop increments i.
            // If I set i = j, next iter i becomes j+1 which is k (if
            // contiguous). Yes. j points to MOVF. i=j. Next loop i=j+1 =
            // k? My skip() logic advanced j past comments. result[j] is
            // MOVF. So if I set i = j, loop checks i < size, then ++i ->
            // i = j+1. If there are comments between j and k, i will
            // process them. That's fine.

            changed = true;
            w_var.reset();  // We disrupted the tracking, safe reset
            continue;
          }
        }
      }

      // --- Comparison to Jump Optimization ---
      if (current.type == PIC14AsmLine::INSTRUCTION &&
          current.mnemonic == "CLRF" && current.op1.starts_with("tmp.")) {
        std::string tmp_reg = current.op1;
        size_t j = i + 1;
        auto skip = [&]() {
          while (j < result.size() &&
                 (result[j].type == PIC14AsmLine::COMMENT ||
                  result[j].type == PIC14AsmLine::EMPTY))
            j++;
        };

        skip();
        if (j < result.size() && result[j].type == PIC14AsmLine::INSTRUCTION &&
            (result[j].mnemonic == "BTFSS" || result[j].mnemonic == "BTFSC") &&
            result[j].op1 == "STATUS") {
          PIC14AsmLine i1 = result[j++];
          skip();
          if (j < result.size() &&
              result[j].type == PIC14AsmLine::INSTRUCTION &&
              result[j].mnemonic == "INCF" && result[j].op1 == tmp_reg &&
              result[j].op2 == "F") {
            j++;
            skip();
            if (j < result.size() &&
                result[j].type == PIC14AsmLine::INSTRUCTION &&
                result[j].mnemonic == "MOVF" && result[j].op1 == tmp_reg &&
                result[j].op2 == "W") {
              j++;
              skip();
              if (j < result.size() &&
                  result[j].type == PIC14AsmLine::INSTRUCTION &&
                  result[j].mnemonic == "IORLW" && result[j].op1 == "0") {
                j++;
                skip();
              }

              if (j + 1 < result.size() &&
                  result[j].type == PIC14AsmLine::INSTRUCTION &&
                  (result[j].mnemonic == "BTFSS" ||
                   result[j].mnemonic == "BTFSC") &&
                  result[j].op1 == "STATUS" && result[j].op2 == "2" &&
                  result[j + 1].type == PIC14AsmLine::INSTRUCTION &&
                  result[j + 1].mnemonic == "GOTO") {
                bool bit_is_sc = (i1.mnemonic == "BTFSC");
                bool z_is_sc = (result[j].mnemonic == "BTFSC");
                std::string new_mnemonic =
                    (bit_is_sc == z_is_sc) ? "BTFSS" : "BTFSC";

                next.push_back(
                    PIC14AsmLine::Instruction(new_mnemonic, "STATUS", i1.op2));
                next.push_back(result[j + 1]);
                i = j + 1;
                changed = true;
                continue;
              }
            }
          }
        }
      }

      if (current.type == PIC14AsmLine::LABEL) {
        if (!used_labels.contains(current.label) &&
            (current.label.starts_with("L.") ||
             current.label.starts_with("L_"))) {
          changed = true;
          continue;
        }
        w_lit.reset();
        w_var.reset();
        rp0.reset();
        rp1.reset();
        current_movlb.reset();
        next.push_back(current);
        continue;
      }
      if (current.type != PIC14AsmLine::INSTRUCTION) {
        next.push_back(current);
        continue;
      }

      // --- Bank tracking (PIC14E MOVLB) ---
      if (current.mnemonic == "MOVLB") {
        try {
          int bank = std::stoi(current.op1);
          if (current_movlb && *current_movlb == bank) {
            changed = true;
            continue;  // Skip redundant MOVLB
          }
          current_movlb = bank;
        } catch (...) {
          current_movlb.reset();
        }
        next.push_back(current);
        continue;
      }

      // --- Bank tracking (Legacy PIC14 RP0/RP1) ---
      if (current.mnemonic == "BSF" && current.op1 == "STATUS" &&
          current.op2 == "5") {
        if (rp0 && *rp0 == true) {
          changed = true;
          continue;
        }
        rp0 = true;
      } else if (current.mnemonic == "BCF" && current.op1 == "STATUS" &&
                 current.op2 == "5") {
        if (rp0 && *rp0 == false) {
          changed = true;
          continue;
        }
        rp0 = false;
      } else if (current.mnemonic == "BSF" && current.op1 == "STATUS" &&
                 current.op2 == "6") {
        if (rp1 && *rp1 == true) {
          changed = true;
          continue;
        }
        rp1 = true;
      } else if (current.mnemonic == "BCF" && current.op1 == "STATUS" &&
                 current.op2 == "6") {
        if (rp1 && *rp1 == false) {
          changed = true;
          continue;
        }
        rp1 = false;
      }

      // --- State tracking optimizations ---

      if (current.mnemonic == "MOVLW") {
        if (w_lit && *w_lit == current.op1) {
          changed = true;
          continue;  // Skip redundant MOVLW
        }
        w_lit = current.op1;
        w_var.reset();
      } else if (current.mnemonic == "MOVF" && current.op2 == "W") {
        if (w_var && *w_var == current.op1) {
          changed = true;
          continue;  // Skip redundant MOVF
        }
        w_var = current.op1;
        w_lit.reset();
      } else if (current.mnemonic == "MOVWF") {
        if (w_var && *w_var == current.op1) {
          changed = true;
          continue;  // Redundant store
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
            const std::string &m = next[j].mnemonic;
            if (m == "MOVF" || m == "ADDWF" || m == "SUBWF" || m == "ANDWF" ||
                m == "IORWF" || m == "XORWF" || m == "INCF" || m == "DECF" ||
                m == "ADDLW" || m == "SUBLW" || m == "ANDLW" || m == "XORLW" ||
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
      } else if (current.mnemonic == "ADDLW" &&
                 (current.op1 == "0" || current.op1 == "0x00")) {
        changed = true;
        continue;
      } else if (current.mnemonic == "XORLW" &&
                 (current.op1 == "0" || current.op1 == "0x00")) {
        changed = true;
        continue;
      } else if (current.mnemonic == "ANDLW" &&
                 (current.op1 == "255" || current.op1 == "0xFF")) {
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
          } else if (result[j].type == PIC14AsmLine::COMMENT ||
                     result[j].type == PIC14AsmLine::EMPTY) {
            continue;
          } else {
            break;
          }
        }

        bool preceded_by_skip = false;
        if (!next.empty() && next.back().type == PIC14AsmLine::INSTRUCTION) {
          const std::string &prev = next.back().mnemonic;
          if (prev == "BTFSC" || prev == "BTFSS" || prev == "DECFSZ" ||
              prev == "INCFSZ") {
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
          while (i + 1 < result.size() &&
                 result[i + 1].type != PIC14AsmLine::LABEL) {
            if (result[i + 1].type == PIC14AsmLine::INSTRUCTION) {
              changed = true;
            } else {
              next.push_back(result[i + 1]);
            }
            i++;
          }
        }
        w_lit.reset();
        w_var.reset();
        rp0.reset();
        rp1.reset();
        current_movlb.reset();
        continue;
      } else if (current.mnemonic == "RETURN" || current.mnemonic == "RETFIE") {
        bool preceded_by_skip = false;
        if (!next.empty() && next.back().type == PIC14AsmLine::INSTRUCTION) {
          const std::string &prev = next.back().mnemonic;
          if (prev == "BTFSC" || prev == "BTFSS" || prev == "DECFSZ" ||
              prev == "INCFSZ") {
            preceded_by_skip = true;
          }
        }
        next.push_back(current);
        if (!preceded_by_skip) {
          while (i + 1 < result.size() &&
                 result[i + 1].type != PIC14AsmLine::LABEL) {
            if (result[i + 1].type == PIC14AsmLine::INSTRUCTION) {
              changed = true;
            } else {
              next.push_back(result[i + 1]);
            }
            i++;
          }
        }
        w_lit.reset();
        w_var.reset();
        rp0.reset();
        rp1.reset();
        current_movlb.reset();
        continue;
      } else if (current.mnemonic == "CALL") {
        w_lit.reset();
        w_var.reset();
        rp0.reset();
        rp1.reset();
        current_movlb.reset();
      } else {
        // Arithmetic instructions typically change W and flags
        w_lit.reset();
        w_var.reset();
      }

      // --- INCF/DECF Optimization ---
      // Pattern 1: MOVF x, W -> ADDLW 1 -> MOVWF x  =>  INCF x, F
      // Pattern 2: MOVF x, W -> ADDLW 1 -> MOVWF tmp -> MOVF tmp, W -> MOVWF x
      // => INCF x, F
      if (current.mnemonic == "MOVF" && current.op2 == "W") {
        std::string reg = current.op1;  // x

        // Helper to get next instruction
        size_t j = i + 1;
        auto next_inst = [&](size_t &idx) -> PIC14AsmLine * {
          while (idx < result.size() &&
                 (result[idx].type == PIC14AsmLine::COMMENT ||
                  result[idx].type == PIC14AsmLine::EMPTY))
            idx++;
          if (idx < result.size() &&
              result[idx].type == PIC14AsmLine::INSTRUCTION)
            return &result[idx];
          return nullptr;
        };

        size_t idx2 = j;
        PIC14AsmLine *inst2 = next_inst(idx2);

        if (inst2) {
          bool is_inc = false;
          bool is_dec = false;

          if (inst2->mnemonic == "ADDLW") {
            if (inst2->op1 == "1" || inst2->op1 == "0x01")
              is_inc = true;
            else if (inst2->op1 == "255" || inst2->op1 == "0xFF" ||
                     inst2->op1 == "-1")
              is_dec = true;
          }

          if (is_inc || is_dec) {
            size_t idx3 = idx2 + 1;
            PIC14AsmLine *inst3 = next_inst(idx3);

            if (inst3) {
              // Case 1: Direct move back to x
              if (inst3->mnemonic == "MOVWF" && inst3->op1 == reg) {
                std::string new_mnemonic = is_inc ? "INCF" : "DECF";
                next.push_back(
                    PIC14AsmLine::Instruction(new_mnemonic, reg, "F"));
                w_lit.reset();
                w_var.reset();
                i = idx3;
                changed = true;
                continue;
              }
              // Case 2: Move to temp, then move temp back to x
              else if (inst3->mnemonic == "MOVWF" &&
                       inst3->op1.find("tmp.") == 0) {
                std::string tmp_reg = inst3->op1;

                size_t idx4 = idx3 + 1;
                PIC14AsmLine *inst4 = next_inst(idx4);

                if (inst4 && inst4->mnemonic == "MOVF" &&
                    inst4->op1 == tmp_reg && inst4->op2 == "W") {
                  size_t idx5 = idx4 + 1;
                  PIC14AsmLine *inst5 = next_inst(idx5);

                  if (inst5 && inst5->mnemonic == "MOVWF" &&
                      inst5->op1 == reg) {
                    std::string new_mnemonic = is_inc ? "INCF" : "DECF";
                    next.push_back(
                        PIC14AsmLine::Instruction(new_mnemonic, reg, "F"));
                    w_lit.reset();
                    w_var.reset();
                    i = idx5;
                    changed = true;
                    continue;
                  }
                }
              }
            }
          }
        }
      }

      next.push_back(current);
    }
    result = next;
  }

  return result;
}