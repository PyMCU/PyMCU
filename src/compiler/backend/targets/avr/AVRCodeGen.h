#ifndef AVRCODEGEN_H
#define AVRCODEGEN_H

#pragma once
#include <map>
#include <string>
#include <utility>
#include <vector>

#include "../../CodeGen.h"
#include "AVRPeephole.h"
#include "DeviceConfig.h"

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
  int label_counter = 0;

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
  void compile_variant(const tacky::AugAssign &arg);
  void compile_variant(const tacky::Delay &arg);
  void compile_variant(const tacky::DebugLine &arg);
};

#endif // AVRCODEGEN_H
