/*
 * -----------------------------------------------------------------------------
 * Whipsnake Compiler (whipc)
 * Copyright (C) 2026 Ivan Montiel Cardona and the Whipsnake Project Authors
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

#include "AVRCodeGen.h"

#include <algorithm>
#include <format>
#include <iostream>
#include <map>
#include <unordered_map>
#include <utility>
#include <variant>

#include "backend/analysis/StackAllocator.h"
#include "AVRLinearScan.h"
#include "AVRRegisterAllocator.h"

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

  // Sanitize dots to underscores: AVRA treats '.' as the current-address operator,
  // so dotted names (e.g. inline1.foo.bar) in instructions cause assembler errors.
  std::replace(name.begin(), name.end(), '.', '_');
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

void AVRCodeGen::emit_branch(const std::string &cond,
                             const std::string &target) {
  // AVR short branches are limited to ±63 words. Use a label trampoline:
  //   INV_COND skip   ; skip RJMP if inverted condition holds
  //   RJMP target     ; long jump (±2047 words, covers all of ATmega328P)
  //   skip:
  static const std::unordered_map<std::string, std::string> inv = {
      {"BREQ", "BRNE"}, {"BRNE", "BREQ"}, {"BRLT", "BRGE"},
      {"BRGE", "BRLT"}, {"BRCS", "BRCC"}, {"BRCC", "BRCS"},
      {"BRLO", "BRSH"}, {"BRSH", "BRLO"},
  };
  auto it = inv.find(cond);
  std::string inverted = (it != inv.end()) ? it->second : cond;
  std::string skip = make_label("L_BR_SKIP");
  emit(inverted, skip);
  emit("RJMP", target);
  emit_label(skip);
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

    // Check register allocation first — MOV is a single-cycle register copy.
    if (!name.empty() && reg_layout.contains(name)) {
      const std::string &src_reg = reg_layout.at(name);
      if (src_reg != reg) {
        emit("MOV", reg, src_reg);
        if (is_16bit) emit("MOV", reg_h, get_high_reg(src_reg));
      }
      // If src_reg == reg, the value is already there — emit nothing.
      return;
    }

    // Check linear-scan allocated temporaries (R16/R17).
    if (!name.empty() && tmp_reg_layout.contains(name)) {
      const std::string &src_reg = tmp_reg_layout.at(name);
      if (src_reg != reg) {
        emit("MOV", reg, src_reg);
        if (is_16bit) emit("MOV", reg_h, get_high_reg(src_reg));
      }
      return;
    }

    if (!name.empty() && stack_layout.contains(name)) {
      int offset = stack_layout.at(name);
      if (offset + (is_16bit ? 1 : 0) < 64) {
        emit("LDD", reg, std::format("Y+{}", offset));
        if (is_16bit) {
          emit("LDD", reg_h, std::format("Y+{}", offset + 1));
        }
        return;
      }
      // offset >= 64: LDD Y+q requires q<64 → use LDS with the absolute
      // numeric SRAM address. Using the .equ symbol name would break AVRA
      // because dots in names are parsed as the current-address operator.
      int abs_addr = 0x0100 + offset;
      emit("LDS", reg, std::format("0x{:04X}", abs_addr));
      if (is_16bit) {
        emit("LDS", reg_h, std::format("0x{:04X}", abs_addr + 1));
      }
      return;
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

    // Check register allocation first — store via MOV (single cycle, no RAM).
    if (!name.empty() && reg_layout.contains(name)) {
      const std::string &dst_reg = reg_layout.at(name);
      if (dst_reg != reg) {
        emit("MOV", dst_reg, reg);
        if (is_16bit) emit("MOV", get_high_reg(dst_reg), reg_h);
      }
      // If dst_reg == reg, value is already in the right register — nothing to do.
      return;
    }

    // Check linear-scan allocated temporaries (R16/R17).
    if (!name.empty() && tmp_reg_layout.contains(name)) {
      const std::string &dst_reg = tmp_reg_layout.at(name);
      if (dst_reg != reg) {
        emit("MOV", dst_reg, reg);
        if (is_16bit) emit("MOV", get_high_reg(dst_reg), reg_h);
      }
      return;
    }

    if (!name.empty() && stack_layout.contains(name)) {
      int offset = stack_layout.at(name);
      if (offset + (is_16bit ? 1 : 0) < 64) {
        emit("STD", std::format("Y+{}", offset), reg);
        if (is_16bit) {
          emit("STD", std::format("Y+{}", offset + 1), reg_h);
        }
        return;
      }
      // offset >= 64: STD Y+q requires q<64 → use STS with absolute address.
      int abs_addr = 0x0100 + offset;
      emit("STS", std::format("0x{:04X}", abs_addr), reg);
      if (is_16bit) {
        emit("STS", std::format("0x{:04X}", abs_addr + 1), reg_h);
      }
      return;
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
  string_pool_.clear();
  uart_send_z_needed_ = false;
  all_tmp_reg_names_.clear();

  StackAllocator allocator;
  auto [offsets, total_size] = allocator.allocate(program);
  this->stack_layout = offsets;

  // Register allocation: assign named locals to R4-R15, reducing Y-frame traffic.
  this->reg_layout = AVRRegisterAllocator::allocate(program);

  emit_comment("Generated by whipc for " + config.chip);
  // NOTE: .device directive omitted — avra 1.3.0 crashes on .device atmega328p with .db
  // emit_raw(".device " + config.chip);

  // Emit .extern directives for @extern C symbols (avr-as / GNU AS syntax).
  // avra ignores unrecognised directives starting with '.', so this is safe
  // for both assemblers; avr-ld uses them to resolve external ELF symbols.
  for (const auto &sym : program.extern_symbols) {
    emit_raw(".extern " + sym);
  }
  if (!program.extern_symbols.empty()) emit_raw("");

  // Stack and Memory setup — skip .equ for register-allocated variables.
  emit_raw(".equ RAMSTART = 0x0100");
  emit_raw(std::format(".equ _stack_base = RAMSTART"));

  for (const auto &[name, offset] : stack_layout) {
    if (reg_layout.contains(name)) continue;         // lives in named register R4-R15
    if (all_tmp_reg_names_.contains(name)) continue; // lives in R16/R17 (linear scan)
    // Sanitize dots → underscores: AVRA treats '.' as the current-address operator,
    // so dotted names (e.g. inline1._foo.bar) in .equ lines cause parse errors.
    std::string safe_name = name;
    std::replace(safe_name.begin(), safe_name.end(), '.', '_');
    emit_raw(std::format(".equ {} = _stack_base + {}", safe_name, offset));
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
    // ATmega328P vector table: 26 slots (RESET + 25 IRQs).
    // AVRA .org uses WORD addresses. The ATmega328P stores program memory as
    // 16-bit words, so word address = byte address / 2.  The datasheet lists
    // each vector's WORD address: INT0=0x0002, INT1=0x0004, PCINT0=0x0006, …
    // @interrupt(addr) takes that same word address directly.
    // Each slot is 2 words wide (to fit a 2-word JMP instruction).
    // We use RJMP (1 word) and fill the second word with NOP.
    for (int vec = 1; vec <= 25; vec++) {
      int word_addr = vec * 2;  // word address: 0x0002, 0x0004, 0x0006, …
      emit_raw(std::format(".org 0x{:04X}", word_addr));
      if (isr_map.contains(word_addr)) {
        emit("RJMP", isr_map[word_addr]->name);
        emit_raw("\tNOP");  // fill 2nd word of the 2-word vector slot
      } else {
        emit("RETI");
        emit_raw("\tNOP");  // fill 2nd word of the 2-word vector slot
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

  // Emit flash string pool (__uart_send_z routine + .db string data) if needed.
  emit_string_pool(os);
}

void AVRCodeGen::compile_function(const tacky::Function &func) {
  // Per-function linear scan: assign short-lived temporaries to R16/R17.
  tmp_reg_layout = AVRLinearScan::allocate(func);
  for (const auto &[name, reg] : tmp_reg_layout)
    all_tmp_reg_names_.insert(name);

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

  // For non-inline, non-main, non-interrupt functions with parameters:
  // copy arguments from calling-convention registers into their frame/reg slots.
  // Convention (GCC AVR compatible, descending pairs from R25):
  //   arg0 → R24, arg1 → R22, arg2 → R20, arg3 → R18
  //   (16-bit: arg0 → R25:R24, arg1 → R23:R22, etc.)
  if (!func.is_interrupt && func.name != "main" && !func.params.empty()) {
    const std::string arg_regs[] = {"R24", "R22", "R20", "R18"};
    for (size_t k = 0; k < func.params.size() && k < 4; ++k) {
      const std::string &pname = func.params[k];
      if (reg_layout.contains(pname)) {
        // Parameter lives in a scratch register — MOV it in (skip if already there)
        if (arg_regs[k] != reg_layout.at(pname))
          emit("MOV", reg_layout.at(pname), arg_regs[k]);
      } else if (stack_layout.contains(pname)) {
        emit("STD", std::format("Y+{}", stack_layout.at(pname)), arg_regs[k]);
      }
    }
  }

  bool emitted_epilogue = false;
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
      emitted_epilogue = true;
      continue;
    }
    compile_instruction(instr);
  }

  // Implicit return for ISR only if no explicit return was encountered
  if (func.is_interrupt && !emitted_epilogue) {
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
      emit_branch("BREQ", arg.target);
  } else {
      emit("TST", "R24");
      emit_branch("BREQ", arg.target);
  }
}

void AVRCodeGen::compile_variant(const tacky::JumpIfNotZero &arg) {
  DataType type = get_val_type(arg.condition);
  load_into_reg(arg.condition, "R24", type);

  if (size_of(type) == 2) {
      emit("OR", "R24", "R25");
      emit_branch("BRNE", arg.target);
  } else {
      emit("TST", "R24");
      emit_branch("BRNE", arg.target);
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
        // CPC does not accept an immediate — load constant into R18:R19 for 16-bit compare
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
  emit_branch("BREQ", arg.target);
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
  emit_branch("BRNE", arg.target);
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
  emit_branch("BRLO", arg.target);
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
  // A <= B (unsigned): branch if lower (BRLO) or equal (BREQ)
  emit_branch("BRLO", arg.target);
  emit_branch("BREQ", arg.target);
}

void AVRCodeGen::compile_variant(const tacky::JumpIfGreaterThan &arg) {
  // Unsigned greater-than: a > b.
  // AVRA does not have a BRHI mnemonic. Two strategies:
  //   Const b, val < max: compare against val+1 and use BRSH  (a >= val+1 == a > val)
  //   Variable b:         CMP then BREQ(skip) + BRSH(target)
  DataType type = get_val_type(arg.src1);
  load_into_reg(arg.src1, "R24", type);

  if (const auto c = std::get_if<tacky::Constant>(&arg.src2)) {
    int val = c->value;
    int max_val = (size_of(type) == 2) ? 0xFFFF : 0xFF;
    if (val < max_val) {
      // Promote to val+1 so we can use BRSH (>=) which equals > val
      int cmp_val = val + 1;
      if (size_of(type) == 2) {
        emit("LDI", "R18", std::format("{}", cmp_val & 0xFF));
        emit("LDI", "R19", std::format("{}", (cmp_val >> 8) & 0xFF));
        emit("CP",  "R24", "R18");
        emit("CPC", "R25", "R19");
      } else {
        emit("CPI", "R24", std::format("{}", cmp_val & 0xFF));
      }
      emit_branch("BRSH", arg.target);
      return;
    }
    // val == max: a > max is always false for the type; no branch emitted
    return;
  }

  // Variable src2: compare, skip branch if equal, take branch if higher
  load_into_reg(arg.src2, "R18", type);
  emit("CP", "R24", "R18");
  if (size_of(type) == 2) {
    emit("CPC", "R25", "R19");
  }
  std::string skip = make_label("L_BRHI_SKIP");
  emit("BREQ", skip);
  emit_branch("BRSH", arg.target);
  emit_label(skip);
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
  emit_branch("BRSH", arg.target);
}

void AVRCodeGen::compile_variant(const tacky::Call &arg) {
  // Load arguments into calling-convention registers before RCALL.
  // Convention (GCC AVR compatible): arg0 → R24, arg1 → R22, arg2 → R20, arg3 → R18
  // (16-bit arguments use the pair: R25:R24, R23:R22, etc.)
  const std::string arg_regs[] = {"R24", "R22", "R20", "R18"};
  for (size_t k = 0; k < arg.args.size() && k < 4; ++k) {
    DataType arg_type = get_val_type(arg.args[k]);
    load_into_reg(arg.args[k], arg_regs[k], arg_type);
  }
  emit("RCALL", arg.function_name);
  // Return value is in R24 (or R25:R24 for 16-bit)
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
      // Logical NOT: branch immediately after TST to avoid CLR clobbering SREG.
      std::string l_true = make_label("L_NOT_TRUE");
      std::string l_done = make_label("L_NOT_DONE");
      if (size_of(type) == 2) emit("OR", "R24", "R25");
      emit("TST", "R24");
      emit_branch("BREQ", l_true);   // branch on TST flags — no intervening write
      emit("CLR", "R24");            // non-zero input → NOT = 0
      if (size_of(type) == 2) emit("CLR", "R25");
      emit("RJMP", l_done);
      emit_label(l_true);
      emit("LDI", "R24", "1");       // zero input → NOT = 1
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
            case tacky::BinaryOp::Add: {
                // Negate the full 16-bit value so carry propagates correctly.
                // hi8(-K) != -(hi8(K)) when lo overflows, so compute together.
                int neg = -(int)val;
                emit("SUBI", "R24", std::format("{}", (unsigned char)(neg & 0xFF)));
                emit("SBCI", "R25", std::format("{}", (unsigned char)((neg >> 8) & 0xFF)));
                used_immediate = true;
                break;
            }
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
        emit_branch("BREQ", l_done);
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
        emit_branch("BREQ", l_done);
        if (size_of(type) == 2) {
          emit("LSR", "R25"); // shift high byte, carry → low bit of R25
          emit("ROR", "R24"); // rotate carry into high bit of R24
        } else {
          emit("LSR", "R24"); // 8-bit: logical shift right (no carry needed)
        }
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
      emit("RCALL", "__div8");
      break;
    case tacky::BinaryOp::FloorDiv:
      emit("RCALL", "__div8");
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
      emit_branch("BREQ", l_skip);
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
      emit_branch("BRNE", l_skip);
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
      emit_branch("BRLO", l_skip);
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
      emit_branch("BRSH", l_skip);
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
      emit_branch("BREQ", l_done);  // If Z=1, it's false
      emit_branch("BRSH", l_true);  // If C=0 (and we know Z=0), it's true
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
      emit_branch("BRLO", l_true);  // A < B
      emit_branch("BREQ", l_true);  // A == B
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
  emit_branch("BRNE", l_skip);
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
      emit_branch("BREQ", l_skip);
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
  emit_branch("BREQ", l_skip);
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
  emit_branch("BRNE", arg.target);
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
  emit_branch("BREQ", arg.target);
}

void AVRCodeGen::compile_variant(const tacky::AugAssign &arg) {
  DataType type = get_val_type(arg.target);
  load_into_reg(arg.target, "R24", type);

  // Try immediate-optimized path for constant operands
  bool used_immediate = false;
  if (const auto c = std::get_if<tacky::Constant>(&arg.operand)) {
      int val = c->value;
      if (size_of(type) == 1) {
          switch (arg.op) {
            case tacky::BinaryOp::Add:
              if (val == 1) { emit("INC", "R24"); used_immediate = true; }
              else if (val == 255) { emit("DEC", "R24"); used_immediate = true; }
              else { emit("SUBI", "R24", std::format("{}", (unsigned char)-val)); used_immediate = true; }
              break;
            case tacky::BinaryOp::Sub:
              if (val == 1) { emit("DEC", "R24"); used_immediate = true; }
              else if (val == 255) { emit("INC", "R24"); used_immediate = true; }
              else { emit("SUBI", "R24", std::format("{}", val & 0xFF)); used_immediate = true; }
              break;
            case tacky::BinaryOp::BitAnd:
              emit("ANDI", "R24", std::format("{}", val & 0xFF)); used_immediate = true; break;
            case tacky::BinaryOp::BitOr:
              emit("ORI", "R24", std::format("{}", val & 0xFF)); used_immediate = true; break;
            case tacky::BinaryOp::BitXor:
              emit("LDI", "R18", std::format("{}", val & 0xFF));
              emit("EOR", "R24", "R18"); used_immediate = true; break;
            case tacky::BinaryOp::LShift:
              for (int i = 0; i < (val & 7); ++i) emit("LSL", "R24");
              used_immediate = true; break;
            case tacky::BinaryOp::RShift:
              for (int i = 0; i < (val & 7); ++i) emit("LSR", "R24");
              used_immediate = true; break;
            default: break;
          }
      } else {
          // 16-bit immediate
          switch (arg.op) {
            case tacky::BinaryOp::Add: {
              int neg = -(int)val;
              emit("SUBI", "R24", std::format("{}", (unsigned char)(neg & 0xFF)));
              emit("SBCI", "R25", std::format("{}", (unsigned char)((neg >> 8) & 0xFF)));
              used_immediate = true; break;
            }
            case tacky::BinaryOp::Sub:
              emit("SUBI", "R24", std::format("{}", val & 0xFF));
              emit("SBCI", "R25", std::format("{}", (val >> 8) & 0xFF));
              used_immediate = true; break;
            default:
              emit("LDI", "R18", std::format("{}", val & 0xFF));
              emit("LDI", "R19", std::format("{}", (val >> 8) & 0xFF));
              break;
          }
      }
  }

  if (!used_immediate) {
      load_into_reg(arg.operand, "R18", type);
  }

  if (!used_immediate) {
      switch (arg.op) {
        case tacky::BinaryOp::Add:
          emit("ADD", "R24", "R18");
          if (size_of(type) == 2) emit("ADC", "R25", "R19");
          break;
        case tacky::BinaryOp::Sub:
          emit("SUB", "R24", "R18");
          if (size_of(type) == 2) emit("SBC", "R25", "R19");
          break;
        case tacky::BinaryOp::BitAnd:
          emit("AND", "R24", "R18");
          if (size_of(type) == 2) emit("AND", "R25", "R19");
          break;
        case tacky::BinaryOp::BitOr:
          emit("OR", "R24", "R18");
          if (size_of(type) == 2) emit("OR", "R25", "R19");
          break;
        case tacky::BinaryOp::BitXor:
          emit("EOR", "R24", "R18");
          if (size_of(type) == 2) emit("EOR", "R25", "R19");
          break;
        case tacky::BinaryOp::LShift: {
          std::string l_start = make_label("L_AUG_LSHIFT");
          std::string l_done = make_label("L_AUG_LSHIFT_DONE");
          emit_label(l_start);
          emit("TST", "R18");
          emit_branch("BREQ", l_done);
          emit("LSL", "R24");
          if (size_of(type) == 2) emit("ROL", "R25");
          emit("DEC", "R18");
          emit("RJMP", l_start);
          emit_label(l_done);
          break;
        }
        case tacky::BinaryOp::RShift: {
          std::string l_start = make_label("L_AUG_RSHIFT");
          std::string l_done = make_label("L_AUG_RSHIFT_DONE");
          emit_label(l_start);
          emit("TST", "R18");
          emit_branch("BREQ", l_done);
          if (size_of(type) == 2) {
            emit("LSR", "R25");
            emit("ROR", "R24");
          } else {
            emit("LSR", "R24");
          }
          emit("DEC", "R18");
          emit("RJMP", l_start);
          emit_label(l_done);
          break;
        }
        case tacky::BinaryOp::Mul:
          emit("MUL", "R24", "R18");
          emit("MOV", "R24", "R0");
          emit("CLR", "R1");
          if (size_of(type) == 2) emit("LDI", "R25", "0");
          break;
        case tacky::BinaryOp::Div:
          emit("RCALL", "__div8");
          break;
        case tacky::BinaryOp::FloorDiv:
          emit("RCALL", "__div8");
          break;
        case tacky::BinaryOp::Mod:
          emit("RCALL", "__mod8");
          break;
        default:
          throw std::runtime_error(std::format("AugAssign op {} not implemented in AVR backend",
              static_cast<int>(arg.op)));
      }
  }

  store_reg_into("R24", arg.target, type);
}


void AVRCodeGen::compile_variant(const tacky::InlineAsm &arg) {
  assembly.push_back(AVRAsmLine::Raw(arg.instruction));
}

// ---------------------------------------------------------------------------
// Array Load / Store  (variable-index uint8/uint16 arrays on the Y-frame stack)
// ---------------------------------------------------------------------------
//
// Layout: array base sits at stack_layout[array_name]; element k is at
//   RAMSTART + stack_layout[array_name] + k * elem_size
//
// For constant index:  use LDD/STD Y+offset directly (offset must be 0-63).
// For variable index:  compute Z = RAMSTART + base + index*elem_size and use
//                      LD/ST Z (Z = R30:R31).
// ---------------------------------------------------------------------------

void AVRCodeGen::compile_variant(const tacky::ArrayLoad &arg) {
  int elem_size = size_of(arg.elem_type);
  bool is_16bit = (elem_size == 2);

  if (!stack_layout.contains(arg.array_name)) {
    emit_comment("ArrayLoad: array not in stack_layout -- skip");
    return;
  }
  int base_offset = stack_layout.at(arg.array_name);

  if (auto *c = std::get_if<tacky::Constant>(&arg.index)) {
    // Constant index: LDD Y+(base + k*elem_size)
    int offset = base_offset + c->value * elem_size;
    if (offset < 64) {
      emit("LDD", "R24", std::format("Y+{}", offset));
      if (is_16bit) emit("LDD", "R25", std::format("Y+{}", offset + 1));
    } else {
      // offset >= 64: use absolute LDS (RAMSTART = 0x0100)
      emit("LDS", "R24", std::format("0x{:04X}", 0x0100 + offset));
      if (is_16bit) emit("LDS", "R25", std::format("0x{:04X}", 0x0100 + offset + 1));
    }
  } else {
    // Variable index: Z = RAMSTART + base_offset + index * elem_size
    emit_comment("ArrayLoad variable index via Z");
    load_into_reg(arg.index, "R24", DataType::UINT8);
    // R24 = index (uint8)
    if (elem_size == 2) {
      emit("LSL", "R24");  // index * 2 (uint8 array up to 127 elements)
    }
    int abs_base = 0x0100 + base_offset;
    emit("LDI", "R30", std::format("low({})", abs_base));
    emit("LDI", "R31", std::format("high({})", abs_base));
    emit("ADD", "R30", "R24");
    emit("CLR", "R16");         // R16 = 0; CLR preserves carry flag
    emit("ADC", "R31", "R16"); // propagate carry from ADD
    emit("LD", "R24", "Z");
    if (is_16bit) emit("LDD", "R25", "Z+1");
  }

  store_reg_into("R24", arg.dst, arg.elem_type);
}

void AVRCodeGen::compile_variant(const tacky::ArrayStore &arg) {
  int elem_size = size_of(arg.elem_type);
  bool is_16bit = (elem_size == 2);

  if (!stack_layout.contains(arg.array_name)) {
    emit_comment("ArrayStore: array not in stack_layout -- skip");
    return;
  }
  int base_offset = stack_layout.at(arg.array_name);

  // Load src value into R24(:R25)
  load_into_reg(arg.src, "R24", arg.elem_type);

  if (auto *c = std::get_if<tacky::Constant>(&arg.index)) {
    // Constant index: STD Y+(base + k*elem_size)
    int offset = base_offset + c->value * elem_size;
    if (offset < 64) {
      emit("STD", std::format("Y+{}", offset), "R24");
      if (is_16bit) emit("STD", std::format("Y+{}", offset + 1), "R25");
    } else {
      emit("STS", std::format("0x{:04X}", 0x0100 + offset), "R24");
      if (is_16bit) emit("STS", std::format("0x{:04X}", 0x0100 + offset + 1), "R25");
    }
  } else {
    // Variable index: save src, compute Z, then store.
    // src is in R24(:R25) -- save to R18(:R19) before clobbering R24 for index
    emit("MOV", "R18", "R24");
    if (is_16bit) emit("MOV", "R19", "R25");

    emit_comment("ArrayStore variable index via Z");
    load_into_reg(arg.index, "R24", DataType::UINT8);
    if (elem_size == 2) {
      emit("LSL", "R24");
    }
    int abs_base = 0x0100 + base_offset;
    emit("LDI", "R30", std::format("low({})", abs_base));
    emit("LDI", "R31", std::format("high({})", abs_base));
    emit("ADD", "R30", "R24");
    emit("CLR", "R16");
    emit("ADC", "R31", "R16");
    emit("ST", "Z", "R18");
    if (is_16bit) emit("STD", "Z+1", "R19");
  }
}

// ---------------------------------------------------------------------------
// Flash String Pool — UARTSendString
// ---------------------------------------------------------------------------

std::string AVRCodeGen::intern_string(const std::string &text) {
  auto it = string_pool_.find(text);
  if (it != string_pool_.end()) return it->second;
  std::string label = "__str_" + std::to_string(string_pool_.size());
  string_pool_[text] = label;
  return label;
}

void AVRCodeGen::compile_variant(const tacky::UARTSendString &arg) {
  uart_send_z_needed_ = true;
  std::string content = arg.text + arg.end_str;
  if (content.empty()) return;
  std::string label = intern_string(content);
  // Load Z with the byte address of the string in flash.
  // AVRA label values are WORD addresses; multiply by 2 to get byte address for LPM.
  emit("LDI", "R30", "low(" + label + " * 2)");
  emit("LDI", "R31", "high(" + label + " * 2)");
  emit("RCALL", "__uart_send_z");
}

void AVRCodeGen::emit_string_pool(std::ostream &os) const {
  if (!uart_send_z_needed_) return;

  os << "\n; --- Flash String Pool (LPM+Z UART send) ---\n";

  // Shared null-terminated string send routine.
  // On entry: Z = byte address of null-terminated string in flash.
  // Clobbers: R24 (char), R25 (UCSR0A scratch). Preserves all other registers.
  os << "__uart_send_z:\n";
  os << "__usendz_loop:\n";
  os << "\tLPM\tR24, Z+\n";         // load byte, post-increment Z
  os << "\tTST\tR24\n";             // null terminator?
  os << "\tBREQ\t__usendz_done\n";  // yes → exit
  os << "__usendz_wait:\n";
  os << "\tLDS\tR25, 0x00C0\n";     // UCSR0A
  os << "\tSBRS\tR25, 5\n";         // skip if UDRE0 (bit 5) set
  os << "\tRJMP\t__usendz_wait\n";  // not ready → wait
  os << "\tSTS\t0x00C6, R24\n";     // UDR0 = char
  os << "\tRJMP\t__usendz_loop\n";  // next char
  os << "__usendz_done:\n";
  os << "\tRET\n";
  os << "\n";

  // String data — emit each string as a sequence of decimal byte values + null.
  for (const auto &[text, label] : string_pool_) {
    os << label << ":\n";
    os << ".db ";
    bool first = true;
    for (unsigned char c : text) {
      if (!first) os << ", ";
      os << static_cast<int>(c);
      first = false;
    }
    os << ", 0\n";  // null terminator
  }
}