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

#ifndef RISCVCODEGEN_H
#define RISCVCODEGEN_H

#pragma once
#include <map>
#include <string>
#include <format>
#include <utility>
#include <vector>

#include "../../../common/DeviceConfig.h"
#include "../../CodeGen.h"
#include "RISCVPeephole.h"

class RISCVCodeGen : public CodeGen {
 public:
  explicit RISCVCodeGen(DeviceConfig cfg);

  void compile(const tacky::Program &program, std::ostream &os) override;

  void emit_context_save() override {}

  void emit_context_restore() override {}

  void emit_interrupt_return() override {}

 private:
  DeviceConfig config;
  std::ostream *out;
  std::vector<RISCVAsmLine> assembly;
  std::map<std::string, int> stack_layout;
  int label_counter = 0;
  std::string current_func_name;
  bool current_is_leaf = true;
  int current_stack_adjustment = 0;

  std::string make_label(const std::string &prefix) {
    return std::format("{}_{}", prefix, label_counter++);
  }

  // --- Memory & Register Management ---
  std::string resolve_address(const tacky::Val &val);

  // --- Emission Helpers ---
  void emit(const std::string &mnemonic) const;

  void emit(const std::string &mnemonic, const std::string &op1) const;

  void emit(const std::string &mnemonic, const std::string &op1,
            const std::string &op2) const;

  void emit(const std::string &mnemonic, const std::string &op1,
            const std::string &op2, const std::string &op3) const;

  void emit_label(const std::string &label) const;

  void emit_comment(const std::string &comment) const;

  void emit_raw(const std::string &text) const;

  // --- Logic Helpers ---
  void load_into_reg(const tacky::Val &val, const std::string &reg);

  void store_reg_into(const std::string &reg, const tacky::Val &val);

  // --- Compilation Dispatchers ---
  void compile_function(const tacky::Function &func);

  void compile_instruction(const tacky::Instruction &instr);

  // --- Instruction Visitors ---
  void compile_variant(const tacky::Return &arg);

  void compile_variant(const tacky::Jump &arg) const;

  void compile_variant(const tacky::JumpIfZero &arg);

  void compile_variant(const tacky::JumpIfNotZero &arg);

  void compile_variant(const tacky::Label &arg) const;

  void compile_variant(const tacky::Call &arg);

  void compile_variant(const tacky::Copy &arg);

  void compile_variant(const tacky::Unary &arg);

  void compile_variant(const tacky::Binary &arg);

  void compile_variant(const tacky::BitSet &arg);

  void compile_variant(const tacky::BitClear &arg);

  void compile_variant(const tacky::BitCheck &arg);

  void compile_variant(const tacky::BitWrite &arg);

  void compile_variant(const tacky::JumpIfBitSet &arg);

  void compile_variant(const tacky::JumpIfBitClear &arg);

  void compile_variant(const tacky::JumpIfEqual &arg);
  void compile_variant(const tacky::JumpIfNotEqual &arg);
  void compile_variant(const tacky::JumpIfLessThan &arg);
  void compile_variant(const tacky::JumpIfLessOrEqual &arg);
  void compile_variant(const tacky::JumpIfGreaterThan &arg);
  void compile_variant(const tacky::JumpIfGreaterOrEqual &arg);

  void compile_variant(const tacky::LoadIndirect &arg);
  void compile_variant(const tacky::StoreIndirect &arg);

  void compile_variant(const tacky::AugAssign &arg);
  void compile_variant(const tacky::InlineAsm &arg);
  void compile_variant(const tacky::DebugLine &arg);
  void compile_variant(const tacky::UARTSendString &) {}  // AVR-only; no-op here
  void compile_variant(const tacky::ArrayLoad &) {}       // AVR-only; no-op here
  void compile_variant(const tacky::ArrayStore &) {}      // AVR-only; no-op here
};

#endif  // RISCVCODEGEN_H