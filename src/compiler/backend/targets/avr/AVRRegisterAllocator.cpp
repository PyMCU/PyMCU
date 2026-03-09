/*
 * PyMCU Compiler — AVR Register Allocator
 * Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
 *
 * Licensed under the GNU General Public License v3. See LICENSE for details.
 */

#include "AVRRegisterAllocator.h"

#include <algorithm>
#include <format>
#include <map>
#include <string>
#include <variant>
#include <vector>

static int size_of_type(DataType t) {
  return (t == DataType::UINT16 || t == DataType::INT16) ? 2 : 1;
}

std::map<std::string, std::string> AVRRegisterAllocator::allocate(
    const tacky::Program &program) {

  // Count uses of each named Variable across all functions.
  // Temporaries (tmp_N) are excluded — they are handled by the peephole's
  // STD/LDD forwarding pass, which eliminates their RAM traffic at no cost.
  std::map<std::string, int> use_count;
  std::map<std::string, DataType> var_types;

  // Returns the number of '.' characters in a string.
  auto dot_count = [](const std::string &s) {
    return static_cast<int>(std::count(s.begin(), s.end(), '.'));
  };

  auto count_val = [&](const tacky::Val &val) {
    if (auto *v = std::get_if<tacky::Variable>(&val)) {
      // Inline parameter transfer slots have names like "inline1.write.data"
      // (2+ dots). Exclude them — they exist only to pass values into @inline
      // call frames and should not occupy scratch registers.
      if (dot_count(v->name) >= 2) return;
      use_count[v->name]++;
      var_types[v->name] = v->type;
    }
  };

  for (const auto &func : program.functions) {
    for (const auto &instr : func.body) {
      std::visit(
          [&](auto &&arg) {
            using T = std::decay_t<decltype(arg)>;
            if constexpr (std::is_same_v<T, tacky::Copy>) {
              count_val(arg.src);
              count_val(arg.dst);
            } else if constexpr (std::is_same_v<T, tacky::Binary>) {
              count_val(arg.src1);
              count_val(arg.src2);
              count_val(arg.dst);
            } else if constexpr (std::is_same_v<T, tacky::Unary>) {
              count_val(arg.src);
              count_val(arg.dst);
            } else if constexpr (std::is_same_v<T, tacky::Return>) {
              count_val(arg.value);
            } else if constexpr (std::is_same_v<T, tacky::JumpIfZero>) {
              count_val(arg.condition);
            } else if constexpr (std::is_same_v<T, tacky::JumpIfNotZero>) {
              count_val(arg.condition);
            } else if constexpr (std::is_same_v<T, tacky::JumpIfEqual>) {
              count_val(arg.src1);
              count_val(arg.src2);
            } else if constexpr (std::is_same_v<T, tacky::JumpIfNotEqual>) {
              count_val(arg.src1);
              count_val(arg.src2);
            } else if constexpr (std::is_same_v<T, tacky::JumpIfLessThan>) {
              count_val(arg.src1);
              count_val(arg.src2);
            } else if constexpr (std::is_same_v<T, tacky::JumpIfLessOrEqual>) {
              count_val(arg.src1);
              count_val(arg.src2);
            } else if constexpr (std::is_same_v<T, tacky::JumpIfGreaterThan>) {
              count_val(arg.src1);
              count_val(arg.src2);
            } else if constexpr (std::is_same_v<T, tacky::JumpIfGreaterOrEqual>) {
              count_val(arg.src1);
              count_val(arg.src2);
            } else if constexpr (std::is_same_v<T, tacky::BitCheck>) {
              count_val(arg.source);
              count_val(arg.dst);
            } else if constexpr (std::is_same_v<T, tacky::BitWrite>) {
              count_val(arg.target);
              count_val(arg.src);
            } else if constexpr (std::is_same_v<T, tacky::BitSet>) {
              count_val(arg.target);
            } else if constexpr (std::is_same_v<T, tacky::BitClear>) {
              count_val(arg.target);
            } else if constexpr (std::is_same_v<T, tacky::AugAssign>) {
              count_val(arg.target);
              count_val(arg.operand);
            } else if constexpr (std::is_same_v<T, tacky::JumpIfBitSet>) {
              count_val(arg.source);
            } else if constexpr (std::is_same_v<T, tacky::JumpIfBitClear>) {
              count_val(arg.source);
            } else if constexpr (std::is_same_v<T, tacky::Call>) {
              count_val(arg.dst);
              for (const auto &a : arg.args) count_val(a);
            } else if constexpr (std::is_same_v<T, tacky::ArrayLoad>) {
              // Count index and dst vars -- but NOT the array base name itself
              // (it lives on the stack as a contiguous block, not a register).
              count_val(arg.index);
              count_val(arg.dst);
            } else if constexpr (std::is_same_v<T, tacky::ArrayStore>) {
              count_val(arg.index);
              count_val(arg.src);
            }
          },
          instr);
    }
  }

  // Sort variables by use count descending (most-used gets register first).
  std::vector<std::pair<std::string, int>> sorted(use_count.begin(),
                                                   use_count.end());
  std::sort(sorted.begin(), sorted.end(),
            [](const auto &a, const auto &b) { return a.second > b.second; });

  // Greedily assign variables to R4..R15 (12 registers).
  // 8-bit variables take 1 register; 16-bit take 2 (low:high pair).
  // Stop when no more registers are available.
  std::map<std::string, std::string> result;
  int next_reg = 4;  // start at R4

  for (const auto &[name, count] : sorted) {
    if (next_reg > 15) break;

    auto type_it = var_types.find(name);
    int sz = (type_it != var_types.end()) ? size_of_type(type_it->second) : 1;

    if (next_reg + sz - 1 > 15) break;  // not enough registers left

    result[name] = std::format("R{}", next_reg);
    next_reg += sz;
  }

  return result;
}
