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

#include "PIC14EStrategy.h"

#include <format>
#include <string>

void PIC14EStrategy::emit_preamble() {
  // Enhanced Preamble (PIC14E)
  codegen->emit_raw(std::format("\tLIST P={}", codegen->config.target_chip));
  std::string chip_short = codegen->config.target_chip;
  if (chip_short.starts_with("pic")) {
    chip_short = chip_short.substr(3);
  }
  codegen->emit_raw(std::format("#include <p{}.inc>", chip_short));

  // ErrorLevel suppression often useful for generated code
  codegen->emit_raw("\terrorlevel -302");

  codegen->emit_config_directives();
}

void PIC14EStrategy::emit_bank_select(int bank) {
  // Optimization: Track current_bsr to avoid redundant MOVLB
  // PIC14E linear data memory is mapped, but SFRs are banked (0-63).
  // Each bank is 128 bytes.

  if (current_bsr != bank) {
    codegen->emit("MOVLB", std::to_string(bank));
    current_bsr = bank;
  }
}

void PIC14EStrategy::emit_context_save() {
  // Hardware Context Save (Shadow Registers)
  // No instructions needed.
  codegen->emit_comment("Context Save (Hardware Automatic)");
}

void PIC14EStrategy::emit_context_restore() {
  // Hardware Context Restore (Shadow Registers)
  // No instructions needed (RETFIE restores).
  codegen->emit_comment("Context Restore (Hardware Automatic)");
}

void PIC14EStrategy::emit_interrupt_return() { codegen->emit("RETFIE"); }
