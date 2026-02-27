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

#ifndef PIOCODEGEN_H
#define PIOCODEGEN_H

#pragma once
#include <map>
#include <string>
#include <vector>

#include "../../../common/DeviceConfig.h"
#include "../../CodeGen.h"
#include "PIOPeephole.h"

class PIOCodeGen : public CodeGen {
 public:
  explicit PIOCodeGen(DeviceConfig cfg);

  void compile(const tacky::Program &program, std::ostream &os) override;

  void emit_context_save() override {}

  void emit_context_restore() override {}

  void emit_interrupt_return() override {}

 private:
  DeviceConfig config;
  std::vector<PIOAsmLine> assembly;
  std::map<std::string, std::string>
      register_map;  // Maps Variable/Temporary to X or Y
  int label_counter = 0;

  std::string make_label(const std::string &prefix) {
    return std::format("{}_{}", prefix, label_counter++);
  }

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
  std::string resolve_operand(const tacky::Val &val);

  void allocate_registers(const tacky::Function &func);

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

  void compile_variant(const tacky::LoadIndirect &arg);
  void compile_variant(const tacky::StoreIndirect &arg);

  void compile_variant(const tacky::AugAssign &arg);
  void compile_variant(const tacky::InlineAsm &arg);
  void compile_variant(const tacky::DebugLine &arg);

  void compile_variant(const tacky::JumpIfEqual &arg);
  void compile_variant(const tacky::JumpIfNotEqual &arg);
  void compile_variant(const tacky::JumpIfLessThan &arg);
  void compile_variant(const tacky::JumpIfLessOrEqual &arg);
  void compile_variant(const tacky::JumpIfGreaterThan &arg);
  void compile_variant(const tacky::JumpIfGreaterOrEqual &arg);
  void compile_variant(const tacky::UARTSendString &) {}  // AVR-only; no-op here
};

#endif  // PIOCODEGEN_H