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

#ifndef PIC14CODEGEN_H
#define PIC14CODEGEN_H

#pragma once
#include <map>
#include <memory>
#include <string>
#include <utility>
#include <vector>

#include "../../../common/DeviceConfig.h"
#include "../../CodeGen.h"
#include "ArchStrategy.h"
#include "PIC14Peephole.h"

// Forward declare for friends
class PIC14Strategy;
class PIC14EStrategy;

class PIC14CodeGen : public CodeGen {
  friend class PIC14Strategy;
  friend class PIC14EStrategy;

 public:
  explicit PIC14CodeGen(DeviceConfig cfg);

  void compile(const tacky::Program &program, std::ostream &os) override;

  // Interrupt Support
  void emit_context_save() override;

  void emit_context_restore() override;

  void emit_interrupt_return() override;

  void set_stack_layout(std::map<std::string, int> layout) {
    stack_layout = std::move(layout);
  }

  // Exposed for ArchStrategy
  void emit_comment(const std::string &comment) const;
  void emit(const std::string &mnemonic) const;
  void emit(const std::string &mnemonic, const std::string &op1) const;
  void emit(const std::string &mnemonic, const std::string &op1,
            const std::string &op2) const;

 private:
  DeviceConfig config;
  std::unique_ptr<ArchStrategy> strategy;  // Architecture Strategy

  std::ostream *out;
  std::vector<PIC14AsmLine> assembly;
  std::map<std::string, int> symbol_table;
  std::map<std::string, int> stack_layout;
  std::map<std::string, int> var_sizes;
  int ram_head;
  int label_counter = 0;
  bool uses_float = false;

  std::string make_label(const std::string &prefix) {
    return prefix + "_" + std::to_string(label_counter++);
  }

  // --- Memory & Bank Management ---
  std::string get_or_alloc_variable(const std::string &name);

  std::string resolve_address(const tacky::Val &val);

  void select_bank(const std::string &operand);

  int current_bank = -1;
  bool current_block_terminated = false;
  std::string current_function_name;

  // --- Emission Helpers ---
  // (Moved public)

  void emit_float_add(const std::string &target, const std::string &source);

  void emit_label(const std::string &label) const;

  void emit_raw(const std::string &text) const;

  void emit_config_directives();

  // --- Logic Helpers (The "Glue") ---
  void load_into_w(const tacky::Val &val);

  void store_w_into(const tacky::Val &val);

  // --- Compilation Dispatchers ---

  void compile_function(const tacky::Function &func);

  void compile_instruction(const tacky::Instruction &instr);

  // --- Instruction Visitors (Implementation) ---

  // Control Flow
  void compile_variant(const tacky::Return &arg);

  void compile_variant(const tacky::Jump &arg) const;

  void compile_variant(const tacky::JumpIfZero &arg);

  void compile_variant(const tacky::JumpIfBitSet &arg);

  void compile_variant(const tacky::JumpIfBitClear &arg);

  void compile_variant(const tacky::JumpIfEqual &arg);
  void compile_variant(const tacky::JumpIfNotEqual &arg);
  void compile_variant(const tacky::JumpIfLessThan &arg);
  void compile_variant(const tacky::JumpIfLessOrEqual &arg);
  void compile_variant(const tacky::JumpIfGreaterThan &arg);
  void compile_variant(const tacky::JumpIfGreaterOrEqual &arg);

  void compile_variant(const tacky::JumpIfNotZero &arg);

  void compile_variant(const tacky::Label &arg) const;

  void compile_variant(const tacky::Call &arg);

  // Data Movement & Arithmetic
  void compile_variant(const tacky::Copy &arg);

  void compile_variant(const tacky::LoadIndirect &arg);

  void compile_variant(const tacky::StoreIndirect &arg);

  void compile_variant(const tacky::Unary &arg);

  void compile_variant(const tacky::Binary &arg);

  // Bit Manipulation (Hardware Access)
  void compile_variant(const tacky::BitSet &arg);

  void compile_variant(const tacky::BitClear &arg);

  void compile_variant(const tacky::BitCheck &arg);

  void compile_variant(const tacky::BitWrite &arg);

  void compile_variant(const tacky::AugAssign &arg);

  void compile_variant(const tacky::InlineAsm &arg);

  void compile_variant(const tacky::DebugLine &arg);

  void compile_variant(const tacky::UARTSendString &) {}  // AVR-only; no-op here
  void compile_variant(const tacky::ArrayLoad &) {}       // AVR-only; no-op here
  void compile_variant(const tacky::ArrayStore &) {}      // AVR-only; no-op here
};

#endif  // PIC14CODEGEN_H