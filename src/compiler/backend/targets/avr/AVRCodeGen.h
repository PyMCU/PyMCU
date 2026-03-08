/*
 * Copyright (c) 2024 Begeistert and/or its affiliates.
 *
 * This file is part of PyMCU.
 *
 * PyMCU is free software: you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * PyMCU is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with PyMCU.  If not, see <https://www.gnu.org/licenses/>.
 */

#ifndef AVRCODEGEN_H
#define AVRCODEGEN_H

#pragma once
#include <map>
#include <set>
#include <string>
#include <utility>
#include <vector>

#include "../../../common/DeviceConfig.h"
#include "../../CodeGen.h"
#include "AVRLinearScan.h"
#include "AVRPeephole.h"
#include "AVRRegisterAllocator.h"

class AVRCodeGen : public CodeGen {
 public:
  explicit AVRCodeGen(DeviceConfig cfg);

  void compile(const tacky::Program &program, std::ostream &os) override;

  void emit_context_save() override {}

  void emit_context_restore() override {}

  void emit_interrupt_return() override {}

 private:
  DeviceConfig config;
  std::ostream *out;
  std::vector<AVRAsmLine> assembly;
  std::map<std::string, int> stack_layout;
  // Named local variables assigned to AVR scratch registers R4-R15 (greedy, by use count).
  // Variables in reg_layout are accessed via MOV instead of LDD/STD.
  std::map<std::string, std::string> reg_layout;
  // Per-function linear scan: temporaries assigned to R16/R17 (set in compile_function).
  std::map<std::string, std::string> tmp_reg_layout;
  // All temporaries register-allocated across the program (for .equ skipping).
  std::set<std::string> all_tmp_reg_names_;
  int label_counter = 0;

  // Flash string pool: content (incl. any \n suffix) → label (e.g. "__str_0").
  // Strings with identical content share one pool entry (deduplication).
  std::map<std::string, std::string> string_pool_;
  // True when at least one UARTSendString instruction was compiled, causing
  // emit_string_pool() to write the __uart_send_z routine + .db data at EOF.
  bool uart_send_z_needed_ = false;

  // Intern text into the flash string pool; returns the flash label.
  std::string intern_string(const std::string &text);

  // Write __uart_send_z subroutine and all .db string entries directly to os.
  void emit_string_pool(std::ostream &os) const;

  std::string make_label(const std::string &prefix) {
    return prefix + "_" + std::to_string(label_counter++);
  }

  // --- Memory & Register Management ---
  std::string resolve_address(const tacky::Val &val);
  DataType get_val_type(const tacky::Val &val) const;
  std::string get_high_reg(const std::string &reg) const;

  // --- Emission Helpers ---
  void emit(const std::string &mnemonic) const;

  void emit(const std::string &mnemonic, const std::string &op1) const;

  void emit(const std::string &mnemonic, const std::string &op1,
            const std::string &op2) const;

  void emit_label(const std::string &label) const;

  void emit_comment(const std::string &comment) const;

  void emit_raw(const std::string &text) const;

  // Emit a conditional branch using an RJMP trampoline to avoid ±63-word limit.
  // Emits:  INV_COND skip_label / RJMP target / skip_label:
  void emit_branch(const std::string &cond, const std::string &target);

  // --- Logic Helpers ---
  void load_into_reg(const tacky::Val &val, const std::string &reg, DataType type = DataType::UINT8);

  void store_reg_into(const std::string &reg, const tacky::Val &val, DataType type = DataType::UINT8);

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

  void compile_variant(const tacky::LoadIndirect &arg);

  void compile_variant(const tacky::StoreIndirect &arg);

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

  void compile_variant(const tacky::AugAssign &arg);

  void compile_variant(const tacky::InlineAsm &arg);

  void compile_variant(const tacky::DebugLine &arg);

  void compile_variant(const tacky::UARTSendString &arg);
};

#endif  // AVRCODEGEN_H