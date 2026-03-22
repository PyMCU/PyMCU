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

#ifndef PIC14STRATEGY_H
#define PIC14STRATEGY_H

#include "ArchStrategy.h"
#include "PIC14CodeGen.h"

// Legacy PIC14 Architecture (e.g. PIC16F84A, PIC16F877A)
class PIC14Strategy : public ArchStrategy {
 public:
  explicit PIC14Strategy(PIC14CodeGen* codegen) : codegen(codegen) {}

  void emit_preamble() override {
    // Legacy Preamble
    codegen->emit_raw(std::format("\tLIST P={}", codegen->config.target_chip));
    std::string chip_short = codegen->config.target_chip;
    if (chip_short.starts_with("pic")) {
      chip_short = chip_short.substr(3);
    }
    codegen->emit_raw(std::format("#include <p{}.inc>", chip_short));
    codegen->emit_config_directives();
  }

  void emit_bank_select(int bank) override {
    // Legacy BCF/BSF method
    if (bank & 1)
      codegen->emit("BSF", "STATUS", "5");
    else
      codegen->emit("BCF", "STATUS", "5");  // RP0
    if (bank & 2)
      codegen->emit("BSF", "STATUS", "6");
    else
      codegen->emit("BCF", "STATUS", "6");  // RP1
  }

  void emit_context_save() override {
    codegen->emit_comment("Context Save (Manual)");
    codegen->emit("MOVWF", "W_TEMP");
    codegen->emit("SWAPF", "STATUS", "W");
    codegen->emit("MOVWF", "STATUS_TEMP");
    // Force Bank 0 (Manual BCFs because we don't track bank in ISR entry yet)
    codegen->emit("BCF", "STATUS", "5");
    codegen->emit("BCF", "STATUS", "6");
  }

  void emit_context_restore() override {
    codegen->emit_comment("Context Restore (Manual)");
    codegen->emit("SWAPF", "STATUS_TEMP", "W");
    codegen->emit("MOVWF", "STATUS");
    codegen->emit("SWAPF", "W_TEMP", "F");
    codegen->emit("SWAPF", "W_TEMP", "W");
  }

  void emit_interrupt_return() override { codegen->emit("RETFIE"); }

 private:
  PIC14CodeGen* codegen;
};

#endif  // PIC14STRATEGY_H
