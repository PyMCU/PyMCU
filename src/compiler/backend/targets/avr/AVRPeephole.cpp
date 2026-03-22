/*
 * -----------------------------------------------------------------------------
 * Whipsnake Compiler (whipc)
 * Copyright (C) 2026 Ivan Montiel Cardona and the Whipsnake Project Authors
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

// Returns true if the line is a flow-terminating instruction (RJMP or RETI).
// After these, all following instructions until the next label/raw are unreachable.
static bool is_flow_terminator(const AVRAsmLine &line) {
  return line.type == AVRAsmLine::INSTRUCTION &&
         (line.mnemonic == "RJMP" || line.mnemonic == "JMP" ||
          line.mnemonic == "RETI" || line.mnemonic == "RET");
}

// Parse "Y+N" offset string, returns N or -1 on failure.
static int parse_y_offset(const std::string &s) {
  if (s.size() < 3 || s[0] != 'Y' || s[1] != '+') return -1;
  try {
    return std::stoi(s.substr(2));
  } catch (...) {
    return -1;
  }
}

// Parse register index from "RN" string, returns N or -1.
static int parse_reg(const std::string &s) {
  if (s.empty() || s[0] != 'R') return -1;
  try {
    return std::stoi(s.substr(1));
  } catch (...) {
    return -1;
  }
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
            line.mnemonic == "JMP"  || line.mnemonic == "CALL"  ||
            line.mnemonic == "BREQ" || line.mnemonic == "BRNE" ||
            line.mnemonic == "BRLO" || line.mnemonic == "BRSH" ||
            line.mnemonic == "BRMI" || line.mnemonic == "BRPL" ||
            line.mnemonic == "BRLT" || line.mnemonic == "BRGE" ||
            line.mnemonic == "BRCS" || line.mnemonic == "BRCC") {
          used_labels.insert(line.op1);
        }
      }
    }
    used_labels.insert("main");
    used_labels.insert("__vector_default");

    // Track register value identities. Two registers with the same identity
    // string hold the same value. Using a monotonic counter ensures that any
    // modification (INC, ADD, LDS, etc.) gives a fresh unique identity, while
    // MOV propagates the source's identity to the destination — allowing
    // detection of redundant MOVs even when the actual value is unknown.
    int mod_ctr = 0;
    std::string aliases[32];
    for (int k = 0; k < 32; ++k)
      aliases[k] = std::format("init_{}", mod_ctr++);

    for (size_t i = 0; i < result.size(); ++i) {
      auto &current = result[i];

      if (current.type == AVRAsmLine::LABEL) {
        if (!used_labels.contains(current.label) &&
            (current.label.starts_with("L.") ||
             current.label.starts_with("L_"))) {
          changed = true;
          continue;
        }
        // Reset all aliases at each label boundary (conservative).
        for (int k = 0; k < 32; ++k)
          aliases[k] = std::format("lab_{}", mod_ctr++);
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
            // Canonical identity for a known constant is "ldi_<value>"
            std::string val_id = "ldi_" + current.op2;
            if (aliases[reg_idx] == val_id) {
              changed = true;
              continue;
            }
            aliases[reg_idx] = val_id;
          }
        } catch (...) {
        }
      } else if (current.mnemonic == "MOV") {
        try {
          int dst_idx = std::stoi(current.op1.substr(1));
          int src_idx = std::stoi(current.op2.substr(1));
          if (dst_idx >= 0 && dst_idx < 32 && src_idx >= 0 && src_idx < 32) {
            // MOV Rd, Rs: if Rd already has the same identity as Rs, skip.
            // This covers both "same known constant" and "same alias chain"
            // (e.g., R5 was copied from R24 and R24 hasn't changed since).
            if (aliases[dst_idx] == aliases[src_idx]) {
              changed = true;
              continue;
            }
            aliases[dst_idx] = aliases[src_idx];
          } else {
            if (dst_idx >= 0 && dst_idx < 32)
              aliases[dst_idx] = std::format("mov_{}", mod_ctr++);
          }
        } catch (...) {
          for (int k = 0; k < 32; ++k)
            aliases[k] = std::format("err_{}", mod_ctr++);
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
          if (reg_idx >= 0 && reg_idx < 32)
            aliases[reg_idx] = std::format("load_{}", mod_ctr++);
        } catch (...) {
          for (int k = 0; k < 32; ++k)
            aliases[k] = std::format("err_{}", mod_ctr++);
        }
      } else if (current.mnemonic == "CLR") {
        try {
          int reg_idx = std::stoi(current.op1.substr(1));
          if (reg_idx >= 0 && reg_idx < 32)
            aliases[reg_idx] = "ldi_0";  // same identity as LDI Rd, 0
        } catch (...) {
          for (int k = 0; k < 32; ++k)
            aliases[k] = std::format("err_{}", mod_ctr++);
        }
      } else if (current.mnemonic == "ADD" || current.mnemonic == "SUB" ||
                 current.mnemonic == "INC" || current.mnemonic == "DEC" ||
                 current.mnemonic == "NEG" || current.mnemonic == "COM" ||
                 current.mnemonic == "ORI" || current.mnemonic == "ANDI" ||
                 current.mnemonic == "EOR" || current.mnemonic == "AND" ||
                 current.mnemonic == "OR" || current.mnemonic == "ADC" ||
                 current.mnemonic == "SBC" || current.mnemonic == "LSR" ||
                 current.mnemonic == "ASR" || current.mnemonic == "ROR" ||
                 current.mnemonic == "LSL" || current.mnemonic == "ROL" ||
                 current.mnemonic == "MUL" || current.mnemonic == "MULS" ||
                 current.mnemonic == "CPC" || current.mnemonic == "CPI") {
        try {
          int reg_idx = std::stoi(current.op1.substr(1));
          if (reg_idx >= 0 && reg_idx < 32)
            aliases[reg_idx] = std::format("mod_{}", mod_ctr++);
        } catch (...) {
          for (int k = 0; k < 32; ++k)
            aliases[k] = std::format("err_{}", mod_ctr++);
        }
      } else if (is_flow_terminator(current)) {
        // --- Unreachable code removal after RJMP / RETI ---
        // For RJMP: also check if it's a jump to the immediately next label.
        if (current.mnemonic == "RJMP") {
          bool redundant = false;
          for (size_t j = i + 1; j < result.size(); ++j) {
            if (result[j].type == AVRAsmLine::LABEL) {
              if (result[j].label == current.op1) redundant = true;
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
        }

        next.push_back(current);

        // SBIS/SBIC + RJMP is a *conditional* branch pattern:
        //   SBIS reg, bit  →  if bit SET: skip RJMP → fall through (if-body)
        //   RJMP L_skip    →  if bit CLEAR: jump to L_skip (skip if-body)
        // The code after the RJMP IS reachable via the skip path, so we must
        // NOT remove it as dead code. Detect this by checking whether the last
        // INSTRUCTION emitted before this RJMP was SBIS or SBIC.
        bool preceded_by_skip = false;
        if (current.mnemonic == "RJMP") {
          for (int k = (int)next.size() - 2; k >= 0; --k) {
            if (next[k].type == AVRAsmLine::INSTRUCTION) {
              preceded_by_skip = (next[k].mnemonic == "SBIS" ||
                                  next[k].mnemonic == "SBIC");
              break;
            }
          }
        }

        if (!preceded_by_skip) {
          // Remove unreachable instructions after unconditional flow terminator.
          // Stop at the next LABEL or RAW (e.g., .org directives in ISR vector tables).
          while (i + 1 < result.size() &&
                 result[i + 1].type != AVRAsmLine::LABEL &&
                 result[i + 1].type != AVRAsmLine::RAW) {
            if (result[i + 1].type == AVRAsmLine::INSTRUCTION) {
              changed = true;
            } else {
              next.push_back(result[i + 1]);
            }
            i++;
          }
        }
        for (int k = 0; k < 32; ++k)
          aliases[k] = std::format("flow_{}", mod_ctr++);
        continue;
      } else {
        // Unknown instruction — conservatively reset all aliases.
        for (int k = 0; k < 32; ++k)
          aliases[k] = std::format("unk_{}", mod_ctr++);
      }

      next.push_back(current);
    }
    result = next;
  }

  // --- STD/LDD Forwarding Pass ---
  // Pattern A: STD Y+N, Rx  immediately followed by  LDD Ry, Y+N
  //   → If Rx == Ry: delete both (value already in Rx)
  //   → If Rx != Ry: replace pair with MOV Ry, Rx
  // Pattern B: LDD Rx, Y+N  immediately followed by  STD Y+N, Rx
  //   → Delete the STD (storing a value that was just loaded from that same slot)
  // "Immediately" means: no INSTRUCTION or LABEL between the two, only COMMENTs/EMPTY.
  bool fwd_changed = true;
  while (fwd_changed) {
    fwd_changed = false;
    for (size_t i = 0; i + 1 < result.size(); ++i) {
      if (result[i].type != AVRAsmLine::INSTRUCTION) continue;

      // Find the next instruction (skip comments/empty)
      size_t j = i + 1;
      while (j < result.size() &&
             (result[j].type == AVRAsmLine::COMMENT ||
              result[j].type == AVRAsmLine::EMPTY)) {
        j++;
      }
      if (j >= result.size() || result[j].type != AVRAsmLine::INSTRUCTION) continue;

      auto &a = result[i];
      auto &b = result[j];

      // Pattern A: STD Y+N, Rx ; LDD Ry, Y+N
      if (a.mnemonic == "STD" && b.mnemonic == "LDD") {
        int a_off = parse_y_offset(a.op1);
        int b_off = parse_y_offset(b.op2);
        if (a_off >= 0 && a_off == b_off) {
          int rx = parse_reg(a.op2);
          int ry = parse_reg(b.op1);
          if (rx >= 0 && ry >= 0) {
            // Check if there are any later LDD instructions for the same Y+N
            // offset. If yes, the STD is still needed to hold the value.
            bool later_load = false;
            for (size_t k = j + 1; k < result.size(); ++k) {
              if (result[k].type != AVRAsmLine::INSTRUCTION) continue;
              if (result[k].mnemonic == "LDD" &&
                  parse_y_offset(result[k].op2) == a_off) {
                later_load = true;
                break;
              }
            }
            if (!later_load) {
              if (rx == ry) {
                // Delete both: value already in Rx, no need to spill and reload
                result[i] = AVRAsmLine::Empty();
                result[j] = AVRAsmLine::Empty();
              } else {
                // Replace STD+LDD with MOV Ry, Rx
                result[i] = AVRAsmLine::Empty();
                result[j] = AVRAsmLine::Instruction("MOV",
                    std::format("R{}", ry), std::format("R{}", rx));
              }
              fwd_changed = true;
              changed = true;
            } else {
              // There is a later load from Y+N; keep the STD but still forward
              // the value across the immediate LDD to avoid the redundant reload.
              if (rx == ry) {
                // STD Y+N, Rx; LDD Rx, Y+N → just delete the LDD (value in Rx)
                result[j] = AVRAsmLine::Empty();
              } else {
                // STD Y+N, Rx; LDD Ry, Y+N → keep STD, replace LDD with MOV
                result[j] = AVRAsmLine::Instruction("MOV",
                    std::format("R{}", ry), std::format("R{}", rx));
              }
              fwd_changed = true;
              changed = true;
            }
          }
        }
      }

      // Pattern B: LDD Rx, Y+N ; STD Y+N, Rx — redundant store after load
      if (a.mnemonic == "LDD" && b.mnemonic == "STD") {
        int a_off = parse_y_offset(a.op2);
        int b_off = parse_y_offset(b.op1);
        if (a_off >= 0 && a_off == b_off && a.op1 == b.op2) {
          result[j] = AVRAsmLine::Empty();
          fwd_changed = true;
          changed = true;
        }
      }
    }
  }

  // --- 3-Instruction Window: MOV Ra, Rb; OP Ra; MOV Rb, Ra → OP Rb ---
  // Single-operand ops (INC, DEC, COM, NEG) work for all AVR registers.
  // Compresses the common load-op-store pattern produced for named variables
  // in reg_layout (e.g., col in R4): MOV R24,R4; INC R24; MOV R4,R24 → INC R4
  {
    bool win3 = true;
    while (win3) {
      win3 = false;
      for (size_t i = 0; i < result.size(); ++i) {
        if (result[i].type != AVRAsmLine::INSTRUCTION) continue;
        // Find next instruction (skip comments/empty)
        size_t j = i + 1;
        while (j < result.size() && (result[j].type == AVRAsmLine::COMMENT ||
                                      result[j].type == AVRAsmLine::EMPTY))
          ++j;
        if (j >= result.size() || result[j].type != AVRAsmLine::INSTRUCTION)
          continue;
        // Find instruction after that
        size_t k = j + 1;
        while (k < result.size() && (result[k].type == AVRAsmLine::COMMENT ||
                                      result[k].type == AVRAsmLine::EMPTY))
          ++k;
        if (k >= result.size() || result[k].type != AVRAsmLine::INSTRUCTION)
          continue;

        auto &a = result[i];  // MOV Ra, Rb
        auto &b = result[j];  // OP Ra
        auto &c = result[k];  // MOV Rb, Ra

        if (a.mnemonic == "MOV" && c.mnemonic == "MOV" &&
            (b.mnemonic == "INC" || b.mnemonic == "DEC" ||
             b.mnemonic == "COM" || b.mnemonic == "NEG") &&
            b.op1 == a.op1 &&   // OP target == MOV dst (Ra)
            c.op2 == a.op1 &&   // final MOV src == OP target (Ra)
            c.op1 == a.op2) {   // final MOV dst == original MOV src (Rb)
          const std::string Ra = a.op1;  // scratch reg (e.g. R24)
          const std::string Rb = a.op2;  // named-var reg (e.g. R4)

          // Check whether Ra is used as a source in the very next instruction
          // after c. This happens when alias tracking eliminated the explicit
          // load (e.g. STD Y+N, Ra for uart.write). If so, keep Ra in sync.
          size_t next = k + 1;
          while (next < result.size() &&
                 (result[next].type == AVRAsmLine::COMMENT ||
                  result[next].type == AVRAsmLine::EMPTY))
            ++next;
          bool ra_needed = (next < result.size() &&
                            result[next].type == AVRAsmLine::INSTRUCTION &&
                            (result[next].op1 == Ra || result[next].op2 == Ra));

          b.op1 = Rb;              // redirect OP to operate on Rb directly
          a = AVRAsmLine::Empty(); // drop: MOV Ra, Rb (now redundant)
          if (ra_needed) {
            // Invert c: MOV Rb, Ra → MOV Ra, Rb so Ra = Rb after the op.
            c.op1 = Ra;
            c.op2 = Rb;
          } else {
            c = AVRAsmLine::Empty(); // Ra is dead after the window; safe to drop
          }
          win3 = changed = true;
        }
      }
      // Remove EMPTY lines inserted by this sub-pass before next iteration
      result.erase(
          std::remove_if(result.begin(), result.end(),
                         [](const AVRAsmLine &l) {
                           return l.type == AVRAsmLine::EMPTY;
                         }),
          result.end());
    }
  }

  // Final pass: remove any remaining EMPTY lines
  std::vector<AVRAsmLine> final_result;
  final_result.reserve(result.size());
  for (const auto &line : result) {
    if (line.type != AVRAsmLine::EMPTY) {
      final_result.push_back(line);
    }
  }

  return final_result;
}
