#include "PIC14CodeGen.h"
#include <algorithm>
#include <format>
#include <iostream>
#include <utility>
#include <variant>

#include "backend/analysis/StackAllocator.h"

PIC14CodeGen::PIC14CodeGen(DeviceConfig cfg)
  : config(std::move(cfg)), out(nullptr) {
  label_counter = 0;
  ram_head = 0x20;
}

std::string PIC14CodeGen::resolve_address(const tacky::Val &val) {
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

  if (stack_layout.contains(name)) {
    return name;
  }

  return get_or_alloc_variable(name);
}

std::string PIC14CodeGen::get_or_alloc_variable(const std::string &name) {
  if (!symbol_table.contains(name)) {
    symbol_table[name] = ram_head++;
    emit_raw(std::format("{} EQU 0x{:02X}", name, symbol_table[name]));
  }
  return name;
}

void PIC14CodeGen::select_bank(const std::string &operand) {
  int addr = -1;

  try {
    if (operand.size() > 2 && operand.substr(0, 2) == "0x") {
      addr = std::stoi(operand, nullptr, 16);
    } else if (stack_layout.contains(operand)) {
      // Stack variables are offset from _stack_base (0x20 usually)
      // We need to know the absolute address.
      // But wait, the symbol table stores the offset?
      // emit("... EQU _stack_base + offset")
      // We can't easily resolve _stack_base + offset at compile time here
      // UNLESS we know _stack_base.
      // In constructor/compile, we set _stack_base to offsets?
      // Let's look at compile(): _stack_base is logic.
      // Actually, stack variables in CBLOCK 0x20 are in Bank 0 (0x20-0x7F).
      // If we assume stack is always in Bank 0 for now (PIC16F84A has only Bank
      // 0 RAM essentially, mapped to 0x00-0x4F?).
      // Wait, PIC16F84A has 68 bytes of RAM. Bank 0: 0x0C-0x4F. Bank 1:
      // 0x8C-0xCF (mapped?).
      // Status bit RP0 selects Bank 0 or 1.
      // For generic PIC14, we should try to resolve.
      // If we can't resolve, we MUST emit bank select conservatively?
      // Or assume Bank 0 if it's a stack variable?
      // Let's look at stack_layout.
      // It maps name -> offset.
      // _stack_base is defined in ASM as a constant (usually 0x20 or after
      // special registers).
      // So name = 0x20 + offset.
      // This is generally Bank 0 (0x00 - 0x7F).
      // Let's assume Bank 0 for stack variables for now, but mark as TODO if
      // meaningful.
      addr = 0x20 + stack_layout.at(operand);
    } else if (symbol_table.contains(operand)) {
      // Global/Static variables allocated by us
      addr = symbol_table.at(operand);
    }
  } catch (...) {
    // If stoi fails or something else, we can't optimize.
    // Fallback: emit bank select (reset state to unknown?)
    // distinct from "addr found".
  }

  if (addr != -1) {
    const int new_bank = (addr >> 7) & 0x03;
    if (current_bank == new_bank)
      return;

    if (new_bank & 1)
      emit("BSF", "STATUS", "5");
    else
      emit("BCF", "STATUS", "5"); // RP0
    if (new_bank & 2)
      emit("BSF", "STATUS", "6");
    else
      emit("BCF", "STATUS", "6"); // RP1
    current_bank = new_bank;
  } else {
    // Unknown address format (e.g. indirect addressing FSR? or just
    // unimplemented resolution) We should probably invalidate the bank state
    // just in case? Or do nothing? Standard `select_bank` only handled "0x..."
    // before. If we pass a label name that we don't know, we might be in
    // trouble. But `resolve_address` returns either hex, or a name in
    // stack_layout/symbol_table, or allocs a new one. So we SHOULD cover all
    // cases. If we miss one, we might fail to switch bank. Safe fallback: If we
    // can't determine bank, we should probably output nothing (assume user
    // handles it?) OR emit a conservative bank switch if we could guess? The
    // original code ONLY handled "0x". So expanding to symbols is strictly
    // better.
    current_bank = -1; // Invalidate
  }
}

void PIC14CodeGen::emit(const std::string &mnemonic) const {
  const_cast<PIC14CodeGen *>(this)->assembly.push_back(
    PIC14AsmLine::Instruction(mnemonic));
}

void PIC14CodeGen::emit(const std::string &mnemonic,
                        const std::string &op1) const {
  const_cast<PIC14CodeGen *>(this)->assembly.push_back(
    PIC14AsmLine::Instruction(mnemonic, op1));
}

void PIC14CodeGen::emit(const std::string &mnemonic, const std::string &op1,
                        const std::string &op2) const {
  const_cast<PIC14CodeGen *>(this)->assembly.push_back(
    PIC14AsmLine::Instruction(mnemonic, op1, op2));
}

void PIC14CodeGen::emit_label(const std::string &label) const {
  const_cast<PIC14CodeGen *>(this)->assembly.push_back(
    PIC14AsmLine::Label(label));
}

void PIC14CodeGen::emit_comment(const std::string &comment) const {
  const_cast<PIC14CodeGen *>(this)->assembly.push_back(
    PIC14AsmLine::Comment(comment));
}

void PIC14CodeGen::emit_raw(const std::string &text) const {
  const_cast<PIC14CodeGen *>(this)->assembly.push_back(PIC14AsmLine::Raw(text));
}

void PIC14CodeGen::compile(const tacky::Program &program, std::ostream &os) {
  out = &os;
  assembly.clear();

  StackAllocator allocator;
  auto [offsets, total_size] = allocator.allocate(program);

  // Pre-scan for floats
  uses_float = false;
  for (const auto &func: program.functions) {
    for (const auto &instr: func.body) {
      if (std::holds_alternative<tacky::Binary>(instr)) {
        const auto &bin = std::get<tacky::Binary>(instr);
        auto is_float_val = [](const tacky::Val &v) {
          if (auto var = std::get_if<tacky::Variable>(&v))
            return var->type == DataType::FLOAT;
          if (auto tmp = std::get_if<tacky::Temporary>(&v))
            return tmp->type == DataType::FLOAT;
          return false;
        };
        if (is_float_val(bin.dst) || is_float_val(bin.src1) ||
            is_float_val(bin.src2)) {
          uses_float = true;
          break;
        }
      }
    }
    if (uses_float)
      break;
  }

  this->stack_layout = offsets;
  this->var_sizes = allocator.get_variable_sizes();

  // Emit CBLOCK for stack variables
  if (!stack_layout.empty() || uses_float) {
    emit_comment("--- Compiled Stack (Overlays) ---");
    emit_raw("\tCBLOCK 0x20");
    // Find max offset
    int max_offset = 0;
    for (const auto &[name, offset]: stack_layout) {
      if (offset > max_offset)
        max_offset = offset;
    }
    emit_raw(std::format("_stack_base: {}",
                         total_size)); // Use total_size from allocator
    if (uses_float) {
      emit_raw("__FP_A: 4");
      emit_raw("__FP_B: 4");
      emit_raw("__FP_C: 4"); // Scratch/Result if needed? __add_float usually
      // uses A as accum.
    }
    emit_raw("\tENDC");
    emit_raw("");
  }

  // Define Symbols (Global Variables)
  emit_comment("--- Variable Offsets ---");
  for (const auto &[name, offset]: stack_layout) {
    emit_raw(std::format("{} EQU _stack_base + {}", name, offset));
  }
  emit_raw("");

  emit_config_directives();

  // Emit Interrupt Handlers (IVT)
  // PIC14 only supports vector 0x04.
  // We must validate that all interrupt functions target 0x04 (or use default).

  const tacky::Function *isr_0x04 = nullptr;

  for (const auto &func: program.functions) {
    if (func.is_interrupt) {
      if (func.interrupt_vector != 0x04 && func.interrupt_vector != 0) {
        // 0 is default from AST if not specified? Parser sets 0x04 default?
        // If parser sets 0x04, we are good.
        // If user specified @interrupt(0x08), we must error for PIC14.
        throw std::runtime_error(
          std::format("PIC14 architecture does not support interrupt vector "
                      "0x{:02X}. Only 0x04 is supported.",
                      func.interrupt_vector));
      }

      if (isr_0x04 != nullptr) {
        throw std::runtime_error(
          "Multiple interrupt handlers defined for vector 0x04.");
      }
      isr_0x04 = &func;
    }
  }

  if (isr_0x04) {
    emit_raw(std::format("\tORG 0x{:02X}", 0x04));
    emit_label("__interrupt");

    emit_context_save();

    current_function_name = isr_0x04->name;
    current_bank = -1; // Unknown bank on entry
    current_block_terminated = false; // Reset dead code flag

    for (const auto &instr: isr_0x04->body) {
      if (current_block_terminated) {
        if (std::holds_alternative<tacky::Label>(instr)) {
          current_block_terminated = false;
          compile_instruction(instr);
        }
        continue;
      }

      if (std::holds_alternative<tacky::Return>(instr)) {
        emit_context_restore();
        emit_interrupt_return();
        current_block_terminated = true;
        continue;
      }
      compile_instruction(instr);
    }

    if (!current_block_terminated) {
      emit_context_restore();
      emit_interrupt_return();
    }
  } else {
    // Dummy interrupt handler
    emit_raw("\tORG 0x04");
    emit_label("__interrupt");
    emit("RETFIE");
  }

  // Emit Main and other functions
  // emit_label("main"); // REMOVED: main is emitted by compile_function("main")

  for (const auto &func: program.functions) {
    if (func.is_interrupt)
      continue; // Already emitted

    // Skip inline functions (they're already expanded at call sites)
    // Always emit main even if marked inline (it shouldn't be, but safety
    // first)
    if (func.is_inline && func.name != "main") {
      continue;
    }

    // Defer main to end (optional, but good for organization)
    // Actually, we just need to ensure we don't emit "main" label manually
    // above AND let this loop emit it. BUT the user request says: "Skip main
    // during the loop. Only emit main at the very end"

    if (func.name == "main")
      continue;

    compile_function(func);
  }

  // Emit Main at the end
  for (const auto &func: program.functions) {
    if (func.name == "main") {
      compile_function(func);
      break;
    }
  }

  if (uses_float) {
    emit_raw("#include \"float.inc\"");
  }

  if (needs_delay_1ms) {
    emit_label("__delay_1ms_base");
    unsigned long cycles = config.frequency / 4000; // 1ms
    // Subtract CALL(2) + RETURN(2) + Loop Overhead(3) = 7 cycles
    if (cycles > 7)
      cycles -= 7;
    else
      cycles = 0; // Handle very low clock speeds gracefully
    emit_delay_cycles(cycles);
    emit("RETURN");
  }

  emit_raw("\tEND");

  // Optimization step
  // auto optimized = PIC14Peephole::optimize(assembly);

  for (const auto &line: assembly) {
    os << line.to_string() << "\n";
  }
}

void PIC14CodeGen::emit_config_directives() {
  if (config.fuses.empty())
    return;

  std::string config_line = "\t__CONFIG";
  bool first = true;
  for (const auto &[key, val]: config.fuses) {
    if (!first)
      config_line += " &";
    // gpasm expects _KEY_VAL or just _VAL depending on the header
    // For now, we'll assume the user provides the full flag like _FOSC_HS
    if (val == "ON" || val == "1" || val == "TRUE") {
      config_line += " " + key;
    } else if (val == "OFF" || val == "0" || val == "FALSE") {
      // This is tricky because usually it's _WDT_OFF
      config_line += " " + key;
    } else {
      config_line += " " + val;
    }
    first = false;
  }
  emit_raw(config_line);
}

void PIC14CodeGen::compile_function(const tacky::Function &func) {
  emit_label(func.name);
  current_bank = -1; // Reset bank state at function entry
  current_block_terminated = false; // Reset dead code flag

  for (const auto &instr: func.body) {
    // Skip emitting if we've hit a terminator
    if (current_block_terminated) {
      // Reset flag on labels (new basic blocks)
      if (std::holds_alternative<tacky::Label>(instr)) {
        current_block_terminated = false;
        compile_instruction(instr);
      }
      // Otherwise skip this dead instruction
      continue;
    }

    compile_instruction(instr);
  }
}

void PIC14CodeGen::compile_instruction(const tacky::Instruction &instr) {
  std::visit([this](auto &&arg) { compile_variant(arg); }, instr);
}

void PIC14CodeGen::load_into_w(const tacky::Val &val) {
  if (std::holds_alternative<std::monostate>(val)) {
    emit("MOVLW", "0x00");
    return;
  }
  if (const auto c = std::get_if<tacky::Constant>(&val)) {
    emit("MOVLW", std::format("0x{:02X}", c->value & 0xFF));
  } else {
    std::string op = resolve_address(val);
    select_bank(op);
    emit("MOVF", op, "W");
  }
}

void PIC14CodeGen::store_w_into(const tacky::Val &val) {
  if (std::holds_alternative<std::monostate>(val)) {
    return;
  }
  std::string op = resolve_address(val);
  select_bank(op);
  emit("MOVWF", op);
}

void PIC14CodeGen::compile_variant(const tacky::Return &arg) {
  load_into_w(arg.value);
  emit("RETURN");
  current_block_terminated = true; // Mark block as terminated
}

void PIC14CodeGen::compile_variant(const tacky::Copy &arg) {
  int size = 1;
  std::string name;
  if (const auto v = std::get_if<tacky::Variable>(&arg.dst))
    name = v->name;
  else if (const auto t = std::get_if<tacky::Temporary>(&arg.dst))
    name = t->name;
  else if (const auto addr = std::get_if<tacky::MemoryAddress>(&arg.dst))
    size = (addr->type == DataType::UINT16) ? 2 : 1; // Basic size check

  if (!name.empty() && var_sizes.contains(name))
    size = var_sizes[name];

  if (size == 1) {
    load_into_w(arg.src);
    store_w_into(arg.dst);
    return;
  }

  if (size == 2) {
    std::string dst_lo = resolve_address(arg.dst);
    std::string dst_hi =
        dst_lo.starts_with("0x")
          ? std::format("0x{:02X}", std::stoi(dst_lo, nullptr, 16) + 1)
          : dst_lo + "+1";

    if (const auto c = std::get_if<tacky::Constant>(&arg.src)) {
      // Copy Constant
      int val = c->value;
      emit("MOVLW", std::format("0x{:02X}", val & 0xFF));
      select_bank(dst_lo);
      emit("MOVWF", dst_lo);

      emit("MOVLW", std::format("0x{:02X}", (val >> 8) & 0xFF));
      select_bank(dst_hi);
      emit("MOVWF", dst_hi);
    } else {
      // Copy Variable/Temp
      std::string src_lo = resolve_address(arg.src);
      std::string src_hi =
          src_lo.starts_with("0x")
            ? std::format("0x{:02X}", std::stoi(src_lo, nullptr, 16) + 1)
            : src_lo + "+1";

      select_bank(src_lo);
      emit("MOVF", src_lo, "W");
      select_bank(dst_lo);
      emit("MOVWF", dst_lo);

      select_bank(src_hi);
      emit("MOVF", src_hi, "W");
      select_bank(dst_hi);
      emit("MOVWF", dst_hi);
    }
    return;
  }

  if (size == 4) {
    if (const auto c = std::get_if<tacky::Constant>(&arg.src)) {
      int val = c->value;
      std::string dst_base = resolve_address(arg.dst);
      for (int i = 0; i < 4; ++i) {
        int byte_val = (val >> (i * 8)) & 0xFF;
        emit("MOVLW", std::format("0x{:02X}", byte_val));
        std::string dst_byte = dst_base;
        if (i > 0) {
          if (dst_base.starts_with("0x"))
            dst_byte =
                std::format("0x{:02X}", std::stoi(dst_base, nullptr, 16) + i);
          else
            dst_byte = dst_base + "+" + std::to_string(i);
        }
        select_bank(dst_byte);
        emit("MOVWF", dst_byte);
      }
      return;
    }
    if (const auto v = std::get_if<tacky::Variable>(&arg.src)) {
      std::string src_base = resolve_address(arg.src);
      std::string dst_base = resolve_address(arg.dst);
      for (int i = 0; i < 4; ++i) {
        std::string src_byte = src_base;
        std::string dst_byte = dst_base;
        if (i > 0) {
          if (src_base.starts_with("0x"))
            src_byte =
                std::format("0x{:02X}", std::stoi(src_base, nullptr, 16) + i);
          else
            src_byte = src_base + "+" + std::to_string(i);

          if (dst_base.starts_with("0x"))
            dst_byte =
                std::format("0x{:02X}", std::stoi(dst_base, nullptr, 16) + i);
          else
            dst_byte = dst_base + "+" + std::to_string(i);
        }
        select_bank(src_byte);
        emit("MOVF", src_byte, "W");
        select_bank(dst_byte);
        emit("MOVWF", dst_byte);
      }
      return;
    }
  }

  throw std::runtime_error("PIC14: Copy only supports 1, 2, 4 bytes");
}

void PIC14CodeGen::compile_variant(const tacky::Unary &arg) {
  load_into_w(arg.src);
  switch (arg.op) {
    case tacky::UnaryOp::BitNot:
      emit("XORLW", "0xFF");
      break;
    case tacky::UnaryOp::Not:
      emit("XORLW", "1");
      emit("ANDLW", "1");
      break;
    case tacky::UnaryOp::Neg:
      emit("SUBLW", "0");
      break;
  }
  store_w_into(arg.dst);
}

void PIC14CodeGen::compile_variant(const tacky::Binary &arg) {
  // Determine operation size
  int size = 1;
  auto get_val_size = [&](const tacky::Val &val) {
    if (const auto v = std::get_if<tacky::Variable>(&val)) {
      if (!v->name.empty() && var_sizes.contains(v->name))
        return var_sizes[v->name];
    } else if (const auto t = std::get_if<tacky::Temporary>(&val)) {
      if (!t->name.empty() && var_sizes.contains(t->name))
        return var_sizes[t->name];
    } else if (const auto m = std::get_if<tacky::MemoryAddress>(&val)) {
      if (m->type == DataType::UINT16)
        return 2;
    } else if (const auto c = std::get_if<tacky::Constant>(&val)) {
      if (c->value > 255 || c->value < 0)
        return 2;
    }
    return 1;
  };

  // For arithmetic, size is determined by dst
  // For comparison, size is determined by operands (max of src1, src2)
  bool is_comparison = false;
  switch (arg.op) {
    case tacky::BinaryOp::Equal:
    case tacky::BinaryOp::NotEqual:
    case tacky::BinaryOp::LessThan:
    case tacky::BinaryOp::LessEqual:
    case tacky::BinaryOp::GreaterThan:
    case tacky::BinaryOp::GreaterEqual:
      is_comparison = true;
      size = std::max(get_val_size(arg.src1), get_val_size(arg.src2));
      break;
    default:
      size = get_val_size(arg.dst);
      break;
  }

  // Check for Float
  auto is_float = [&](const tacky::Val &val) {
    if (auto v = std::get_if<tacky::Variable>(&val))
      return v->type == DataType::FLOAT;
    if (auto t = std::get_if<tacky::Temporary>(&val))
      return t->type == DataType::FLOAT;
    return false;
  };

  if (is_float(arg.dst) || is_float(arg.src1) || is_float(arg.src2)) {
    if (arg.op == tacky::BinaryOp::Add) {
      std::string dst_addr = resolve_address(arg.dst);
      compile_variant(tacky::Copy{arg.src1, arg.dst});
      std::string src2_addr = resolve_address(arg.src2);
      emit_float_add(dst_addr, src2_addr);
      return;
    }
    throw std::runtime_error("PIC14: Float op not supported (only Add)");
  }

  // --- 16-bit Implementation ---
  if (size == 2) {
    if (!is_comparison) {
      // Arithmetic: Delegate to Copy + AugAssign
      // dst = src1; dst op= src2;
      compile_variant(tacky::Copy{arg.src1, arg.dst});

      // AugAssign handles In-Place op
      // Only if op is supported by AugAssign (Add, Sub, Bitwise, Shift)
      switch (arg.op) {
        case tacky::BinaryOp::Add:
        case tacky::BinaryOp::Sub:
        case tacky::BinaryOp::BitAnd:
        case tacky::BinaryOp::BitOr:
        case tacky::BinaryOp::BitXor:
        case tacky::BinaryOp::LShift:
        case tacky::BinaryOp::RShift:
          compile_variant(tacky::AugAssign{arg.op, arg.dst, arg.src2});
          break;
        default:
          throw std::runtime_error("PIC14: 16-bit Binary Op not supported");
      }
      return;
    } else {
      // Comparison (16-bit) -> Result is 8-bit boolean (0 or 1)
      std::string dst_addr = resolve_address(arg.dst);
      select_bank(dst_addr);
      emit("CLRF", dst_addr); // Default to 0 (false)

      // Helper to load byte (high/low)
      auto load_byte = [&](const tacky::Val &v, bool high) {
        if (const auto c = std::get_if<tacky::Constant>(&v)) {
          int val = high ? ((c->value >> 8) & 0xFF) : (c->value & 0xFF);
          emit("MOVLW", std::format("0x{:02X}", val));
        } else {
          std::string addr = resolve_address(v);
          if (high) {
            if (addr.starts_with("0x"))
              addr = std::format("0x{:02X}", std::stoi(addr, nullptr, 16) + 1);
            else
              addr += "+1";
          }
          select_bank(addr);
          emit("MOVF", addr, "W");
        }
      };

      auto get_addr = [&](const tacky::Val &v, bool high) {
        std::string addr = resolve_address(v);
        if (high) {
          if (addr.starts_with("0x"))
            addr = std::format("0x{:02X}", std::stoi(addr, nullptr, 16) + 1);
          else
            addr += "+1";
        }
        return addr;
      };

      std::string label_true = make_label("cmp_true");
      std::string label_end = make_label("cmp_end");

      if (arg.op == tacky::BinaryOp::Equal) {
        // (hi1 == hi2) && (lo1 == lo2)
        load_byte(arg.src2, true);
        std::string src1_hi = get_addr(arg.src1, true);
        if (std::holds_alternative<tacky::Constant>(arg.src1)) {
          emit("XORLW",
               std::format("0x{:02X}",
                           std::get<tacky::Constant>(arg.src1).value >> 8 &
                           0xFF));
        } else {
          select_bank(src1_hi);
          emit("XORWF", src1_hi, "W");
        }
        emit("BTFSS", "STATUS", "2");
        emit("GOTO", label_end);

        load_byte(arg.src2, false);
        std::string src1_lo = get_addr(arg.src1, false);
        if (std::holds_alternative<tacky::Constant>(arg.src1)) {
          emit("XORLW",
               std::format("0x{:02X}",
                           std::get<tacky::Constant>(arg.src1).value & 0xFF));
        } else {
          select_bank(src1_lo);
          emit("XORWF", src1_lo, "W");
        }
        emit("BTFSS", "STATUS", "2");
        emit("GOTO", label_end);

        select_bank(dst_addr);
        emit("INCF", dst_addr, "F");
        emit_label(label_end);
        return;
      }

      if (arg.op == tacky::BinaryOp::NotEqual) {
        load_byte(arg.src2, true);
        std::string src1_hi = get_addr(arg.src1, true);
        if (std::holds_alternative<tacky::Constant>(arg.src1))
          emit("XORLW",
               std::format("0x{:02X}",
                           std::get<tacky::Constant>(arg.src1).value >> 8 &
                           0xFF));
        else {
          select_bank(src1_hi);
          emit("XORWF", src1_hi, "W");
        }
        emit("BTFSS", "STATUS", "2");
        emit("GOTO", label_true);

        load_byte(arg.src2, false);
        std::string src1_lo = get_addr(arg.src1, false);
        if (std::holds_alternative<tacky::Constant>(arg.src1))
          emit("XORLW",
               std::format("0x{:02X}",
                           std::get<tacky::Constant>(arg.src1).value & 0xFF));
        else {
          select_bank(src1_lo);
          emit("XORWF", src1_lo, "W");
        }
        emit("BTFSC", "STATUS", "2");
        emit("GOTO", label_end);

        emit_label(label_true);
        select_bank(dst_addr);
        emit("INCF", dst_addr, "F");
        emit_label(label_end);
        return;
      }

      // <, <=, >, >= (Unsigned assumed)
      load_byte(arg.src2, true);
      std::string src1_hi = get_addr(arg.src1, true);
      if (std::holds_alternative<tacky::Constant>(arg.src1)) {
        emit(
          "SUBLW",
          std::format("0x{:02X}",
                      std::get<tacky::Constant>(arg.src1).value >> 8 & 0xFF));
      } else {
        select_bank(src1_hi);
        emit("SUBWF", src1_hi, "W");
      }

      std::string lbl_chk = make_label("cmp_chk");
      emit("BTFSS", "STATUS", "2");
      emit("GOTO", lbl_chk);

      load_byte(arg.src2, false);
      std::string src1_lo = get_addr(arg.src1, false);
      if (std::holds_alternative<tacky::Constant>(arg.src1)) {
        emit("SUBLW",
             std::format("0x{:02X}",
                         std::get<tacky::Constant>(arg.src1).value & 0xFF));
      } else {
        select_bank(src1_lo);
        emit("SUBWF", src1_lo, "W");
      }

      emit_label(lbl_chk);

      if (arg.op == tacky::BinaryOp::LessThan) {
        emit("BTFSS", "STATUS", "0");
        emit("GOTO", label_true);
      } else if (arg.op == tacky::BinaryOp::GreaterEqual) {
        emit("BTFSC", "STATUS", "0");
        emit("GOTO", label_true);
      } else if (arg.op == tacky::BinaryOp::GreaterThan) {
        emit("BTFSS", "STATUS", "0");
        emit("GOTO", label_end);
        emit("BTFSS", "STATUS", "2");
        emit("GOTO", label_true);
      } else if (arg.op == tacky::BinaryOp::LessEqual) {
        emit("BTFSS", "STATUS", "0");
        emit("GOTO", label_true);
        emit("BTFSC", "STATUS", "2");
        emit("GOTO", label_true);
      }

      emit("GOTO", label_end);
      emit_label(label_true);
      select_bank(dst_addr);
      emit("INCF", dst_addr, "F");
      emit_label(label_end);
      return;
    }
  }

  if (!is_comparison) {
    // --- Shift operations need special handling (file register ops) ---
    if (arg.op == tacky::BinaryOp::LShift ||
        arg.op == tacky::BinaryOp::RShift) {
      // Store source into destination first
      load_into_w(arg.src1);
      store_w_into(arg.dst);

      std::string dst_addr = resolve_address(arg.dst);
      std::string rotate_op =
          (arg.op == tacky::BinaryOp::LShift) ? "RLF" : "RRF";

      if (const auto c2 = std::get_if<tacky::Constant>(&arg.src2)) {
        // Constant shift: unroll N iterations
        int shift_count = c2->value & 0x07; // Max 7 for 8-bit
        for (int i = 0; i < shift_count; i++) {
          emit("BCF", "STATUS", "0"); // Clear Carry
          select_bank(dst_addr);
          emit(rotate_op, dst_addr, "F"); // Rotate in-place
        }
      } else {
        // Variable shift: use a counter loop
        std::string count_addr = resolve_address(arg.src2);
        load_into_w(arg.src2);
        std::string loop_label = make_label("shift");
        std::string done_label = make_label("shift_done");

        // W already has src2, store to a temp? Actually use MOVWF to work with
        // it. For simplicity, count down using W and the file register
        select_bank(count_addr);
        emit("MOVF", count_addr, "W");
        emit("BTFSC", "STATUS", "2"); // Skip if count != 0
        emit("GOTO", done_label);
        emit_label(loop_label);
        emit("BCF", "STATUS", "0");
        select_bank(dst_addr);
        emit(rotate_op, dst_addr, "F");
        select_bank(count_addr);
        emit("DECFSZ", count_addr, "F");
        emit("GOTO", loop_label);
        emit_label(done_label);
      }
      return;
    }

    // Optimization: if src2 is a constant, we can use literal instructions
    if (const auto c2 = std::get_if<tacky::Constant>(&arg.src2)) {
      load_into_w(arg.src1);
      int val = c2->value & 0xFF;
      std::string val_str = std::format("0x{:02X}", val);
      switch (arg.op) {
        case tacky::BinaryOp::Add:
          emit("ADDLW", val_str);
          break;
        case tacky::BinaryOp::BitAnd:
          emit("ANDLW", val_str);
          break;
        case tacky::BinaryOp::BitOr:
          emit("IORLW", val_str);
          break;
        case tacky::BinaryOp::BitXor:
          emit("XORLW", val_str);
          break;
        case tacky::BinaryOp::Sub: {
          // To do W - k, we can do W + (-k)
          int neg_val = (-c2->value) & 0xFF;
          emit("ADDLW", std::format("0x{:02X}", neg_val));
          break;
        }
        default:
          break;
      }

      store_w_into(arg.dst);
      return;
    }

    load_into_w(arg.src2);
    std::string addr1 = resolve_address(arg.src1);

    switch (arg.op) {
      case tacky::BinaryOp::Add:
        if (std::holds_alternative<tacky::Constant>(arg.src1)) {
          emit("ADDLW", addr1);
        } else {
          select_bank(addr1);
          emit("ADDWF", addr1, "W");
        }
        break;
      case tacky::BinaryOp::Sub:
        if (const auto c1 = std::get_if<tacky::Constant>(&arg.src1)) {
          // SUBLW k computes k - W -> W.
          emit("SUBLW", std::format("0x{:02X}", c1->value & 0xFF));
        } else {
          // Variable - Variable: SUBWF f, W -> f - W
          select_bank(addr1);
          emit("SUBWF", addr1, "W");
        }
        break;
      case tacky::BinaryOp::BitAnd:
        if (std::holds_alternative<tacky::Constant>(arg.src1)) {
          emit("ANDLW", addr1);
        } else {
          select_bank(addr1);
          emit("ANDWF", addr1, "W");
        }
        break;
      case tacky::BinaryOp::BitOr:
        if (std::holds_alternative<tacky::Constant>(arg.src1)) {
          emit("IORLW", addr1);
        } else {
          select_bank(addr1);
          emit("IORWF", addr1, "W");
        }
        break;
      case tacky::BinaryOp::BitXor:
        if (std::holds_alternative<tacky::Constant>(arg.src1)) {
          emit("XORLW", addr1);
        } else {
          select_bank(addr1);
          emit("XORWF", addr1, "W");
        }
        break;
      default:
        break;
    }
    // Critical Fix: Enforce storage of result
    store_w_into(arg.dst);
    return;
  }

  load_into_w(arg.src2);

  std::string addr1 = resolve_address(arg.src1);
  select_bank(addr1);
  emit("SUBWF", addr1, "W");

  std::string dst_addr = resolve_address(arg.dst);
  select_bank(dst_addr);
  emit("CLRF", dst_addr);

  switch (arg.op) {
    case tacky::BinaryOp::Equal:
      emit("BTFSC", "STATUS", "2");
      break;
    case tacky::BinaryOp::NotEqual:
      emit("BTFSS", "STATUS", "2");
      break;
    case tacky::BinaryOp::LessThan:
      emit("BTFSS", "STATUS", "0");
      break;
    case tacky::BinaryOp::GreaterEqual:
      emit("BTFSC", "STATUS", "0");
      break;
    case tacky::BinaryOp::GreaterThan: {
      std::string lbl_skip = make_label("L_GT");
      emit("BTFSS", "STATUS", "0");
      emit("GOTO", lbl_skip);
      emit("BTFSC", "STATUS", "2");
      emit("GOTO", lbl_skip);
      emit("INCF", dst_addr, "F");
      emit_label(lbl_skip);
      return;
    }
    case tacky::BinaryOp::LessEqual: {
      std::string lbl_set = make_label("L_LE");
      std::string lbl_skip = make_label("L_LES");
      emit("BTFSS", "STATUS", "0");
      emit("GOTO", lbl_set);
      emit("BTFSS", "STATUS", "2");
      emit("GOTO", lbl_skip);
      emit_label(lbl_set);
      emit("INCF", dst_addr, "F");
      emit_label(lbl_skip);
      return;
    }
    default:
      break;
  }
  emit("INCF", dst_addr, "F");
}

void PIC14CodeGen::compile_variant(const tacky::BitSet &arg) {
  std::string addr = resolve_address(arg.target);
  select_bank(addr);
  emit("BSF", addr, std::to_string(arg.bit));
}

void PIC14CodeGen::compile_variant(const tacky::BitClear &arg) {
  std::string addr = resolve_address(arg.target);
  select_bank(addr);
  emit("BCF", addr, std::to_string(arg.bit));
}

void PIC14CodeGen::compile_variant(const tacky::BitCheck &arg) {
  std::string addr = resolve_address(arg.source);
  select_bank(addr);

  std::string dst_addr = resolve_address(arg.dst);
  {
    select_bank(dst_addr);
    emit("CLRF", dst_addr);

    emit("BTFSC", addr, std::to_string(arg.bit));

    select_bank(dst_addr);
    emit("INCF", dst_addr, "F");
  }
}

void PIC14CodeGen::compile_variant(const tacky::BitWrite &arg) {
  if (const auto c = std::get_if<tacky::Constant>(&arg.src)) {
    std::string addr = resolve_address(arg.target);
    select_bank(addr);
    if (c->value != 0) {
      emit("BSF", addr, std::to_string(arg.bit));
    } else {
      emit("BCF", addr, std::to_string(arg.bit));
    }
    return;
  }

  load_into_w(arg.src);
  emit("IORLW", "0");

  std::string addr = resolve_address(arg.target);
  std::string bit_str = std::to_string(arg.bit);
  std::string lbl_zero = make_label("L_BZ");
  std::string lbl_end = make_label("L_BE");

  emit("BTFSC", "STATUS", "2"); // Skip if not zero
  emit("GOTO", lbl_zero);

  select_bank(addr);
  emit("BSF", addr, bit_str);
  emit("GOTO", lbl_end);

  emit_label(lbl_zero);
  select_bank(addr);
  emit("BCF", addr, bit_str);

  emit_label(lbl_end);
}

void PIC14CodeGen::compile_variant(const tacky::Label &arg) const {
  emit_label(arg.name);
  // Label could be a jump target from anywhere, so bank state is unknown.
  const_cast<PIC14CodeGen *>(this)->current_bank = -1;
}

void PIC14CodeGen::compile_variant(const tacky::Jump &arg) const {
  emit("GOTO", arg.target);
}

void PIC14CodeGen::compile_variant(const tacky::JumpIfZero &arg) {
  if (const auto c = std::get_if<tacky::Constant>(&arg.condition)) {
    if (c->value == 0) {
      emit("GOTO", arg.target);
    }
    return;
  }
  load_into_w(arg.condition);
  emit("IORLW", "0");
  emit("BTFSC", "STATUS", "2");
  emit("GOTO", arg.target);
}

void PIC14CodeGen::compile_variant(const tacky::JumpIfNotZero &arg) {
  if (const auto c = std::get_if<tacky::Constant>(&arg.condition)) {
    if (c->value != 0) {
      emit("GOTO", arg.target);
    }
    return;
  }
  load_into_w(arg.condition);
  emit("IORLW", "0");
  emit("BTFSS", "STATUS", "2");
  emit("GOTO", arg.target);
}

void PIC14CodeGen::compile_variant(const tacky::Call &arg) {
  emit("CALL", arg.function_name);
  if (!std::holds_alternative<tacky::Variable>(arg.dst) &&
      !std::holds_alternative<tacky::Temporary>(arg.dst)) {
  } else {
    store_w_into(arg.dst);
  }
}

void PIC14CodeGen::compile_variant(const tacky::JumpIfBitSet &arg) {
  std::string addr = resolve_address(arg.source);
  select_bank(addr);
  // BTFSC: skip next if bit is CLEAR (i.e., execute GOTO only when bit is SET)
  emit("BTFSC", addr, std::to_string(arg.bit));
  emit("GOTO", arg.target);
}

void PIC14CodeGen::compile_variant(const tacky::JumpIfBitClear &arg) {
  std::string addr = resolve_address(arg.source);
  select_bank(addr);
  // BTFSS: skip next if bit is SET (i.e., execute GOTO only when bit is CLEAR)
  emit("BTFSS", addr, std::to_string(arg.bit));
  emit("GOTO", arg.target);
}

void PIC14CodeGen::compile_variant(const tacky::AugAssign &arg) {
  std::string target_addr = resolve_address(arg.target);
  std::string target_addr_hi = target_addr;

  // Determine size
  int size = 1;
  std::string var_name;
  if (const auto v = std::get_if<tacky::Variable>(&arg.target)) {
    var_name = v->name;
    if (var_sizes.contains(var_name))
      size = var_sizes[var_name];
  } else if (const auto t = std::get_if<tacky::Temporary>(&arg.target)) {
    var_name = t->name;
    if (var_sizes.contains(var_name))
      size = var_sizes[var_name];
  }

  // Handle address strings for high byte
  if (size > 1) {
    if (target_addr.starts_with("0x")) {
      int addr = std::stoi(target_addr, nullptr, 16);
      target_addr_hi = std::format("0x{:02X}", addr + 1);
    } else {
      target_addr_hi = target_addr + "+1";
    }
  }

  // --- 8-bit Implementation (Original Fast Path) ---
  if (size == 1) {
    select_bank(target_addr);
    load_into_w(arg.operand);

    switch (arg.op) {
      case tacky::BinaryOp::Add:
        emit("ADDWF", target_addr, "F");
        break;
      case tacky::BinaryOp::Sub:
        emit("SUBWF", target_addr, "F");
        break;
      case tacky::BinaryOp::BitAnd:
        emit("ANDWF", target_addr, "F");
        break;
      case tacky::BinaryOp::BitOr:
        emit("IORWF", target_addr, "F");
        break;
      case tacky::BinaryOp::BitXor:
        emit("XORWF", target_addr, "F");
        break;
      case tacky::BinaryOp::LShift: {
        std::string loop_lbl = make_label("augls");
        std::string done_lbl = make_label("augls_done");
        emit("MOVWF", "__tmp");
        emit("MOVF", "__tmp", "F");
        emit("BTFSC", "STATUS", "2");
        emit("GOTO", done_lbl);
        emit_label(loop_lbl);
        emit("BCF", "STATUS", "0");
        emit("RLF", target_addr, "F");
        emit("DECFSZ", "__tmp", "F");
        emit("GOTO", loop_lbl);
        emit_label(done_lbl);
        break;
      }
      case tacky::BinaryOp::RShift: {
        std::string loop_lbl = make_label("augrs");
        std::string done_lbl = make_label("augrs_done");
        emit("MOVWF", "__tmp");
        emit("MOVF", "__tmp", "F");
        emit("BTFSC", "STATUS", "2");
        emit("GOTO", done_lbl);
        emit_label(loop_lbl);
        emit("BCF", "STATUS", "0");
        emit("RRF", target_addr, "F");
        emit("DECFSZ", "__tmp", "F");
        emit("GOTO", loop_lbl);
        emit_label(done_lbl);
        break;
      }
      default:
        throw std::runtime_error("PIC14: AugAssign 8-bit op not implemented");
    }
    return;
  }

  // --- 16-bit Implementation ---
  if (size == 2) {
    // 1. Bitwise Ops (Apply to both bytes)
    if (arg.op == tacky::BinaryOp::BitAnd || arg.op == tacky::BinaryOp::BitOr ||
        arg.op == tacky::BinaryOp::BitXor) {
      int val_lo = 0, val_hi = 0;
      bool is_const = false;
      if (const auto c = std::get_if<tacky::Constant>(&arg.operand)) {
        val_lo = c->value & 0xFF;
        val_hi = (c->value >> 8) & 0xFF;
        is_const = true;
      }

      // Low byte
      std::string op_str = (arg.op == tacky::BinaryOp::BitAnd)
                             ? "ANDWF"
                             : (arg.op == tacky::BinaryOp::BitOr)
                                 ? "IORWF"
                                 : "XORWF";
      std::string lit_op_str = (arg.op == tacky::BinaryOp::BitAnd)
                                 ? "ANDLW"
                                 : (arg.op == tacky::BinaryOp::BitOr)
                                     ? "IORLW"
                                     : "XORLW";

      select_bank(target_addr);
      if (is_const)
        emit(lit_op_str, std::format("0x{:02X}", val_lo));
      else {
        // Load operand low
        // TODO: Need helper to load operand low/high for variables
        load_into_w(arg.operand); // This loads low byte currently
      }
      if (!is_const)
        emit(op_str, target_addr, "F");
      else {
        // For literals, we loaded W with literal, now apply to F
        // wait, ANDLW modifies W. ANDWF modifies F or W.
        // ANDLW k -> W = W & k.
        // We want target &= k.
        // MOVF target, W; ANDLW k; MOVWF target.
        emit("MOVF", target_addr, "W");
        emit(lit_op_str, std::format("0x{:02X}", val_lo));
        emit("MOVWF", target_addr);
      }

      // High byte
      select_bank(target_addr_hi);
      if (is_const) {
        emit("MOVF", target_addr_hi, "W");
        emit(lit_op_str, std::format("0x{:02X}", val_hi));
        emit("MOVWF", target_addr_hi);
      } else {
        // Load operand high...
        // We need generic load_byte(val, offset) support
        throw std::runtime_error(
          "PIC14: 16-bit AugAssign non-const bitwise not fully implemented");
      }
      return;
    }

    // 2. Arithmetic (Add/Sub)
    if (arg.op == tacky::BinaryOp::Add) {
      // target += operand
      // Add LSB
      if (const auto c = std::get_if<tacky::Constant>(&arg.operand)) {
        emit("MOVLW", std::format("0x{:02X}", c->value & 0xFF));
        select_bank(target_addr);
        emit("ADDWF", target_addr, "F");
        // Carry
        emit("BTFSC", "STATUS", "0");
        emit("INCF", target_addr_hi, "F");
        // Add MSB
        emit("MOVLW", std::format("0x{:02X}", (c->value >> 8) & 0xFF));
        select_bank(target_addr_hi);
        emit("ADDWF", target_addr_hi, "F");
      } else {
        // Variable
        throw std::runtime_error(
          "PIC14: 16-bit AugAssign var addition not fully implemented");
      }
      return;
    }

    if (arg.op == tacky::BinaryOp::Sub) {
      int val_lo = 0, val_hi = 0;
      bool is_const = false;
      if (const auto c = std::get_if<tacky::Constant>(&arg.operand)) {
        val_lo = c->value & 0xFF;
        val_hi = (c->value >> 8) & 0xFF;
        is_const = true;
      }

      if (is_const)
        emit("MOVLW", std::format("0x{:02X}", val_lo));
      else
        load_into_w(arg.operand);

      select_bank(target_addr);
      emit("SUBWF", target_addr, "F");

      // If borrow (C=0), decrement hi
      select_bank(target_addr_hi);
      emit("BTFSS", "STATUS", "0");
      emit("DECF", target_addr_hi, "F");

      if (is_const) {
        emit("MOVLW", std::format("0x{:02X}", val_hi));
        emit("SUBWF", target_addr_hi, "F");
      } else {
        if (const auto v = std::get_if<tacky::Variable>(&arg.operand)) {
          std::string op_hi = v->name + "+1";
          select_bank(op_hi);
          emit("MOVF", op_hi, "W");
          select_bank(target_addr_hi);
          emit("SUBWF", target_addr_hi, "F");
        }
      }
      return;
    }

    if (arg.op == tacky::BinaryOp::LShift) {
      // Loop and shift both bytes
      std::string loop_lbl = make_label("augls16");
      std::string done_lbl = make_label("augls16_done");

      // Load shift count into __tmp
      load_into_w(arg.operand);
      emit("MOVWF", "__tmp");

      emit_label(loop_lbl);
      emit("MOVF", "__tmp", "F");
      emit("BTFSC", "STATUS", "2"); // Check if 0
      emit("GOTO", done_lbl);

      emit("BCF", "STATUS", "0"); // Clear carry
      select_bank(target_addr);
      emit("RLF", target_addr, "F");
      select_bank(target_addr_hi);
      emit("RLF", target_addr_hi, "F"); // Rotate through carry

      emit("DECF", "__tmp", "F");
      emit("GOTO", loop_lbl);
      emit_label(done_lbl);
      return;
    }

    if (arg.op == tacky::BinaryOp::RShift) {
      // Loop and shift both bytes
      std::string loop_lbl = make_label("augrs16");
      std::string done_lbl = make_label("augrs16_done");

      load_into_w(arg.operand);
      emit("MOVWF", "__tmp");

      emit_label(loop_lbl);
      emit("MOVF", "__tmp", "F");
      emit("BTFSC", "STATUS", "2");
      emit("GOTO", done_lbl);

      emit("BCF", "STATUS", "0");
      select_bank(target_addr_hi); // MSB first for RShift
      emit("RRF", target_addr_hi, "F");
      select_bank(target_addr);
      emit("RRF", target_addr, "F"); // Rotate through carry

      emit("DECF", "__tmp", "F");
      emit("GOTO", loop_lbl);
      emit_label(done_lbl);
      return;
    }
  }

  throw std::runtime_error("PIC14: AugAssign size/op not fully implemented");
}

void PIC14CodeGen::emit_float_add(const std::string &target,
                                  const std::string &source) {
  // Move 4 bytes of source to __FP_B
  std::string src_base = source;
  for (int i = 0; i < 4; ++i) {
    std::string src_byte = src_base;
    if (i > 0) {
      if (src_base.starts_with("0x"))
        src_byte =
            std::format("0x{:02X}", std::stoi(src_base, nullptr, 16) + i);
      else
        src_byte = src_base + "+" + std::to_string(i);
    }

    // Load byte to W
    if (src_base.starts_with("0x")) {
      select_bank(src_byte);
      emit("MOVF", src_byte, "W");
    } else {
      select_bank(src_byte);
      emit("MOVF", src_byte, "W");
    }

    emit("MOVWF", "__FP_B+" + std::to_string(i));
  }

  // Move 4 bytes of target to __FP_A
  std::string tgt_base = target;
  for (int i = 0; i < 4; ++i) {
    std::string tgt_byte = tgt_base;
    if (i > 0) {
      if (tgt_base.starts_with("0x"))
        tgt_byte =
            std::format("0x{:02X}", std::stoi(tgt_base, nullptr, 16) + i);
      else
        tgt_byte = tgt_base + "+" + std::to_string(i);
    }

    select_bank(tgt_byte);
    emit("MOVF", tgt_byte, "W");
    emit("MOVWF", "__FP_A+" + std::to_string(i));
  }

  emit("CALL", "__add_float");

  // Move 4 bytes of __FP_A back to target
  for (int i = 0; i < 4; ++i) {
    std::string tgt_byte = tgt_base;
    if (i > 0) {
      if (tgt_base.starts_with("0x"))
        tgt_byte =
            std::format("0x{:02X}", std::stoi(tgt_base, nullptr, 16) + i);
      else
        tgt_byte = tgt_base + "+" + std::to_string(i);
    }

    emit("MOVF", "__FP_A+" + std::to_string(i), "W");
    select_bank(tgt_byte);
    emit("MOVWF", tgt_byte);
  }
}

void PIC14CodeGen::compile_variant(const tacky::Delay &arg) {
  if (std::holds_alternative<tacky::Constant>(arg.target)) {
    // Case A: Constant Delay
    unsigned long duration = std::get<tacky::Constant>(arg.target).value;
    unsigned long long cycles;
    // Tcy = 4 / Fosc
    // Inst = Time * (Fosc / 4)
    if (arg.is_ms) {
      cycles =
          (static_cast<unsigned long long>(duration) * config.frequency) / 4000;
    } else {
      cycles = (static_cast<unsigned long long>(duration) * config.frequency) /
               4000000;
      if (cycles == 0 && duration > 0)
        cycles = 1; // At least 1 cycle for minimal delay
    }
    emit_comment(std::format("Delay {} {}", duration, arg.is_ms ? "ms" : "us"));
    emit_delay_cycles(static_cast<unsigned long>(cycles));
  } else {
    // Case B: Variable Delay
    if (arg.is_ms) {
      needs_delay_1ms = true;
      std::string loop_ctr = get_or_alloc_variable("__delay_ms_ctr");

      // delay_ms(var) implementation:
      // Loop N times calling __delay_1ms_base
      // Overhead Analysis:
      //   MOVF   (1)
      //   MOVWF  (1)
      //   ... check 0 ...
      // Loop:
      //   CALL   (2) -> __delay_1ms_base
      //   DECFSZ (1)
      //   GOTO   (2)
      //
      // Total Loop Overhead = 5 cycles + 1ms_base.
      // We tune __delay_1ms_base to be (1ms_cycles - 5).
      // (Previous analysis said 7? CALL(2)+RET(2)+DECFSZ(1)+GOTO(2) = 7.
      //  __delay_1ms_base includes RET(2). So Caller overhead is
      //  CALL(2)+DECFSZ(1)+GOTO(2) = 5. Wait, __delay_1ms_base BODY + RET =
      //  cost. Total = CALL + BODY + RET + DECFSZ + GOTO. Total = 2 + BODY + 2
      //  + 1 + 2 = BODY + 7. So we want BODY + 7 = 1ms_cycles. BODY =
      //  1ms_cycles - 7. Correct.

      load_into_w(arg.target);
      emit("MOVWF", loop_ctr);

      std::string loop_label = make_label("dly_ms_loop");
      std::string end_label = make_label("dly_ms_end");

      // Check for 0 handling
      emit("MOVF", loop_ctr, "F");
      emit("BTFSC", "STATUS", "2"); // Check Z bit
      emit("GOTO", end_label);

      emit_label(loop_label);
      emit("CALL", "__delay_1ms_base");
      emit("DECFSZ", loop_ctr, "F");
      emit("GOTO", loop_label);
      emit_label(end_label);
    } else {
      throw std::runtime_error(
        "Variable delay_us() is not supported on PIC14/16 (timing "
        "constraints). Use delay_ms() or constant delay_us().");
    }
  }
}

void PIC14CodeGen::emit_delay_cycles(unsigned long cycles) {
  if (cycles == 0)
    return;

  // Simple heuristic:
  // Use NOPs for small counts
  while (cycles > 0 && cycles < 10) {
    if (cycles >= 2) {
      emit("GOTO", "$+1");
      cycles -= 2;
    } else {
      emit("NOP");
      cycles--;
    }
  }

  if (cycles == 0)
    return;

  if (cycles < 770) {
    // 1 Level
    unsigned long k = (cycles - 1) / 3;
    unsigned long rem = cycles - (3 * k + 1);

    std::string c1 = get_or_alloc_variable("__dly_c1");

    emit("MOVLW", std::format("0x{:02X}", k & 0xFF));
    emit("MOVWF", c1);
    std::string l = make_label("dly1");
    emit_label(l);
    emit("DECFSZ", c1, "F");
    emit("GOTO", l);

    emit_delay_cycles(rem); // Recurse for remainder
  } else if (cycles < 197000) {
    // 2 Levels
    unsigned long k2 = cycles / 770;
    if (k2 > 255)
      k2 = 255;
    if (k2 == 0)
      k2 = 1;

    unsigned long used = k2 * 770;

    std::string c1 = get_or_alloc_variable("__dly_c1");
    std::string c2 = get_or_alloc_variable("__dly_c2");

    emit("MOVLW", std::format("0x{:02X}", k2 & 0xFF));
    emit("MOVWF", c2);
    emit("MOVLW", "0xFF"); // 256 loops
    emit("MOVWF", c1);

    // Logic check:
    // Outer loop (c2) runs k2 times.
    // Inner loop (c1) runs 256 times per outer.
    // Inner: 256*3 -1 + unrolled?
    // Previous code: emit("INCF", c1, "F"); // 255->0
    emit("INCF", c1, "F");

    std::string l = make_label("dly2");
    emit_label(l);
    emit("DECFSZ", c1, "F");
    emit("GOTO", l);
    emit("DECFSZ", c2, "F");
    emit("GOTO", l);

    emit_delay_cycles(cycles - used);
  } else {
    // 3 Levels
    unsigned long k3 = cycles / 197120; // 256 * 770
    if (k3 > 255)
      k3 = 255;
    if (k3 == 0)
      k3 = 1;

    std::string c1 = get_or_alloc_variable("__dly_c1");
    std::string c2 = get_or_alloc_variable("__dly_c2");
    std::string c3 = get_or_alloc_variable("__dly_c3");

    emit("MOVLW", std::format("0x{:02X}", k3 & 0xFF));
    emit("MOVWF", c3);
    emit("CLRF", c2); // 0 -> 256 loops
    emit("CLRF", c1);

    std::string l = make_label("dly3");
    emit_label(l);
    emit("DECFSZ", c1, "F");
    emit("GOTO", l);
    emit("DECFSZ", c2, "F");
    emit("GOTO", l);
    emit("DECFSZ", c3, "F");
    emit("GOTO", l);

    unsigned long used = k3 * 197120;
    emit_delay_cycles(cycles - used);
  }
}

void PIC14CodeGen::compile_variant(const tacky::DebugLine &arg) {
  emit_comment(std::format("Line {}: {}", arg.line, arg.text));
}

// ... (end of file helper needed? No, we are replacing the bottom chunk)
// Wait, I need to match the replacement chunk exactly.
// The replace tool works on line ranges.
// I will target the `compile_variant(Delay)` and `emit_delay_cycles`
// separately? Or rewrite the end of the file. Lines 1259 to 1377 is a huge
// chunk. I'll do it in one pass if I overlap correctly. Actually,
// `emit_delay_cycles` was ALREADY CORRECT in the file. I only need to change
// `compile_variant(Delay)` and `compile` (at end). And `emit_delay_cycles` is
// essentially unchanged except I might have copy-pasted it. I will use
// replace_file_content on `compile_variant(Delay)` block and `compile` block
// separately to be safer/cleaner? But `emit_delay_cycles` is between them? No,
// `emit_delay_cycles` is at the end? Let's check structure. 1239:
// compile_variant(Delay) 1285: emit_delay_cycles 1371: (inside compile) ...
// needs_delay_1ms logic. Wait, `compile` is way up at line 94! Lines 1371-1379
// were shown in the *first* view_file (lines 1-800?? No wait). Step 18 showed
// 1-800. Step 24 showed 801-1377. `compile` body is lines 94-189. The block `if
// (needs_delay_1ms)` is at 171-179. So I need TO DISTINCT EDITS.

// Edit 1: Update `compile_variant(Delay)` (lines 1259-1282).
// Edit 2: Update `compile` (lines 173-177).

// Let's do Edit 2 first (smaller).
void PIC14CodeGen::emit_context_save() {
  emit_comment("Context Save");
  emit("MOVWF", "W_TEMP");
  emit("SWAPF", "STATUS", "W");
  emit("MOVWF", "STATUS_TEMP");
}

void PIC14CodeGen::emit_context_restore() {
  emit_comment("Context Restore");
  emit("SWAPF", "STATUS_TEMP", "W");
  emit("MOVWF", "STATUS");
  emit("SWAPF", "W_TEMP", "F");
  emit("SWAPF", "W_TEMP", "W");
}

void PIC14CodeGen::emit_interrupt_return() { emit("RETFIE"); }
