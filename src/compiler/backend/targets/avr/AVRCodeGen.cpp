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

#include "AVRCodeGen.h"

#include <algorithm>
#include <format>
#include <iostream>
#include <map>
#include <utility>
#include <variant>

#include "backend/analysis/StackAllocator.h"

AVRCodeGen::AVRCodeGen(DeviceConfig cfg)
    : config(std::move(cfg)), out(nullptr) {
  label_counter = 0;
}

std::string AVRCodeGen::resolve_address(const tacky::Val &val) {
  if (const auto c = std::get_if<tacky::Constant>(&val)) {
    return std::format("{}", c->value & 0xFF);
  }
  if (const auto mem = std::get_if<tacky::MemoryAddress>(&val)) {
    return std::format("0x{:04X}", mem->address);
  }

  std::string name;
  if (const auto v = std::get_if<tacky::Variable>(&val))
    name = v->name;
  else if (const auto t = std::get_if<tacky::Temporary>(&val))
    name = t->name;

  return name;
}

DataType AVRCodeGen::get_val_type(const tacky::Val &val) const {
  if (const auto v = std::get_if<tacky::Variable>(&val)) return v->type;
  if (const auto t = std::get_if<tacky::Temporary>(&val)) return t->type;
  if (const auto m = std::get_if<tacky::MemoryAddress>(&val)) return m->type;
  // Constants default to UINT8 unless value exceeds 8-bit range
  if (const auto c = std::get_if<tacky::Constant>(&val)) {
      if (c->value > 255 || c->value < -128) return DataType::UINT16;
  }
  return DataType::UINT8;
}

std::string AVRCodeGen::get_high_reg(const std::string &reg) const {
  // Simple mapping: R16 -> R17, R24 -> R25, etc.
  int reg_num = std::stoi(reg.substr(1));
  return "R" + std::to_string(reg_num + 1);
}

void AVRCodeGen::emit(const std::string &mnemonic) const {
  const_cast<AVRCodeGen *>(this)->assembly.push_back(
      AVRAsmLine::Instruction(mnemonic));
}

void AVRCodeGen::emit(const std::string &mnemonic,
                      const std::string &op1) const {
  const_cast<AVRCodeGen *>(this)->assembly.push_back(
      AVRAsmLine::Instruction(mnemonic, op1));
}

void AVRCodeGen::emit(const std::string &mnemonic, const std::string &op1,
                      const std::string &op2) const {
  const_cast<AVRCodeGen *>(this)->assembly.push_back(
      AVRAsmLine::Instruction(mnemonic, op1, op2));
}

void AVRCodeGen::emit_label(const std::string &label) const {
  const_cast<AVRCodeGen *>(this)->assembly.push_back(AVRAsmLine::Label(label));
}

void AVRCodeGen::emit_comment(const std::string &comment) const {
  const_cast<AVRCodeGen *>(this)->assembly.push_back(
      AVRAsmLine::Comment(comment));
}

void AVRCodeGen::emit_raw(const std::string &text) const {
  const_cast<AVRCodeGen *>(this)->assembly.push_back(AVRAsmLine::Raw(text));
}

void AVRCodeGen::load_into_reg(const tacky::Val &val, const std::string &reg, DataType type) {
  bool is_16bit = (size_of(type) == 2);
  std::string reg_h = is_16bit ? get_high_reg(reg) : "";

  if (const auto c = std::get_if<tacky::Constant>(&val)) {
    emit("LDI", reg, std::format("{}", c->value & 0xFF));
    if (is_16bit) {
      emit("LDI", reg_h, std::format("{}", (c->value >> 8) & 0xFF));
    }
  } else if (const auto mem = std::get_if<tacky::MemoryAddress>(&val)) {
    if (mem->address >= 0x20 && mem->address <= 0x5F) {
      emit("IN", reg, std::format("0x{:02X}", mem->address - 0x20));
      if (is_16bit) {
         // MMIO 16-bit access usually requires specific ordering (Low then High for write, Low then High for read? Check datasheet)
         // For standard memory mapped registers:
         emit("LDS", reg_h, std::format("0x{:04X}", mem->address + 1));
      }
    } else {
      emit("LDS", reg, std::format("0x{:04X}", mem->address));
      if (is_16bit) {
        emit("LDS", reg_h, std::format("0x{:04X}", mem->address + 1));
      }
    }
  } else {
    std::string name;
    if (const auto v = std::get_if<tacky::Variable>(&val))
      name = v->name;
    else if (const auto t = std::get_if<tacky::Temporary>(&val))
      name = t->name;

    if (!name.empty() && stack_layout.contains(name)) {
      int offset = stack_layout.at(name);
      if (offset < 64) {
        emit("LDD", reg, std::format("Y+{}", offset));
        if (is_16bit) {
          emit("LDD", reg_h, std::format("Y+{}", offset + 1));
        }
        return;
      }
    }

    std::string addr = resolve_address(val);
    if (!addr.empty()) {
      emit("LDS", reg, addr);
      if (is_16bit) {
        emit("LDS", reg_h, addr + "+1");
      }
    }
  }
}

void AVRCodeGen::store_reg_into(const std::string &reg, const tacky::Val &val, DataType type) {
  if (std::holds_alternative<tacky::Constant>(val)) {
    return;
  }

  bool is_16bit = (size_of(type) == 2);
  std::string reg_h = is_16bit ? get_high_reg(reg) : "";

  if (const auto mem = std::get_if<tacky::MemoryAddress>(&val)) {
    if (mem->address >= 0x20 && mem->address <= 0x5F) {
      emit("OUT", std::format("0x{:02X}", mem->address - 0x20), reg);
      if (is_16bit) {
         // For 16-bit IO registers, high byte usually written first?
         // Assuming standard memory mapped for now.
         emit("STS", std::format("0x{:04X}", mem->address + 1), reg_h);
      }
    } else {
      emit("STS", std::format("0x{:04X}", mem->address), reg);
      if (is_16bit) {
        emit("STS", std::format("0x{:04X}", mem->address + 1), reg_h);
      }
    }
  } else {
    std::string name;
    if (const auto v = std::get_if<tacky::Variable>(&val))
      name = v->name;
    else if (const auto t = std::get_if<tacky::Temporary>(&val))
      name = t->name;

    if (!name.empty() && stack_layout.contains(name)) {
      int offset = stack_layout.at(name);
      if (offset < 64) {
        emit("STD", std::format("Y+{}", offset), reg);
        if (is_16bit) {
          emit("STD", std::format("Y+{}", offset + 1), reg_h);
        }
        return;
      }
    }

    std::string addr = resolve_address(val);
    if (!addr.empty()) {
      emit("STS", addr, reg);
      if (is_16bit) {
        emit("STS", addr + "+1", reg_h);
      }
    }
  }
}

void AVRCodeGen::compile(const tacky::Program &program, std::ostream &os) {
  out = &os;
  assembly.clear();

  StackAllocator allocator;
  auto [offsets, total_size] = allocator.allocate(program);
  this->stack_layout = offsets;

  emit_comment("Generated by pymcuc for " + config.chip);
  emit_raw(".device " + config.chip);

  // Stack and Memory setup
  emit_raw(".equ RAMSTART = 0x0100");
  emit_raw(std::format(".equ _stack_base = RAMSTART"));

  for (const auto &[name, offset] : stack_layout) {
    emit_raw(std::format(".equ {} = _stack_base + {}", name, offset));
  }

  emit_raw("");

  // Build ISR map: vector_address -> function*
  std::map<int, const tacky::Function *> isr_map;
  for (const auto &func : program.functions) {
    if (func.is_interrupt) {
      if (isr_map.contains(func.interrupt_vector)) {
        throw std::runtime_error(std::format(
            "Multiple ISRs defined for vector 0x{:04X}",
            func.interrupt_vector));
      }
      isr_map[func.interrupt_vector] = &func;
    }
  }

  // Emit interrupt vector table
  emit_raw(".org 0x0000");
  emit("RJMP", "main");

  if (!isr_map.empty()) {
    // ATmega328P: 26 vectors at 2-byte intervals (0x0000-0x0032)
    for (int vec = 1; vec <= 25; vec++) {
      int addr = vec * 2;
      emit_raw(std::format(".org 0x{:04X}", addr));
      if (isr_map.contains(addr)) {
        emit("RJMP", isr_map[addr]->name);
      } else {
        emit("RETI");
      }
    }
    emit_raw("");
  }

  // Emit ISR functions first
  for (const auto &func : program.functions) {
    if (func.is_interrupt) {
      compile_function(func);
    }
  }

  // Emit regular functions
  for (const auto &func : program.functions) {
    if (func.is_interrupt) continue;
    if (func.is_inline && func.name != "main") continue;
    compile_function(func);
  }

  // Apply peephole optimizations
  auto optimized = AVRPeephole::optimize(assembly);
  for (const auto &line : optimized) {
    os << line.to_string() << "\n";
  }
}

void AVRCodeGen::compile_function(const tacky::Function &func) {
  emit_label(func.name);

  if (func.is_interrupt) {
    // ISR prologue: save context
    emit_comment("ISR prologue — save context");
    emit("PUSH", "R16");
    emit("PUSH", "R17");
    emit("PUSH", "R18");
    emit("IN", "R16", "0x3F");  // Save SREG
    emit("PUSH", "R16");
  }

  if (func.name == "main") {
    // Initialize Stack Pointer for ATmega328P
    emit("LDI", "R16", "high(0x08FF)");  // RAMEND for ATmega328P is 0x08FF
    emit("OUT", "0x3E", "R16");          // SPH
    emit("LDI", "R16", "low(0x08FF)");
    emit("OUT", "0x3D", "R16");  // SPL

    // Initialize Y (R28:R29) to point to _stack_base
    emit("LDI", "R28", "low(_stack_base)");
    emit("LDI", "R29", "high(_stack_base)");
  }

  for (const auto &instr : func.body) {
    if (func.is_interrupt && std::holds_alternative<tacky::Return>(instr)) {
      // ISR epilogue: restore context + RETI
      emit_comment("ISR epilogue — restore context");
      emit("POP", "R16");
      emit("OUT", "0x3F", "R16");  // Restore SREG
      emit("POP", "R18");
      emit("POP", "R17");
      emit("POP", "R16");
      emit("RETI");
      continue;
    }
    compile_instruction(instr);
  }

  // Implicit return for ISR if no explicit return
  if (func.is_interrupt) {
    emit_comment("ISR epilogue — restore context");
    emit("POP", "R16");
    emit("OUT", "0x3F", "R16");
    emit("POP", "R18");
    emit("POP", "R17");
    emit("POP", "R16");
    emit("RETI");
  }
}

void AVRCodeGen::compile_instruction(const tacky::Instruction &instr) {
  std::visit([this](auto &&arg) { compile_variant(arg); }, instr);
}

void AVRCodeGen::compile_variant(const tacky::Return &arg) {
  if (!std::holds_alternative<std::monostate>(arg.value)) {
    DataType type = get_val_type(arg.value);
    load_into_reg(arg.value, "R24", type);  // R24 or R25:R24
  }
  emit("RET");
}

void AVRCodeGen::compile_variant(const tacky::Jump &arg) const {
  emit("RJMP", arg.target);
}

void AVRCodeGen::compile_variant(const tacky::JumpIfZero &arg) {
  DataType type = get_val_type(arg.condition);
  load_into_reg(arg.condition, "R24", type);

  if (size_of(type) == 2) {
      emit("OR", "R24", "R25"); // Combine low and high
      emit("BREQ", arg.target);
  } else {
      emit("TST", "R24");
      emit("BREQ", arg.target);
  }
}

void AVRCodeGen::compile_variant(const tacky::JumpIfNotZero &arg) {
  DataType type = get_val_type(arg.condition);
  load_into_reg(arg.condition, "R24", type);

  if (size_of(type) == 2) {
      emit("OR", "R24", "R25");
      emit("BRNE", arg.target);
  } else {
      emit("TST", "R24");
      emit("BRNE", arg.target);
  }
}

void AVRCodeGen::compile_variant(const tacky::Label &arg) const {
  emit_label(arg.name);
}

void AVRCodeGen::compile_variant(const tacky::DebugLine &arg) {
  if (!arg.source_file.empty()) {
    emit_comment(std::format("{}:{}: {}", arg.source_file, arg.line, arg.text));
  } else {
    emit_comment(std::format("Line {}: {}", arg.line, arg.text));
  }
}

void AVRCodeGen::compile_variant(const tacky::JumpIfEqual &arg) {
  DataType type = get_val_type(arg.src1);
  load_into_reg(arg.src1, "R24", type);

  if (const auto c = std::get_if<tacky::Constant>(&arg.src2)) {
    int val = c->value;
    if (size_of(type) == 2) {
        emit("CPI", "R24", std::format("{}", val & 0xFF));
        emit("CPC", "R25", std::format("{}", (val >> 8) & 0xFF)); // CPC with constant? No, CPI is only for R16-R31
        // AVR CPI only works on high registers. R24/R25 are high.
        // But CPC with immediate is not supported directly.
        // We need to load constant into register for 16-bit compare if not 0.
        // Actually, we can use CPI for low byte, then CPC for high byte? No, CPC is Reg, Reg.
        // SBCI is Reg, Imm. But we want Compare.
        // Standard way: Load constant into R18:R19
        emit("LDI", "R18", std::format("{}", val & 0xFF));
        emit("LDI", "R19", std::format("{}", (val >> 8) & 0xFF));
        emit("CP", "R24", "R18");
        emit("CPC", "R25", "R19");
    } else {
        emit("CPI", "R24", std::format("{}", val & 0xFF));
    }
  } else {
    load_into_reg(arg.src2, "R18", type);
    emit("CP", "R24", "R18");
    if (size_of(type) == 2) {
        emit("CPC", "R25", "R19");
    }
  }
  emit("BREQ", arg.target);
}

void AVRCodeGen::compile_variant(const tacky::JumpIfNotEqual &arg) {
  DataType type = get_val_type(arg.src1);
  load_into_reg(arg.src1, "R24", type);

  if (const auto c = std::get_if<tacky::Constant>(&arg.src2)) {
    int val = c->value;
    if (size_of(type) == 2) {
        emit("LDI", "R18", std::format("{}", val & 0xFF));
        emit("LDI", "R19", std::format("{}", (val >> 8) & 0xFF));
        emit("CP", "R24", "R18");
        emit("CPC", "R25", "R19");
    } else {
        emit("CPI", "R24", std::format("{}", val & 0xFF));
    }
  } else {
    load_into_reg(arg.src2, "R18", type);
    emit("CP", "R24", "R18");
    if (size_of(type) == 2) {
        emit("CPC", "R25", "R19");
    }
  }
  emit("BRNE", arg.target);
}

void AVRCodeGen::compile_variant(const tacky::JumpIfLessThan &arg) {
  DataType type = get_val_type(arg.src1);
  load_into_reg(arg.src1, "R24", type);

  if (const auto c = std::get_if<tacky::Constant>(&arg.src2)) {
    int val = c->value;
    if (size_of(type) == 2) {
        emit("LDI", "R18", std::format("{}", val & 0xFF));
        emit("LDI", "R19", std::format("{}", (val >> 8) & 0xFF));
        emit("CP", "R24", "R18");
        emit("CPC", "R25", "R19");
    } else {
        emit("CPI", "R24", std::format("{}", val & 0xFF));
    }
  } else {
    load_into_reg(arg.src2, "R18", type);
    emit("CP", "R24", "R18");
    if (size_of(type) == 2) {
        emit("CPC", "R25", "R19");
    }
  }
  emit("BRLO", arg.target);
}

void AVRCodeGen::compile_variant(const tacky::JumpIfLessOrEqual &arg) {
  // A <= B is equivalent to !(B < A) or using BRLS (Unsigned Lower or Same)
  // CP A, B
  DataType type = get_val_type(arg.src1);
  load_into_reg(arg.src1, "R24", type);

  if (const auto c = std::get_if<tacky::Constant>(&arg.src2)) {
    int val = c->value;
    if (size_of(type) == 2) {
        emit("LDI", "R18", std::format("{}", val & 0xFF));
        emit("LDI", "R19", std::format("{}", (val >> 8) & 0xFF));
        emit("CP", "R24", "R18");
        emit("CPC", "R25", "R19");
    } else {
        emit("CPI", "R24", std::format("{}", val & 0xFF));
    }
  } else {
    load_into_reg(arg.src2, "R18", type);
    emit("CP", "R24", "R18");
    if (size_of(type) == 2) {
        emit("CPC", "R25", "R19");
    }
  }
  // BRLS: Branch if Lower or Same (Unsigned <=)
  emit("BRLS", arg.target);
}

void AVRCodeGen::compile_variant(const tacky::JumpIfGreaterThan &arg) {
  DataType type = get_val_type(arg.src1);
  load_into_reg(arg.src1, "R24", type);

  if (const auto c = std::get_if<tacky::Constant>(&arg.src2)) {
    int val = c->value;
    if (size_of(type) == 2) {
        emit("LDI", "R18", std::format("{}", val & 0xFF));
        emit("LDI", "R19", std::format("{}", (val >> 8) & 0xFF));
        emit("CP", "R24", "R18");
        emit("CPC", "R25", "R19");
    } else {
        emit("CPI", "R24", std::format("{}", val & 0xFF));
    }
  } else {
    load_into_reg(arg.src2, "R18", type);
    emit("CP", "R24", "R18");
    if (size_of(type) == 2) {
        emit("CPC", "R25", "R19");
    }
  }
  emit("BRHI", arg.target);
}

void AVRCodeGen::compile_variant(const tacky::JumpIfGreaterOrEqual &arg) {
  DataType type = get_val_type(arg.src1);
  load_into_reg(arg.src1, "R24", type);

  if (const auto c = std::get_if<tacky::Constant>(&arg.src2)) {
    int val = c->value;
    if (size_of(type) == 2) {
        emit("LDI", "R18", std::format("{}", val & 0xFF));
        emit("LDI", "R19", std::format("{}", (val >> 8) & 0xFF));
        emit("CP", "R24", "R18");
        emit("CPC", "R25", "R19");
    } else {
        emit("CPI", "R24", std::format("{}", val & 0xFF));
    }
  } else {
    load_into_reg(arg.src2, "R18", type);
    emit("CP", "R24", "R18");
    if (size_of(type) == 2) {
        emit("CPC", "R25", "R19");
    }
  }
  emit("BRSH", arg.target);
}

void AVRCodeGen::compile_variant(const tacky::Call &arg) {
  // For now, simple call without parameters/return value handling here
  emit("RCALL", arg.function_name);
  // Return value is in R24 (or R25:R24)
  DataType dst_type = get_val_type(arg.dst);
  store_reg_into("R24", arg.dst, dst_type);
}

void AVRCodeGen::compile_variant(const tacky::Copy &arg) {
  DataType type = get_val_type(arg.dst);
  load_into_reg(arg.src, "R24", type);
  store_reg_into("R24", arg.dst, type);
}

void AVRCodeGen::compile_variant(const tacky::LoadIndirect &arg) {
  // Load pointer into X (R26:R27)
  load_into_reg(arg.src_ptr, "R26", DataType::UINT16);

  DataType dst_type = get_val_type(arg.dst);
  if (size_of(dst_type) == 2) {
      emit("LD", "R24", "X+");
      emit("LD", "R25", "X");
  } else {
      emit("LD", "R24", "X");
  }
  store_reg_into("R24", arg.dst, dst_type);
}

void AVRCodeGen::compile_variant(const tacky::StoreIndirect &arg) {
  // Load pointer into X (R26:R27)
  load_into_reg(arg.dst_ptr, "R26", DataType::UINT16);

  DataType src_type = get_val_type(arg.src);
  load_into_reg(arg.src, "R24", src_type);

  if (size_of(src_type) == 2) {
      emit("ST", "X+", "R24");
      emit("ST", "X", "R25");
  } else {
      emit("ST", "X", "R24");
  }
}

void AVRCodeGen::compile_variant(const tacky::Unary &arg) {
  DataType type = get_val_type(arg.dst);
  load_into_reg(arg.src, "R24", type);

  switch (arg.op) {
    case tacky::UnaryOp::Neg:
      emit("NEG", "R24");
      if (size_of(type) == 2) {
          emit("COM", "R25");
          emit("ADC", "R25", "R1"); // R1 is zero
      }
      break;
    case tacky::UnaryOp::BitNot:
      emit("COM", "R24");
      if (size_of(type) == 2) {
          emit("COM", "R25");
      }
      break;
    case tacky::UnaryOp::Not: {
      // Logical NOT
      std::string l_true = make_label("L_NOT_TRUE");
      std::string l_done = make_label("L_NOT_DONE");
      if (size_of(type) == 2) emit("OR", "R24", "R25");
      emit("TST", "R24");
      emit("LDI", "R24", "0");
      if (size_of(type) == 2) emit("LDI", "R25", "0");
      emit("BREQ", l_true);
      emit("RJMP", l_done);
      emit_label(l_true);
      emit("LDI", "R24", "1");
      emit_label(l_done);
    } break;
  }
  store_reg_into("R24", arg.dst, type);
}

void AVRCodeGen::compile_variant(const tacky::Binary &arg) {
  DataType type = get_val_type(arg.dst);
  load_into_reg(arg.src1, "R24", type);

  bool used_immediate = false;
  if (const auto c = std::get_if<tacky::Constant>(&arg.src2)) {
    int val = c->value;
    // Only optimize 8-bit immediates for now, or handle 16-bit immediates carefully
    if (size_of(type) == 1) {
        switch (arg.op) {
          case tacky::BinaryOp::Add:
            if (val == 1) emit("INC", "R24");
            else if (val == 255) emit("DEC", "R24");
            else emit("SUBI", "R24", std::format("{}", (unsigned char)-val));
            used_immediate = true;
            break;
          case tacky::BinaryOp::Sub:
            if (val == 1) emit("DEC", "R24");
            else if (val == 255) emit("INC", "R24");
            else emit("SUBI", "R24", std::format("{}", val & 0xFF));
            used_immediate = true;
            break;
          case tacky::BinaryOp::BitAnd:
            emit("ANDI", "R24", std::format("{}", val & 0xFF));
            used_immediate = true;
            break;
          case tacky::BinaryOp::BitOr:
            emit("ORI", "R24", std::format("{}", val & 0xFF));
            used_immediate = true;
            break;
          case tacky::BinaryOp::LShift:
            for (int i = 0; i < (val & 7); ++i) emit("LSL", "R24");
            used_immediate = true;
            break;
          case tacky::BinaryOp::RShift:
            for (int i = 0; i < (val & 7); ++i) emit("LSR", "R24");
            used_immediate = true;
            break;
          default:
            break;
        }
    } else {
        // 16-bit immediate optimizations
        switch (arg.op) {
            case tacky::BinaryOp::Add:
                emit("SUBI", "R24", std::format("{}", (unsigned char)-(val & 0xFF)));
                emit("SBCI", "R25", std::format("{}", (unsigned char)-((val >> 8) & 0xFF)));
                used_immediate = true;
                break;
            case tacky::BinaryOp::Sub:
                emit("SUBI", "R24", std::format("{}", val & 0xFF));
                emit("SBCI", "R25", std::format("{}", (val >> 8) & 0xFF));
                used_immediate = true;
                break;
            default:
                break;
        }
    }
  }

  if (!used_immediate) {
    load_into_reg(arg.src2, "R18", type);
  }

  switch (arg.op) {
    case tacky::BinaryOp::Add:
      if (!used_immediate) {
          emit("ADD", "R24", "R18");
          if (size_of(type) == 2) emit("ADC", "R25", "R19");
      }
      break;
    case tacky::BinaryOp::Sub:
      if (!used_immediate) {
          emit("SUB", "R24", "R18");
          if (size_of(type) == 2) emit("SBC", "R25", "R19");
      }
      break;
    case tacky::BinaryOp::BitAnd:
      if (!used_immediate) {
          emit("AND", "R24", "R18");
          if (size_of(type) == 2) emit("AND", "R25", "R19");
      }
      break;
    case tacky::BinaryOp::BitOr:
      if (!used_immediate) {
          emit("OR", "R24", "R18");
          if (size_of(type) == 2) emit("OR", "R25", "R19");
      }
      break;
    case tacky::BinaryOp::BitXor:
      emit("EOR", "R24", "R18");
      if (size_of(type) == 2) emit("EOR", "R25", "R19");
      break;
    case tacky::BinaryOp::LShift:
      if (!used_immediate) {
        // Loop shift
        std::string l_start = make_label("L_SHIFT_START");
        std::string l_done = make_label("L_SHIFT_DONE");
        emit_label(l_start);
        emit("TST", "R18");
        emit("BREQ", l_done);
        emit("LSL", "R24");
        if (size_of(type) == 2) emit("ROL", "R25");
        emit("DEC", "R18");
        emit("RJMP", l_start);
        emit_label(l_done);
      }
      break;
    case tacky::BinaryOp::RShift:
      if (!used_immediate) {
        std::string l_start = make_label("L_SHIFT_START");
        std::string l_done = make_label("L_SHIFT_DONE");
        emit_label(l_start);
        emit("TST", "R18");
        emit("BREQ", l_done);
        if (size_of(type) == 2) emit("LSR", "R25");
        emit("ROR", "R24"); // Rotate right through carry
        if (size_of(type) == 1) emit("LSR", "R24"); // If 8-bit, just LSR
        emit("DEC", "R18");
        emit("RJMP", l_start);
        emit_label(l_done);
      }
      break;
    case tacky::BinaryOp::Mul:
      // 8x8 multiplication only for now
      emit("MUL", "R24", "R18");
      emit("MOV", "R24", "R0");
      emit("CLR", "R1");
      if (size_of(type) == 2) {
          // 16-bit mul not implemented
          emit("LDI", "R25", "0");
      }
      break;
    case tacky::BinaryOp::Div:
      emit("RCALL", "__div8"); // TODO: __div16
      break;
    case tacky::BinaryOp::Mod:
      emit("RCALL", "__mod8");
      break;
    case tacky::BinaryOp::Equal: {
      if (!used_immediate) {
          emit("CP", "R24", "R18");
          if (size_of(type) == 2) emit("CPC", "R25", "R19");
      }
      std::string l_skip = make_label("L_SKIP");
      emit("LDI", "R24", "1");
      emit("BREQ", l_skip);
      emit("LDI", "R24", "0");
      emit_label(l_skip);
      if (size_of(type) == 2) emit("LDI", "R25", "0");
    } break;
    case tacky::BinaryOp::NotEqual: {
      if (!used_immediate) {
          emit("CP", "R24", "R18");
          if (size_of(type) == 2) emit("CPC", "R25", "R19");
      }
      std::string l_skip = make_label("L_SKIP");
      emit("LDI", "R24", "1");
      emit("BRNE", l_skip);
      emit("LDI", "R24", "0");
      emit_label(l_skip);
      if (size_of(type) == 2) emit("LDI", "R25", "0");
    } break;
    case tacky::BinaryOp::LessThan: {
      if (!used_immediate) {
          emit("CP", "R24", "R18");
          if (size_of(type) == 2) emit("CPC", "R25", "R19");
      }
      std::string l_skip = make_label("L_SKIP");
      emit("LDI", "R24", "1");
      emit("BRLO", l_skip);
      emit("LDI", "R24", "0");
      emit_label(l_skip);
      if (size_of(type) == 2) emit("LDI", "R25", "0");
    } break;
    case tacky::BinaryOp::GreaterEqual: {
      if (!used_immediate) {
          emit("CP", "R24", "R18");
          if (size_of(type) == 2) emit("CPC", "R25", "R19");
      }
      std::string l_skip = make_label("L_SKIP");
      emit("LDI", "R24", "1");
      emit("BRSH", l_skip);
      emit("LDI", "R24", "0");
      emit_label(l_skip);
      if (size_of(type) == 2) emit("LDI", "R25", "0");
    } break;
    case tacky::BinaryOp::GreaterThan: {
      if (!used_immediate) {
          emit("CP", "R24", "R18");
          if (size_of(type) == 2) emit("CPC", "R25", "R19");
      }
      std::string l_true = make_label("L_TRUE");
      std::string l_done = make_label("L_DONE");
      emit("BREQ", l_done);  // If Z=1, it's false
      emit("BRSH", l_true);  // If C=0 (and we know Z=0), it's true
      emit_label(l_done);
      emit("LDI", "R24", "0");
      std::string l_final = make_label("L_FINAL");
      emit("RJMP", l_final);
      emit_label(l_true);
      emit("LDI", "R24", "1");
      emit_label(l_final);
      if (size_of(type) == 2) emit("LDI", "R25", "0");
    } break;
    case tacky::BinaryOp::LessEqual: {
      if (!used_immediate) {
          emit("CP", "R24", "R18");
          if (size_of(type) == 2) emit("CPC", "R25", "R19");
      }
      std::string l_true = make_label("L_TRUE");
      std::string l_done = make_label("L_DONE");
      emit("BRLO", l_true);  // A < B
      emit("BREQ", l_true);  // A == B
      emit("LDI", "R24", "0");
      std::string l_final = make_label("L_FINAL");
      emit("RJMP", l_final);
      emit_label(l_true);
      emit("LDI", "R24", "1");
      emit_label(l_final);
      if (size_of(type) == 2) emit("LDI", "R25", "0");
    } break;
    default:
      break;
  }
  store_reg_into("R24", arg.dst, type);
}

void AVRCodeGen::compile_variant(const tacky::BitSet &arg) {
  if (const auto mem = std::get_if<tacky::MemoryAddress>(&arg.target)) {
    if (mem->address >= 0x20 && mem->address <= 0x3F) {
      emit("SBI", std::format("0x{:02X}", mem->address - 0x20),
           std::format("{}", arg.bit));
      return;
    }
  }
  load_into_reg(arg.target, "R24");
  emit("ORI", "R24", std::format("{}", 1 << arg.bit));
  store_reg_into("R24", arg.target);
}

void AVRCodeGen::compile_variant(const tacky::BitClear &arg) {
  if (const auto mem = std::get_if<tacky::MemoryAddress>(&arg.target)) {
    if (mem->address >= 0x20 && mem->address <= 0x3F) {
      emit("CBI", std::format("0x{:02X}", mem->address - 0x20),
           std::format("{}", arg.bit));
      return;
    }
  }
  load_into_reg(arg.target, "R24");
  emit("ANDI", "R24", std::format("{}", (unsigned char)~(1 << arg.bit)));
  store_reg_into("R24", arg.target);
}

void AVRCodeGen::compile_variant(const tacky::BitCheck &arg) {
  if (const auto mem = std::get_if<tacky::MemoryAddress>(&arg.source)) {
    if (mem->address >= 0x20 && mem->address <= 0x3F) {
      std::string l_false = make_label("L_BIT_FALSE");
      std::string l_done = make_label("L_BIT_DONE");
      emit("SBIS", std::format("0x{:02X}", mem->address - 0x20),
           std::format("{}", arg.bit));
      emit("RJMP", l_false);
      emit("LDI", "R24", "1");
      emit("RJMP", l_done);
      emit_label(l_false);
      emit("LDI", "R24", "0");
      emit_label(l_done);
      store_reg_into("R24", arg.dst);
      return;
    }
  }
  load_into_reg(arg.source, "R24");
  emit("ANDI", "R24", std::format("{}", 1 << arg.bit));
  std::string l_skip = make_label("L_SKIP");
  emit("LDI", "R18", "1");
  emit("BRNE", l_skip);
  emit("LDI", "R18", "0");
  emit_label(l_skip);
  store_reg_into("R18", arg.dst);
}

void AVRCodeGen::compile_variant(const tacky::BitWrite &arg) {
  if (const auto mem = std::get_if<tacky::MemoryAddress>(&arg.target)) {
    if (mem->address >= 0x20 && mem->address <= 0x3F) {
      load_into_reg(arg.src, "R24");
      std::string l_skip = make_label("L_BIT_WRITE_SKIP");
      std::string l_done = make_label("L_BIT_WRITE_DONE");
      emit("TST", "R24");
      emit("BREQ", l_skip);
      emit("SBI", std::format("0x{:02X}", mem->address - 0x20),
           std::format("{}", arg.bit));
      emit("RJMP", l_done);
      emit_label(l_skip);
      emit("CBI", std::format("0x{:02X}", mem->address - 0x20),
           std::format("{}", arg.bit));
      emit_label(l_done);
      return;
    }
  }
  load_into_reg(arg.src, "R24");
  load_into_reg(arg.target, "R18");
  std::string l_skip = make_label("L_BIT_WRITE_SKIP");
  std::string l_done = make_label("L_BIT_WRITE_DONE");
  emit("TST", "R24");
  emit("BREQ", l_skip);
  emit("ORI", "R18", std::format("{}", 1 << arg.bit));
  emit("RJMP", l_done);
  emit_label(l_skip);
  emit("ANDI", "R18", std::format("{}", (unsigned char)~(1 << arg.bit)));
  emit_label(l_done);
  store_reg_into("R18", arg.target);
}

void AVRCodeGen::compile_variant(const tacky::JumpIfBitSet &arg) {
  if (const auto mem = std::get_if<tacky::MemoryAddress>(&arg.source)) {
    if (mem->address >= 0x20 && mem->address <= 0x3F) {
      // SBIC: skip next if bit is CLEAR → execute RJMP when SET
      emit("SBIC", std::format("0x{:02X}", mem->address - 0x20),
           std::format("{}", arg.bit));
      emit("RJMP", arg.target);
      return;
    }
  }
  load_into_reg(arg.source, "R24");
  emit("ANDI", "R24", std::format("{}", 1 << arg.bit));
  emit("BRNE", arg.target);
}

void AVRCodeGen::compile_variant(const tacky::JumpIfBitClear &arg) {
  if (const auto mem = std::get_if<tacky::MemoryAddress>(&arg.source)) {
    if (mem->address >= 0x20 && mem->address <= 0x3F) {
      // SBIS: skip next if bit is SET → execute RJMP when CLEAR
      emit("SBIS", std::format("0x{:02X}", mem->address - 0x20),
           std::format("{}", arg.bit));
      emit("RJMP", arg.target);
      return;
    }
  }
  load_into_reg(arg.source, "R24");
  emit("ANDI", "R24", std::format("{}", 1 << arg.bit));
  emit("BREQ", arg.target);
}

void AVRCodeGen::compile_variant(const tacky::AugAssign &arg) {
  // Map AugAssign to Binary + Copy
  // This is a fallback if frontend doesn't desugar it, but IRGenerator does desugar it.
  // However, if we want optimized atomic ops later, we can implement it here.
  // For now, throw error or implement basic load-op-store.
  // Since IRGenerator emits AugAssign, we MUST implement it.

  DataType type = get_val_type(arg.target);
  load_into_reg(arg.target, "R24", type);

  // Load operand into R18
  bool used_immediate = false;
  if (const auto c = std::get_if<tacky::Constant>(&arg.operand)) {
      // ... optimization similar to Binary ...
      // For brevity, just load it.
      int val = c->value;
      if (size_of(type) == 1) {
          emit("LDI", "R18", std::format("{}", val & 0xFF));
      } else {
          emit("LDI", "R18", std::format("{}", val & 0xFF));
          emit("LDI", "R19", std::format("{}", (val >> 8) & 0xFF));
      }
  } else {
      load_into_reg(arg.operand, "R18", type);
  }

  // Perform Op (Reuse logic from Binary? Or just minimal set)
  switch (arg.op) {
      case tacky::BinaryOp::Add:
          emit("ADD", "R24", "R18");
          if (size_of(type) == 2) emit("ADC", "R25", "R19");
          break;
      case tacky::BinaryOp::Sub:
          emit("SUB", "R24", "R18");
          if (size_of(type) == 2) emit("SBC", "R25", "R19");
          break;
      // ... others ...
      default:
          throw std::runtime_error("AugAssign op not fully implemented in backend");
  }

  store_reg_into("R24", arg.target, type);
}


void AVRCodeGen::compile_variant(const tacky::InlineAsm &arg) {
  assembly.push_back(AVRAsmLine::Raw(arg.instruction));
}