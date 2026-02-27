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

#include "PIC18CodeGen.h"

#include <algorithm>
#include <format>
#include <iostream>
#include <utility>
#include <variant>

#include "backend/analysis/StackAllocator.h"

PIC18CodeGen::PIC18CodeGen(DeviceConfig cfg)
    : config(std::move(cfg)), out(nullptr) {
  label_counter = 0;
  ram_head = 0x60;  // Start after Access Bank
}

std::string PIC18CodeGen::resolve_address(const tacky::Val &val) {
  if (std::holds_alternative<std::monostate>(val)) {
    return "";
  }
  if (const auto c = std::get_if<tacky::Constant>(&val)) {
    return std::format("0x{:02X}", c->value & 0xFF);
  }
  if (const auto mem = std::get_if<tacky::MemoryAddress>(&val)) {
    return std::format("0x{:02X}", mem->address);
  }

  std::string name;
  if (const auto v = std::get_if<tacky::Variable>(&val))
    name = v->name;
  else if (const auto t = std::get_if<tacky::Temporary>(&val))
    name = t->name;

  if (name.empty()) return "";

  if (stack_layout.contains(name)) {
    return name;
  }

  return get_or_alloc_variable(name);
}

std::string PIC18CodeGen::get_or_alloc_variable(const std::string &name) {
  if (!symbol_table.contains(name)) {
    symbol_table[name] = ram_head++;
  }
  return name;
}

int PIC18CodeGen::get_address(const std::string &operand) {
  if (operand.empty()) return -1;
  if (operand.size() > 2 && operand.substr(0, 2) == "0x") {
    try {
      return std::stoi(operand, nullptr, 16);
    } catch (...) {
      return -1;
    }
  }

  // Check in symbol table (globals)
  if (symbol_table.contains(operand)) {
    return symbol_table[operand];
  }

  // Check stack layout (locals/temporaries)
  if (stack_layout.contains(operand)) {
    // Stack base is 0x60 in our implementation
    return 0x60 + stack_layout[operand];
  }

  return -1;
}

void PIC18CodeGen::select_bank(const std::string &operand) {
  int addr = get_address(operand);
  if (addr == -1) return;

  // Access Bank check (0x00 - 0x5F and 0xF60 - 0xFFF)
  if (addr <= 0x5F || addr >= 0xF60) return;

  const int new_bank = (addr >> 8) & 0x0F;
  if (current_bank == new_bank) return;

  emit("MOVLB", std::format("{}", new_bank));
  current_bank = new_bank;
}

std::string PIC18CodeGen::get_access_mode(const std::string &operand) {
  int addr = get_address(operand);
  if (addr == -1) return "ACCESS";  // Default for registers like WREG, STATUS

  // Access Bank check (0x00 - 0x5F and 0xF60 - 0xFFF)
  if (addr <= 0x5F || addr >= 0xF60) return "ACCESS";

  return "BANKED";
}

void PIC18CodeGen::emit(const std::string &mnemonic) const {
  const_cast<PIC18CodeGen *>(this)->assembly.push_back(
      PIC18AsmLine::Instruction(mnemonic));
}

void PIC18CodeGen::emit(const std::string &mnemonic,
                        const std::string &op1) const {
  const_cast<PIC18CodeGen *>(this)->assembly.push_back(
      PIC18AsmLine::Instruction(mnemonic, op1));
}

void PIC18CodeGen::emit(const std::string &mnemonic, const std::string &op1,
                        const std::string &op2) const {
  const_cast<PIC18CodeGen *>(this)->assembly.push_back(
      PIC18AsmLine::Instruction(mnemonic, op1, op2));
}

void PIC18CodeGen::emit(const std::string &mnemonic, const std::string &op1,
                        const std::string &op2, const std::string &op3) const {
  const_cast<PIC18CodeGen *>(this)->assembly.push_back(
      PIC18AsmLine::Instruction(mnemonic, op1, op2, op3));
}

void PIC18CodeGen::emit_label(const std::string &label) const {
  const_cast<PIC18CodeGen *>(this)->assembly.push_back(
      PIC18AsmLine::Label(label));
  // Invalidate bank assumption at label because we may jump here from anywhere
  const_cast<PIC18CodeGen *>(this)->current_bank = -1;
}

void PIC18CodeGen::emit_comment(const std::string &comment) const {
  const_cast<PIC18CodeGen *>(this)->assembly.push_back(
      PIC18AsmLine::Comment(comment));
}

void PIC18CodeGen::emit_raw(const std::string &text) const {
  const_cast<PIC18CodeGen *>(this)->assembly.push_back(PIC18AsmLine::Raw(text));
}

void PIC18CodeGen::compile(const tacky::Program &program, std::ostream &os) {
  out = &os;
  assembly.clear();
  current_bank = -1;

  StackAllocator allocator;
  auto [offsets, total_size] = allocator.allocate(program);
  this->stack_layout = offsets;

  // 1. Compile functions first to populate symbol_table
  // We use a temporary vector to hold the function code so we can emit headers
  // first later
  for (const auto &func : program.functions) {
    compile_function(func);
  }
  std::vector<PIC18AsmLine> function_code = std::move(assembly);
  assembly.clear();

  // 2. Emit Headers
  std::string chip_file = config.chip;
  std::transform(chip_file.begin(), chip_file.end(), chip_file.begin(),
                 ::tolower);
  // Generate LIST directive (P=chip_model_upper_without_pic)
  std::string list_p = config.chip;
  if (list_p.size() > 3 && list_p.substr(0, 3) == "pic") {
    list_p = list_p.substr(3);
  }
  std::transform(list_p.begin(), list_p.end(), list_p.begin(), ::toupper);
  emit_raw(std::format("\tLIST P={}", list_p));

  // TODO: Add compatibility with other toolchains
  if (chip_file.find("pic") == 0) {
    chip_file = "p" + chip_file.substr(3);
  }
  emit_raw(std::format("\t#include <{}.inc>", chip_file));
  emit_config_directives();

  emit_raw("_stack_base EQU 0x060");
  for (const auto &[name, offset] : stack_layout) {
    emit_raw(std::format("{} EQU _stack_base + 0x{:03X}", name, offset));
  }

  // 3. Emit Global Variables (from symbol_table)
  for (const auto &[name, addr] : symbol_table) {
    emit_raw(std::format("{} EQU 0x{:03X}", name, addr));
  }

  emit_raw("    ORG 0x0000");
  emit("GOTO", "main");

  // 4. Append Function Code
  assembly.insert(assembly.end(), function_code.begin(), function_code.end());

  // Run Peephole Optimization
  auto optimized = PIC18Peephole::optimize(assembly);

  for (const auto &line : optimized) {
    os << line.to_string() << "\n";
  }

  os << "\tEND\n";
}

void PIC18CodeGen::emit_config_directives() {
  for (const auto &[key, val] : config.fuses) {
    emit_raw(std::format("\tCONFIG {} = {}", key, val));
  }
}

void PIC18CodeGen::compile_function(const tacky::Function &func) {
  emit_label(func.name);

  for (const auto &instr : func.body) {
    compile_instruction(instr);
  }
}

void PIC18CodeGen::load_into_w(const tacky::Val &val) {
  if (const auto c = std::get_if<tacky::Constant>(&val)) {
    emit("MOVLW", std::format("0x{:02X}", c->value & 0xFF));
  } else {
    std::string addr = resolve_address(val);
    select_bank(addr);
    emit("MOVF", addr, "W", get_access_mode(addr));
  }
}

void PIC18CodeGen::store_w_into(const tacky::Val &val) {
  std::string addr = resolve_address(val);
  select_bank(addr);
  emit("MOVWF", addr, get_access_mode(addr));
}

void PIC18CodeGen::compile_instruction(const tacky::Instruction &instr) {
  std::visit([this](auto &&arg) { compile_variant(arg); }, instr);
}

void PIC18CodeGen::compile_variant(const tacky::Return &arg) {
  if (!std::holds_alternative<std::monostate>(arg.value)) {
    load_into_w(arg.value);
  }
  emit("RETURN");
}

void PIC18CodeGen::compile_variant(const tacky::Jump &arg) const {
  emit("BRA", arg.target);
}

void PIC18CodeGen::compile_variant(const tacky::JumpIfZero &arg) {
  load_into_w(arg.condition);
  emit("ANDLW", "0xFF");  // Set Z flag
  emit("BZ", arg.target);
}

void PIC18CodeGen::compile_variant(const tacky::JumpIfNotZero &arg) {
  load_into_w(arg.condition);
  emit("ANDLW", "0xFF");  // Set Z flag
  emit("BNZ", arg.target);
}

void PIC18CodeGen::compile_variant(const tacky::Label &arg) const {
  emit_label(arg.name);
}

void PIC18CodeGen::compile_variant(const tacky::Call &arg) {
  emit("CALL", arg.function_name);
  if (!std::holds_alternative<std::monostate>(arg.dst)) {
    store_w_into(arg.dst);
  }
}

void PIC18CodeGen::compile_variant(const tacky::Copy &arg) {
  if (std::holds_alternative<tacky::Constant>(arg.src)) {
    load_into_w(arg.src);
    store_w_into(arg.dst);
  } else {
    std::string src = resolve_address(arg.src);
    std::string dst = resolve_address(arg.dst);
    // PIC18 has MOVFF for memory to memory
    emit("MOVFF", src, dst);
  }
}

void PIC18CodeGen::compile_variant(const tacky::Unary &arg) {
  load_into_w(arg.src);
  if (arg.op == tacky::UnaryOp::Neg) {
    emit("NEGF", "WREG", "ACCESS");
  } else if (arg.op == tacky::UnaryOp::Not) {
    emit("COMF", "WREG", "W", "ACCESS");
  } else if (arg.op == tacky::UnaryOp::BitNot) {
    emit("COMF", "WREG", "W", "ACCESS");
  }
  store_w_into(arg.dst);
}

void PIC18CodeGen::compile_variant(const tacky::Binary &arg) {
  // Rewriting Binary to be more robust
  if (arg.op == tacky::BinaryOp::Mul) {
    if (const auto c2 = std::get_if<tacky::Constant>(&arg.src2)) {
      load_into_w(arg.src1);
      emit("MOVLW", std::format("0x{:02X}", c2->value & 0xFF));
      emit("MULWF", "WREG", "ACCESS");
      emit("MOVF", "PRODL", "W", "ACCESS");
      store_w_into(arg.dst);
      return;
    } else {
      load_into_w(arg.src1);
      std::string right = resolve_address(arg.src2);
      select_bank(right);
      emit("MULWF", right, get_access_mode(right));
      emit("MOVF", "PRODL", "W", "ACCESS");
      store_w_into(arg.dst);
      return;
    }
  }

  load_into_w(arg.src1);
  std::string right = resolve_address(arg.src2);

  switch (arg.op) {
    case tacky::BinaryOp::Add:
      if (std::holds_alternative<tacky::Constant>(arg.src2)) {
        emit("ADDLW", right);
      } else {
        select_bank(right);
        emit("ADDWF", right, "W", get_access_mode(right));
      }
      break;
    case tacky::BinaryOp::Sub:
      if (auto c = std::get_if<tacky::Constant>(&arg.src2)) {
        // W = src1 - constant
        // ADDLW (0x100 - k) is effectively Sub literal
        emit("ADDLW", std::format("(0x100 - ({})) & 0xFF", right));
      } else if (auto c1 = std::get_if<tacky::Constant>(&arg.src1)) {
        // Literal - Variable (10 - x)
        // SUBLW k computes k - W.
        // Load Variable into W first.
        load_into_w(arg.src2);
        emit("SUBLW", std::format("0x{:02X}", c1->value & 0xFF));
      } else {
        // Variable - Variable
        // PIC SUBWF f, d: f - W -> d
        // We want W = src1 - src2
        load_into_w(arg.src2);
        std::string left = resolve_address(arg.src1);
        select_bank(left);
        emit("SUBWF", left, "W", get_access_mode(left));
      }
      // Enforce Storage:
      store_w_into(arg.dst);
      break;
    case tacky::BinaryOp::BitAnd:
      if (std::holds_alternative<tacky::Constant>(arg.src2)) {
        emit("ANDLW", right);
      } else {
        select_bank(right);
        emit("ANDWF", right, "W", get_access_mode(right));
      }
      break;
    case tacky::BinaryOp::BitOr:
      if (std::holds_alternative<tacky::Constant>(arg.src2)) {
        emit("IORLW", right);
      } else {
        select_bank(right);
        emit("IORWF", right, "W", get_access_mode(right));
      }
      break;
    case tacky::BinaryOp::BitXor:
      if (std::holds_alternative<tacky::Constant>(arg.src2)) {
        emit("XORLW", right);
      } else {
        select_bank(right);
        emit("XORWF", right, "W", get_access_mode(right));
      }
      break;
    case tacky::BinaryOp::LShift:
      if (const auto c2 = std::get_if<tacky::Constant>(&arg.src2)) {
        int amount = c2->value & 0x07;
        for (int i = 0; i < amount; ++i) {
          emit("BCF", "STATUS", "C", "ACCESS");
          emit("RLCF", "WREG", "F", "ACCESS");
        }
      } else {
        // Dynamic shift (loop)
        std::string lbl_loop = make_label("shift_loop");
        std::string lbl_end = make_label("shift_end");
        load_into_w(arg.src2);
        emit("BZ", lbl_end);
        std::string count = get_or_alloc_variable(make_label("shift_count"));
        emit("MOVWF", count, "ACCESS");
        load_into_w(arg.src1);
        emit_label(lbl_loop);
        emit("BCF", "STATUS", "C", "ACCESS");
        emit("RLCF", "WREG", "F", "ACCESS");
        emit("DECFSZ", count, "F", "ACCESS");
        emit("BRA", lbl_loop);
        emit_label(lbl_end);
      }
      break;
    case tacky::BinaryOp::RShift:
      if (const auto c2 = std::get_if<tacky::Constant>(&arg.src2)) {
        int amount = c2->value & 0x07;
        for (int i = 0; i < amount; ++i) {
          emit("BCF", "STATUS", "C", "ACCESS");
          emit("RRCF", "WREG", "F", "ACCESS");
        }
      } else {
        std::string lbl_loop = make_label("shift_loop");
        std::string lbl_end = make_label("shift_end");
        load_into_w(arg.src2);
        emit("BZ", lbl_end);
        std::string count = get_or_alloc_variable(make_label("shift_count"));
        emit("MOVWF", count, "ACCESS");
        load_into_w(arg.src1);
        emit_label(lbl_loop);
        emit("BCF", "STATUS", "C", "ACCESS");
        emit("RRCF", "WREG", "F", "ACCESS");
        emit("DECFSZ", count, "F", "ACCESS");
        emit("BRA", lbl_loop);
        emit_label(lbl_end);
      }
      break;
    case tacky::BinaryOp::Div:
    case tacky::BinaryOp::FloorDiv:
    case tacky::BinaryOp::Mod: {
      // Simple iterative division/modulo
      // result = src1 / src2, remainder = src1 % src2
      std::string quot = get_or_alloc_variable(make_label("div_quot"));
      std::string rem = get_or_alloc_variable(make_label("div_rem"));
      std::string divisor = get_or_alloc_variable(make_label("div_divisor"));

      std::string lbl_loop = make_label("div_loop");
      std::string lbl_end = make_label("div_end");

      load_into_w(arg.src2);
      emit("MOVWF", divisor, "ACCESS");
      load_into_w(arg.src1);
      emit("MOVWF", rem, "ACCESS");
      emit("CLRF", quot, "ACCESS");

      emit_label(lbl_loop);
      emit("MOVF", divisor, "W", "ACCESS");
      emit("SUBWF", rem, "W", "ACCESS");
      emit("BN", lbl_end);  // if rem < divisor, break

      emit("MOVWF", rem, "ACCESS");  // rem = rem - divisor
      emit("INCF", quot, "F", "ACCESS");
      emit("BRA", lbl_loop);

      emit_label(lbl_end);
      if (arg.op == tacky::BinaryOp::Div || arg.op == tacky::BinaryOp::FloorDiv) {
        emit("MOVF", quot, "W", "ACCESS");
      } else {
        emit("MOVF", rem, "W", "ACCESS");
      }
      break;
    }
    case tacky::BinaryOp::Equal:
    case tacky::BinaryOp::NotEqual:
    case tacky::BinaryOp::LessThan:
    case tacky::BinaryOp::LessEqual:
    case tacky::BinaryOp::GreaterThan:
    case tacky::BinaryOp::GreaterEqual: {
      bool optimize_zero = false;
      if (const auto c = std::get_if<tacky::Constant>(&arg.src2)) {
        if (c->value == 0) {
          optimize_zero = true;
        }
      }

      std::string left = resolve_address(arg.src1);

      if (optimize_zero) {
        select_bank(left);
        emit("MOVF", left, "F", get_access_mode(left));
      } else {
        load_into_w(arg.src2);
        select_bank(left);
        emit("SUBWF", left, "W", get_access_mode(left));
      }

      if (!optimize_zero) {
        emit("CLRF", "WREG", "ACCESS");
      } else {
        emit("CLRF", "WREG", "ACCESS");
      }

      std::string lbl_comp_false = make_label("comp_false");
      std::string lbl_comp_true = make_label("comp_true");

      switch (arg.op) {
        case tacky::BinaryOp::Equal:
          emit("BTFSC", "STATUS", "Z", "ACCESS");
          break;
        case tacky::BinaryOp::NotEqual:
          emit("BTFSS", "STATUS", "Z", "ACCESS");
          break;
        case tacky::BinaryOp::LessThan:
          emit("BTFSS", "STATUS", "C", "ACCESS");
          break;
        case tacky::BinaryOp::GreaterEqual:
          emit("BTFSC", "STATUS", "C", "ACCESS");
          break;
        case tacky::BinaryOp::GreaterThan: {
          // C=1 and Z=0
          emit("BTFSS", "STATUS", "C", "ACCESS");
          emit("BRA", lbl_comp_false);
          emit("BTFSS", "STATUS", "Z", "ACCESS");
          emit("MOVLW", "1");
          goto comp_emit_label;
        }
        case tacky::BinaryOp::LessEqual: {
          // C=0 or Z=1
          emit("BTFSC", "STATUS", "Z", "ACCESS");
          emit("BRA", lbl_comp_true);
          emit("BTFSC", "STATUS", "C", "ACCESS");
          emit("BRA", lbl_comp_false);
          emit_label(lbl_comp_true);
          emit("MOVLW", "1");
          goto comp_emit_label;
        }
        default:
          break;
      }
      emit("MOVLW", "1");

    comp_emit_label:
      emit_label(lbl_comp_false);
      break;
    }
    default:
      break;
  }
  store_w_into(arg.dst);
}

void PIC18CodeGen::compile_variant(const tacky::BitSet &arg) {
  std::string addr = resolve_address(arg.target);
  select_bank(addr);
  emit("BSF", addr, std::format("{}", arg.bit), get_access_mode(addr));
}

void PIC18CodeGen::compile_variant(const tacky::BitClear &arg) {
  std::string addr = resolve_address(arg.target);
  select_bank(addr);
  emit("BCF", addr, std::format("{}", arg.bit), get_access_mode(addr));
}

void PIC18CodeGen::compile_variant(const tacky::BitCheck &arg) {
  std::string addr = resolve_address(arg.source);
  select_bank(addr);
  emit("CLRF", "WREG", "ACCESS");
  emit("BTFSC", addr, std::format("{}", arg.bit), get_access_mode(addr));
  emit("MOVLW", "1");
  store_w_into(arg.dst);
}

void PIC18CodeGen::compile_variant(const tacky::BitWrite &arg) {
  std::string addr = resolve_address(arg.target);
  select_bank(addr);
  load_into_w(arg.src);
  emit("TSTFSZ", "WREG", "ACCESS");
  emit("BRA", make_label("set_bit"));
  emit("BCF", addr, std::format("{}", arg.bit), get_access_mode(addr));
  emit("BRA", make_label("end_bit_write"));
  emit_label(make_label("set_bit"));
  emit("BSF", addr, std::format("{}", arg.bit), get_access_mode(addr));
  emit_label(make_label("end_bit_write"));
}

void PIC18CodeGen::compile_variant(const tacky::JumpIfBitSet &arg) {
  std::string addr = resolve_address(arg.source);
  select_bank(addr);
  // BTFSC: skip next if bit is CLEAR → execute BRA only when SET
  emit("BTFSC", addr, std::format("{}", arg.bit), get_access_mode(addr));
  emit("BRA", arg.target);
}

void PIC18CodeGen::compile_variant(const tacky::JumpIfBitClear &arg) {
  std::string addr = resolve_address(arg.source);
  select_bank(addr);
  // BTFSS: skip next if bit is SET → execute BRA only when CLEAR
  emit("BTFSS", addr, std::format("{}", arg.bit), get_access_mode(addr));
  emit("BRA", arg.target);
}

void PIC18CodeGen::compile_variant(const tacky::AugAssign &arg) {
  throw std::runtime_error("PIC18: AugAssign not implemented");
}


void PIC18CodeGen::compile_variant(const tacky::LoadIndirect &) {
  throw std::runtime_error("PIC18: LoadIndirect (pointer dereference) is not yet implemented");
}

void PIC18CodeGen::compile_variant(const tacky::StoreIndirect &) {
  throw std::runtime_error("PIC18: StoreIndirect (pointer dereference) is not yet implemented");
}

void PIC18CodeGen::compile_variant(const tacky::InlineAsm &arg) {
  assembly.push_back(PIC18AsmLine::Raw(arg.instruction));
}

void PIC18CodeGen::compile_variant(const tacky::DebugLine &arg) {
  if (!arg.source_file.empty()) {
    emit_comment(std::format("{}:{}: {}", arg.source_file, arg.line, arg.text));
  } else {
    emit_comment(std::format("Line {}: {}", arg.line, arg.text));
  }
}

void PIC18CodeGen::compile_variant(const tacky::JumpIfEqual &arg) {
  if (const auto c2 = std::get_if<tacky::Constant>(&arg.src2)) {
    if (c2->value >= 0 && c2->value <= 255) {
      std::string src1 = resolve_address(arg.src1);
      select_bank(src1);
      emit("MOVF", src1, "W", get_access_mode(src1));
      emit("XORLW", std::format("0x{:02X}", c2->value));
      emit("BTFSC", "STATUS", "Z", "ACCESS");
      emit("BRA", arg.target);
      return;
    }
  }
  load_into_w(arg.src2);
  std::string src1 = resolve_address(arg.src1);
  select_bank(src1);
  emit("SUBWF", src1, "W", get_access_mode(src1));
  emit("BTFSC", "STATUS", "Z", "ACCESS");
  emit("BRA", arg.target);
}

void PIC18CodeGen::compile_variant(const tacky::JumpIfNotEqual &arg) {
  if (const auto c2 = std::get_if<tacky::Constant>(&arg.src2)) {
    if (c2->value >= 0 && c2->value <= 255) {
      std::string src1 = resolve_address(arg.src1);
      select_bank(src1);
      emit("MOVF", src1, "W", get_access_mode(src1));
      emit("XORLW", std::format("0x{:02X}", c2->value));
      emit("BTFSS", "STATUS", "Z", "ACCESS");
      emit("BRA", arg.target);
      return;
    }
  }
  load_into_w(arg.src2);
  std::string src1 = resolve_address(arg.src1);
  select_bank(src1);
  emit("SUBWF", src1, "W", get_access_mode(src1));
  emit("BTFSS", "STATUS", "Z", "ACCESS");
  emit("BRA", arg.target);
}

void PIC18CodeGen::compile_variant(const tacky::JumpIfLessThan &arg) {
  if (const auto c2 = std::get_if<tacky::Constant>(&arg.src2)) {
    int k = c2->value;
    if (k >= 0 && k <= 255) {
      if (k == 0) return;
      std::string src1 = resolve_address(arg.src1);
      select_bank(src1);
      emit("MOVF", src1, "W", get_access_mode(src1));
      emit("SUBLW", std::format("0x{:02X}", k - 1));
      emit("BTFSC", "STATUS", "C", "ACCESS");
      emit("BRA", arg.target);
      return;
    }
  }
  load_into_w(arg.src2);
  std::string src1 = resolve_address(arg.src1);
  select_bank(src1);
  emit("SUBWF", src1, "W", get_access_mode(src1));  // src1 - A(W)
  emit("BTFSS", "STATUS", "C", "ACCESS");
  emit("BRA", arg.target);
}

void PIC18CodeGen::compile_variant(const tacky::JumpIfLessOrEqual &arg) {
  if (const auto c2 = std::get_if<tacky::Constant>(&arg.src2)) {
    int k = c2->value;
    if (k >= 0 && k <= 255) {
      std::string src1 = resolve_address(arg.src1);
      select_bank(src1);
      emit("MOVF", src1, "W", get_access_mode(src1));
      emit("SUBLW", std::format("0x{:02X}", k));
      emit("BTFSC", "STATUS", "C", "ACCESS");
      emit("BRA", arg.target);
      return;
    }
  }
  load_into_w(arg.src1);
  std::string src2 = resolve_address(arg.src2);
  select_bank(src2);
  emit("SUBWF", src2, "W", get_access_mode(src2));  // src2 - src1
  emit("BTFSC", "STATUS", "C", "ACCESS");
  emit("BRA", arg.target);
}

void PIC18CodeGen::compile_variant(const tacky::JumpIfGreaterThan &arg) {
  if (const auto c2 = std::get_if<tacky::Constant>(&arg.src2)) {
    int k = c2->value;
    if (k >= 0 && k <= 255) {
      std::string src1 = resolve_address(arg.src1);
      select_bank(src1);
      emit("MOVF", src1, "W", get_access_mode(src1));
      emit("SUBLW", std::format("0x{:02X}", k));
      emit("BTFSS", "STATUS", "C", "ACCESS");
      emit("BRA", arg.target);
      return;
    }
  }
  load_into_w(arg.src1);
  std::string src2 = resolve_address(arg.src2);
  select_bank(src2);
  emit("SUBWF", src2, "W", get_access_mode(src2));  // src2 - src1
  emit("BTFSS", "STATUS", "C", "ACCESS");
  emit("BRA", arg.target);
}

void PIC18CodeGen::compile_variant(const tacky::JumpIfGreaterOrEqual &arg) {
  if (const auto c2 = std::get_if<tacky::Constant>(&arg.src2)) {
    int k = c2->value;
    if (k >= 0 && k <= 255) {
      if (k == 0) {
        emit("BRA", arg.target);
        return;
      }
      std::string src1 = resolve_address(arg.src1);
      select_bank(src1);
      emit("MOVF", src1, "W", get_access_mode(src1));
      emit("SUBLW", std::format("0x{:02X}", k - 1));
      emit("BTFSS", "STATUS", "C", "ACCESS");
      emit("BRA", arg.target);
      return;
    }
  }
  load_into_w(arg.src2);
  std::string src1 = resolve_address(arg.src1);
  select_bank(src1);
  emit("SUBWF", src1, "W", get_access_mode(src1));
  emit("BTFSC", "STATUS", "C", "ACCESS");
  emit("BRA", arg.target);
}