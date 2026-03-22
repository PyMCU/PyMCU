/*
 * Whipsnake Compiler (whipc) — AVR Linear Scan Register Allocator (Temporaries)
 * Copyright (C) 2026 Ivan Montiel Cardona and the Whipsnake Project Authors
 *
 * Licensed under the GNU General Public License v3. See LICENSE for details.
 */

#include "AVRLinearScan.h"

#include <algorithm>
#include <set>
#include <variant>
#include <vector>

namespace {

struct LiveInterval {
  std::string name;
  DataType type;
  int def;       // instruction index of first definition
  int last_use;  // instruction index of last use
  bool spans_call = false;
};

}  // namespace

std::map<std::string, std::string> AVRLinearScan::allocate(
    const tacky::Function &func) {
  // ── Step 1: compute live intervals for all Temporaries ──────────────────
  std::map<std::string, LiveInterval> intervals;
  std::set<int> call_indices;

  // Helper: record a use of a Temporary at instruction index i.
  auto visit_val = [&](const tacky::Val &val, int i) {
    const auto *t = std::get_if<tacky::Temporary>(&val);
    if (!t) return;
    auto it = intervals.find(t->name);
    if (it == intervals.end()) {
      intervals[t->name] = {t->name, t->type, i, i};
    } else {
      // Update last_use; def stays at the earliest occurrence.
      it->second.last_use = i;
    }
  };

  for (int i = 0; i < static_cast<int>(func.body.size()); ++i) {
    const auto &instr = func.body[i];

    // Record Call instruction indices.
    if (std::holds_alternative<tacky::Call>(instr)) {
      call_indices.insert(i);
    }

    // Visit every Val field in the instruction.
    std::visit(
        [&](auto &&arg) {
          using T = std::decay_t<decltype(arg)>;
          if constexpr (std::is_same_v<T, tacky::Copy>) {
            visit_val(arg.src, i);
            visit_val(arg.dst, i);
          } else if constexpr (std::is_same_v<T, tacky::Binary>) {
            visit_val(arg.src1, i);
            visit_val(arg.src2, i);
            visit_val(arg.dst, i);
          } else if constexpr (std::is_same_v<T, tacky::Unary>) {
            visit_val(arg.src, i);
            visit_val(arg.dst, i);
          } else if constexpr (std::is_same_v<T, tacky::Return>) {
            visit_val(arg.value, i);
          } else if constexpr (std::is_same_v<T, tacky::JumpIfZero>) {
            visit_val(arg.condition, i);
          } else if constexpr (std::is_same_v<T, tacky::JumpIfNotZero>) {
            visit_val(arg.condition, i);
          } else if constexpr (std::is_same_v<T, tacky::JumpIfEqual>) {
            visit_val(arg.src1, i);
            visit_val(arg.src2, i);
          } else if constexpr (std::is_same_v<T, tacky::JumpIfNotEqual>) {
            visit_val(arg.src1, i);
            visit_val(arg.src2, i);
          } else if constexpr (std::is_same_v<T, tacky::JumpIfLessThan>) {
            visit_val(arg.src1, i);
            visit_val(arg.src2, i);
          } else if constexpr (std::is_same_v<T, tacky::JumpIfLessOrEqual>) {
            visit_val(arg.src1, i);
            visit_val(arg.src2, i);
          } else if constexpr (std::is_same_v<T, tacky::JumpIfGreaterThan>) {
            visit_val(arg.src1, i);
            visit_val(arg.src2, i);
          } else if constexpr (std::is_same_v<T, tacky::JumpIfGreaterOrEqual>) {
            visit_val(arg.src1, i);
            visit_val(arg.src2, i);
          } else if constexpr (std::is_same_v<T, tacky::BitCheck>) {
            visit_val(arg.source, i);
            visit_val(arg.dst, i);
          } else if constexpr (std::is_same_v<T, tacky::BitWrite>) {
            visit_val(arg.target, i);
            visit_val(arg.src, i);
          } else if constexpr (std::is_same_v<T, tacky::BitSet>) {
            visit_val(arg.target, i);
          } else if constexpr (std::is_same_v<T, tacky::BitClear>) {
            visit_val(arg.target, i);
          } else if constexpr (std::is_same_v<T, tacky::AugAssign>) {
            visit_val(arg.target, i);
            visit_val(arg.operand, i);
          } else if constexpr (std::is_same_v<T, tacky::JumpIfBitSet>) {
            visit_val(arg.source, i);
          } else if constexpr (std::is_same_v<T, tacky::JumpIfBitClear>) {
            visit_val(arg.source, i);
          } else if constexpr (std::is_same_v<T, tacky::Call>) {
            visit_val(arg.dst, i);
            for (const auto &a : arg.args) visit_val(a, i);
          } else if constexpr (std::is_same_v<T, tacky::LoadIndirect>) {
            visit_val(arg.src_ptr, i);
            visit_val(arg.dst, i);
          } else if constexpr (std::is_same_v<T, tacky::StoreIndirect>) {
            visit_val(arg.dst_ptr, i);
            visit_val(arg.src, i);
          }
        },
        instr);
  }

  // ── Step 2: mark intervals that STRICTLY span a Call ───────────────────
  // An interval spans a call if: def < call_idx < last_use (strict both sides).
  // If last_use == call_idx the temporary is consumed as an argument before
  // dispatch — it is safe to hold it in R16/R17 until the call.
  for (auto &[name, iv] : intervals) {
    for (int ci : call_indices) {
      if (iv.def < ci && ci < iv.last_use) {
        iv.spans_call = true;
        break;
      }
    }
  }

  // ── Step 3: collect eligible intervals (UINT8, no call span) ───────────
  std::vector<LiveInterval *> eligible;
  for (auto &[name, iv] : intervals) {
    if (iv.spans_call) continue;
    if (iv.type != DataType::UINT8) continue;
    eligible.push_back(&iv);
  }

  // Sort by definition point (earliest first — standard linear scan order).
  std::sort(eligible.begin(), eligible.end(),
            [](const LiveInterval *a, const LiveInterval *b) {
              return a->def < b->def;
            });

  // ── Step 4: greedy assignment to R16 (slot 0) / R17 (slot 1) ──────────
  std::map<std::string, std::string> result;
  LiveInterval *active[2] = {nullptr, nullptr};  // currently assigned intervals

  for (LiveInterval *iv : eligible) {
    // Expire active intervals whose last_use is before the current def.
    for (int k = 0; k < 2; ++k) {
      if (active[k] && active[k]->last_use < iv->def) {
        active[k] = nullptr;
      }
    }

    // Assign to first free slot.
    for (int k = 0; k < 2; ++k) {
      if (!active[k]) {
        result[iv->name] = (k == 0) ? "R16" : "R17";
        active[k] = iv;
        break;
      }
    }
    // If no slot is free, the temporary is spilled (uses stack via STD/LDD).
  }

  return result;
}
