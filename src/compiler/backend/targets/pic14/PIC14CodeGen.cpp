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
  try {
    if (operand.size() > 2 && operand.substr(0, 2) == "0x") {
      int addr = std::stoi(operand, nullptr, 16);

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
    }
  } catch (...) {
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
  this->stack_layout = offsets;

  std::string chip_upper = config.chip;
  if (chip_upper.starts_with("pic"))
    chip_upper = chip_upper.substr(3);
  std::ranges::transform(chip_upper, chip_upper.begin(), ::toupper);

  emit_raw(std::format("\tLIST P={}", chip_upper));
  emit_raw(std::format("\t#include <P{}.INC>", chip_upper));

  emit_config_directives();

  emit_comment("--- Compiled Stack (Overlays) ---");
  emit_raw("\tCBLOCK 0x20");
  emit_raw(std::format("_stack_base: {}", total_size));
  emit_raw("\tENDC");

  emit_raw("");
  emit_comment("--- Variable Offsets ---");
  for (const auto &[name, offset] : stack_layout) {
    emit_raw(std::format("{} EQU _stack_base + {}", name, offset));
  }

  emit_raw("");
  emit_comment("--- Code ---");
  emit_raw("\tORG 0x00");
  emit("GOTO", "main");
  emit_raw("\tORG 0x04");
  emit_label("__interrupt");
  emit("RETFIE");

  for (const auto &func : program.functions) {
    compile_function(func);
  }

  emit_raw("\tEND");

  // Optimization step
  auto optimized = PIC14Peephole::optimize(assembly);

  for (const auto &line : optimized) {
    os << line.to_string() << "\n";
  }
}

void PIC14CodeGen::emit_config_directives() {
  if (config.fuses.empty())
    return;

  std::string config_line = "\t__CONFIG";
  bool first = true;
  for (const auto &[key, val] : config.fuses) {
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
  current_bank = -1;
  for (const auto &instr : func.body) {
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
}

void PIC14CodeGen::compile_variant(const tacky::Copy &arg) {
  load_into_w(arg.src);
  store_w_into(arg.dst);
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
  bool is_comparison = false;
  switch (arg.op) {
  case tacky::BinaryOp::Equal:
  case tacky::BinaryOp::NotEqual:
  case tacky::BinaryOp::LessThan:
  case tacky::BinaryOp::LessEqual:
  case tacky::BinaryOp::GreaterThan:
  case tacky::BinaryOp::GreaterEqual:
    is_comparison = true;
    break;
  default:
    break;
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
        // Constant + Var: ADDLW is commutative, just use literal
        emit("ADDLW", addr1);
      } else {
        select_bank(addr1);
        emit("ADDWF", addr1, "W");
      }
      break;
    case tacky::BinaryOp::Sub:
      if (const auto c1 = std::get_if<tacky::Constant>(&arg.src1)) {
        // Literal - Variable: W has src2, we need k - W
        // SUBLW k computes k - W -> W
        emit("SUBLW", std::format("0x{:02X}", c1->value & 0xFF));
      } else {
        // Variable - Variable: SUBWF f, W computes f - W -> W
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