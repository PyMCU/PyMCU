#include "DynamicStackAllocator.h"

#include <variant>

std::pair<std::map<std::string, int>, int> DynamicStackAllocator::allocate(
    const tacky::Function &func) {
  std::map<std::string, int> offsets;
  int current_offset = -reserved_top;

  auto alloc_var = [&](const std::string &name) {
    if (!offsets.contains(name)) {
      current_offset -= word_size;
      offsets[name] = current_offset;
    }
  };

  // 1. Register Parameters
  for (const auto &param : func.params) {
    alloc_var(param);
  }

  // 2. Scan body
  for (const auto &instr : func.body) {
    std::visit(
        [&](auto &&arg) {
          using T = std::decay_t<decltype(arg)>;

          auto check_val = [&](const tacky::Val &v) {
            if (auto var = std::get_if<tacky::Variable>(&v))
              alloc_var(var->name);
            if (auto tmp = std::get_if<tacky::Temporary>(&v))
              alloc_var(tmp->name);
          };

          if constexpr (std::is_same_v<T, tacky::Copy>) {
            check_val(arg.src);
            check_val(arg.dst);
          } else if constexpr (std::is_same_v<T, tacky::Binary>) {
            check_val(arg.src1);
            check_val(arg.src2);
            check_val(arg.dst);
          } else if constexpr (std::is_same_v<T, tacky::Unary>) {
            check_val(arg.src);
            check_val(arg.dst);
          } else if constexpr (std::is_same_v<T, tacky::Call>) {
            for (const auto &a : arg.args) check_val(a);
            check_val(arg.dst);
          } else if constexpr (std::is_same_v<T, tacky::Return>) {
            check_val(arg.value);
          } else if constexpr (std::is_same_v<T, tacky::JumpIfZero>) {
            check_val(arg.condition);
          } else if constexpr (std::is_same_v<T, tacky::JumpIfNotZero>) {
            check_val(arg.condition);
          } else if constexpr (std::is_same_v<T, tacky::BitSet>) {
            check_val(arg.target);
          } else if constexpr (std::is_same_v<T, tacky::BitClear>) {
            check_val(arg.target);
          } else if constexpr (std::is_same_v<T, tacky::BitCheck>) {
            check_val(arg.source);
            check_val(arg.dst);
          } else if constexpr (std::is_same_v<T, tacky::BitWrite>) {
            check_val(arg.target);
            check_val(arg.src);
          }
        },
        instr);
  }

  int total_size = -current_offset;
  // Align to 16 bytes
  if (total_size % 16 != 0) {
    total_size += 16 - (total_size % 16);
  }

  return {offsets, total_size};
}