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

#ifndef PIC12CODEGEN_H
#define PIC12CODEGEN_H

#pragma once
#include <map>
#include <string>
#include <utility>
#include <vector>

#include "../../../common/DeviceConfig.h"
#include "../../CodeGen.h"

struct PIC12AsmLine {
  enum Type { INSTRUCTION, LABEL, COMMENT, RAW, EMPTY };

  Type type;
  std::string label;
  std::string mnemonic;
  std::string op1;
  std::string op2;
  std::string content;

  static PIC12AsmLine Instruction(std::string m, std::string o1 = "",
                                  std::string o2 = "") {
    return {INSTRUCTION, "", m, o1, o2, ""};
  }

  static PIC12AsmLine Label(std::string l) {
    return {LABEL, l, "", "", "", ""};
  }

  static PIC12AsmLine Comment(std::string c) {
    return {COMMENT, "", "", "", "", c};
  }

  static PIC12AsmLine Raw(std::string r) { return {RAW, "", "", "", "", r}; }

  std::string to_string() const;
};

class PIC12CodeGen : public CodeGen {
 public:
  explicit PIC12CodeGen(DeviceConfig cfg);

  void compile(const tacky::Program &program, std::ostream &os) override;

  void emit_context_save() override {}

  void emit_context_restore() override {}

  void emit_interrupt_return() override {}

  void set_stack_layout(std::map<std::string, int> layout) {
    this->stack_layout = std::move(layout);
  }

 private:
  DeviceConfig config;
  std::vector<PIC12AsmLine> assembly;
  std::map<std::string, int> symbol_table;
  std::map<std::string, int> stack_layout;
  int ram_head;
  int label_counter = 0;

  std::string make_label(const std::string &prefix) {
    return std::format("{}_{}", prefix, label_counter++);
  }

  std::string resolve_address(const tacky::Val &val);

  std::string get_or_alloc_variable(const std::string &name);

  void emit(const std::string &mnemonic) const;

  void emit(const std::string &mnemonic, const std::string &op1) const;

  void emit(const std::string &mnemonic, const std::string &op1,
            const std::string &op2) const;

  void emit_label(const std::string &label) const;

  void emit_comment(const std::string &comment) const;

  void emit_raw(const std::string &text) const;

  void load_into_w(const tacky::Val &val);

  void store_w_into(const tacky::Val &val);

  void compile_function(const tacky::Function &func);

  void compile_instruction(const tacky::Instruction &instr);

  // Instruction Visitors
  void compile_variant(const tacky::Return &arg);

  void compile_variant(const tacky::Copy &arg);

  void compile_variant(const tacky::Unary &arg);

  void compile_variant(const tacky::Binary &arg);

  void compile_variant(const tacky::Jump &arg) const;

  void compile_variant(const tacky::JumpIfZero &arg);

  void compile_variant(const tacky::JumpIfNotZero &arg);

  void compile_variant(const tacky::Label &arg) const;

  void compile_variant(const tacky::Call &arg);

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
};

#endif  // PIC12CODEGEN_H