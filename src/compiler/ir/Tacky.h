#ifndef TACKY_H
#define TACKY_H

#pragma once
#include "DataType.h"
#include <memory>
#include <string>
#include <variant>
#include <vector>

namespace tacky {
  // --- Operand Types ---
  struct Constant {
    int value;
  };

  struct FloatConstant {
    double value;
  };

  struct Variable {
    std::string name;
    DataType type = DataType::UINT8;
  };

  struct Temporary {
    std::string name;
    DataType type = DataType::UINT8;
  };

  // Represents a physical memory address (MMIO or Static Global)
  struct MemoryAddress {
    int address;
    DataType type = DataType::UINT8;
  };

  using Val = std::variant<Constant, FloatConstant, Variable, Temporary,
    MemoryAddress, std::monostate>;

  enum class UnaryOp { Not, Neg, BitNot };

  enum class BinaryOp {
    Add,
    Sub,
    Mul,
    Div,
    Mod,
    Equal,
    NotEqual,
    LessThan,
    LessEqual,
    GreaterThan,
    GreaterEqual,
    BitAnd,
    BitOr,
    BitXor,
    LShift,
    RShift
  };

  // --- Instructions ---

  struct Return {
    Val value;
  };

  struct Unary {
    UnaryOp op;
    Val src;
    Val dst;
  };

  struct Binary {
    BinaryOp op;
    Val src1;
    Val src2;
    Val dst;
  };

  struct Copy {
    Val src;
    Val dst;
  };

  struct Jump {
    std::string target;
  };

  struct JumpIfZero {
    Val condition;
    std::string target;
  };

  struct JumpIfNotZero {
    Val condition;
    std::string target;
  };

  struct Label {
    std::string name;
  };

  struct Call {
    std::string function_name;
    std::vector<Val> args;
    Val dst;
  };

  struct BitSet {
    Val target;
    int bit;
  };

  struct BitClear {
    Val target;
    int bit;
  };

  struct BitCheck {
    Val source;
    int bit;
    Val dst;
  };

  struct BitWrite {
    Val target;
    int bit;
    Val src;
  };

  // Optimized conditional jumps on bit state (for tight polling loops)
  struct JumpIfBitSet {
    Val source; // Register to test
    int bit; // Bit index
    std::string target; // Jump destination
  };

  struct JumpIfBitClear {
    Val source;
    int bit;
    std::string target;
  };

  // Augmented assignment: target op= operand (in-place modification)
  struct AugAssign {
    BinaryOp op;
    Val target; // Both source and destination
    Val operand;
  };

  // Timing
  struct Delay {
    Val target; // Duration
    bool is_ms;
  };

  // Debugging
  struct DebugLine {
    int line;
    std::string text;
  };

  // --- The Instruction Container ---
  using Instruction =
  std::variant<Return, Unary, Binary, Copy, Jump, JumpIfZero, JumpIfNotZero,
    Label, Call, BitSet, BitClear, BitCheck, BitWrite,
    JumpIfBitSet, JumpIfBitClear, AugAssign, Delay, DebugLine>;

  // --- Function Definition ---
  struct Function {
    std::string name;
    std::vector<std::string> params;
    std::vector<Instruction> body;
    bool is_inline = false;
    bool is_interrupt = false;
    int interrupt_vector = 0;
  };

  struct Program {
    std::vector<Variable> globals; // Mutable global variable names needing RAM
    std::vector<Function> functions;
  };
} // namespace tacky

#endif // TACKY_H
