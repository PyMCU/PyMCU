/*
 * -----------------------------------------------------------------------------
 * Whisnake Compiler (whipc)
 * Copyright (C) 2026 Ivan Montiel Cardona and the Whisnake Project Authors
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

#include "Optimizer.h"

#include <algorithm>
#include <map>
#include <set>
#include <variant>
#include <queue>
#include <optional>

tacky::Program Optimizer::optimize(const tacky::Program &program) {
  tacky::Program optimized = program;
  for (auto &func : optimized.functions) {
    optimize_function(func);
  }

  // Dead Function Elimination (DFE): remove functions that are never reachable
  // from main or any ISR. Unreachable functions cause AVR codegen to emit LDS
  // with dotted symbol names (e.g. LDS R18, clear_bit.col) which avra rejects.
  {
    // Build call graph: function name -> set of called function names.
    std::map<std::string, std::set<std::string>> call_graph;
    for (const auto &func : optimized.functions) {
      auto &callees = call_graph[func.name];
      for (const auto &instr : func.body) {
        std::visit([&](auto &&arg) {
          using T = std::decay_t<decltype(arg)>;
          if constexpr (std::is_same_v<T, tacky::Call>) {
            callees.insert(arg.function_name);
          }
        }, instr);
      }
    }

    // BFS from main + all ISR functions to collect reachable function names.
    std::set<std::string> reachable;
    std::queue<std::string> worklist;
    auto enqueue = [&](const std::string &name) {
      if (reachable.insert(name).second)  // inserted (not already seen)
        worklist.push(name);
    };
    enqueue("main");
    for (const auto &func : optimized.functions) {
      if (func.is_interrupt) enqueue(func.name);
    }
    while (!worklist.empty()) {
      std::string cur = worklist.front();
      worklist.pop();
      if (call_graph.contains(cur)) {
        for (const auto &callee : call_graph.at(cur)) {
          enqueue(callee);
        }
      }
    }

    // Remove functions not in the reachable set.
    optimized.functions.erase(
        std::remove_if(optimized.functions.begin(), optimized.functions.end(),
                       [&](const tacky::Function &f) {
                         return !reachable.contains(f.name);
                       }),
        optimized.functions.end());
  }

  return optimized;
}

void Optimizer::optimize_function(tacky::Function &func) {
  for (int i = 0; i < 10; ++i) {
    // Fixed number of passes for simplicity
    propagate_copies(func);
    fold_constants(func);
    coalesce_instructions(func);
    eliminate_dead_code(func);
  }
  // After the main loop: first bridge Binary+JumpIfZero → relational jump,
  // then collapse BitCheck+relational jump → JumpIfBitSet/Clear.
  // DCE must run between the two collapses: collapse_bool_jumps marks the
  // replaced Binary as a dead Copy, and if that dead instruction is left in
  // place it will block collapse_bit_checks from seeing BitCheck+JumpIfNotEqual
  // as adjacent.
  collapse_bool_jumps(func);  // Binary(op,a,b)+JumpIfZero → JumpIfEqual/etc
  eliminate_dead_code(func);  // remove dead Binary markers
  collapse_bit_checks(func);  // BitCheck+JumpIfEqual → JumpIfBitSet/Clear
  eliminate_dead_code(func);  // clean up dead BitCheck instructions
}

static std::optional<int> get_constant(const tacky::Val &val) {
  if (auto c = std::get_if<tacky::Constant>(&val)) {
    return c->value;
  }
  return std::nullopt;
}

void Optimizer::fold_constants(tacky::Function &func) {
  for (auto &instr : func.body) {
    if (auto *binary = std::get_if<tacky::Binary>(&instr)) {
      auto c1 = get_constant(binary->src1);
      auto c2 = get_constant(binary->src2);
      if (c1 && c2) {
        int result = 0;
        bool foldable = true;
        switch (binary->op) {
          case tacky::BinaryOp::Add:
            result = *c1 + *c2;
            break;
          case tacky::BinaryOp::Sub:
            result = *c1 - *c2;
            break;
          case tacky::BinaryOp::Mul:
            result = *c1 * *c2;
            break;
          case tacky::BinaryOp::Div:
            if (*c2 != 0)
              result = *c1 / *c2;
            else
              foldable = false;
            break;
          case tacky::BinaryOp::FloorDiv:
            if (*c2 != 0) {
              int q = *c1 / *c2;
              // Floor toward negative infinity (Python semantics)
              if ((*c1 ^ *c2) < 0 && q * *c2 != *c1) q--;
              result = q;
            } else {
              foldable = false;
            }
            break;
          case tacky::BinaryOp::Mod:
            if (*c2 != 0)
              result = *c1 % *c2;
            else
              foldable = false;
            break;
          case tacky::BinaryOp::Equal:
            result = (*c1 == *c2);
            break;
          case tacky::BinaryOp::NotEqual:
            result = (*c1 != *c2);
            break;
          case tacky::BinaryOp::LessThan:
            result = (*c1 < *c2);
            break;
          case tacky::BinaryOp::LessEqual:
            result = (*c1 <= *c2);
            break;
          case tacky::BinaryOp::GreaterThan:
            result = (*c1 > *c2);
            break;
          case tacky::BinaryOp::GreaterEqual:
            result = (*c1 >= *c2);
            break;
          case tacky::BinaryOp::BitAnd:
            result = *c1 & *c2;
            break;
          case tacky::BinaryOp::BitOr:
            result = *c1 | *c2;
            break;
          case tacky::BinaryOp::BitXor:
            result = *c1 ^ *c2;
            break;
          case tacky::BinaryOp::LShift:
            result = *c1 << *c2;
            break;
          case tacky::BinaryOp::RShift:
            result = *c1 >> *c2;
            break;
          default:
            foldable = false;
            break;
        }
        if (foldable) {
          instr = tacky::Copy{tacky::Constant{result}, binary->dst};
        }
      }
    } else if (auto *unary = std::get_if<tacky::Unary>(&instr)) {
      auto c = get_constant(unary->src);
      if (c) {
        int result = 0;
        bool foldable = true;
        switch (unary->op) {
          case tacky::UnaryOp::Neg:
            result = -(*c);
            break;
          case tacky::UnaryOp::Not:
            result = !(*c);
            break;
          case tacky::UnaryOp::BitNot:
            result = ~(*c);
            break;
          default:
            foldable = false;
            break;
        }
        if (foldable) {
          instr = tacky::Copy{tacky::Constant{result}, unary->dst};
        }
      }
    }
  }
}

void Optimizer::eliminate_dead_code(tacky::Function &func) {
  // Simple DCE: remove Copy/Binary/Unary/Call if dst is a Temporary and never
  // used. Note: Call might have side effects, so we should be careful. But for
  // now let's focus on Temporaries which are strictly internal.

  std::set<std::string> used_temps;
  auto register_use = [&](const tacky::Val &val) {
    if (auto t = std::get_if<tacky::Temporary>(&val)) {
      used_temps.insert(t->name);
    }
  };

  for (const auto &instr : func.body) {
    std::visit(
        [&](auto &&arg) {
          using T = std::decay_t<decltype(arg)>;
          if constexpr (std::is_same_v<T, tacky::Return>)
            register_use(arg.value);
          else if constexpr (std::is_same_v<T, tacky::Unary>)
            register_use(arg.src);
          else if constexpr (std::is_same_v<T, tacky::Binary>) {
            register_use(arg.src1);
            register_use(arg.src2);
          } else if constexpr (std::is_same_v<T, tacky::Copy>)
            register_use(arg.src);
          else if constexpr (std::is_same_v<T, tacky::JumpIfZero>)
            register_use(arg.condition);
          else if constexpr (std::is_same_v<T, tacky::JumpIfNotZero>)
            register_use(arg.condition);
          else if constexpr (std::is_same_v<T, tacky::Call>) {
            for (const auto &arg_val : arg.args) register_use(arg_val);
          } else if constexpr (std::is_same_v<T, tacky::BitCheck>)
            register_use(arg.source);
          else if constexpr (std::is_same_v<T, tacky::BitWrite>) {
            register_use(arg.target);
            register_use(arg.src);
          } else if constexpr (std::is_same_v<T, tacky::BitSet>)
            register_use(arg.target);
          else if constexpr (std::is_same_v<T, tacky::BitClear>)
            register_use(arg.target);          else if constexpr (std::is_same_v<T, tacky::JumpIfEqual>) {
            register_use(arg.src1);
            register_use(arg.src2);
          } else if constexpr (std::is_same_v<T, tacky::JumpIfNotEqual>) {
            register_use(arg.src1);
            register_use(arg.src2);
          } else if constexpr (std::is_same_v<T, tacky::JumpIfLessThan>) {
            register_use(arg.src1);
            register_use(arg.src2);
          } else if constexpr (std::is_same_v<T, tacky::JumpIfLessOrEqual>) {
            register_use(arg.src1);
            register_use(arg.src2);
          } else if constexpr (std::is_same_v<T, tacky::JumpIfGreaterThan>) {
            register_use(arg.src1);
            register_use(arg.src2);
          } else if constexpr (std::is_same_v<T, tacky::JumpIfGreaterOrEqual>) {
            register_use(arg.src1);
            register_use(arg.src2);
          } else if constexpr (std::is_same_v<T, tacky::JumpIfBitSet>)
            register_use(arg.source);
          else if constexpr (std::is_same_v<T, tacky::JumpIfBitClear>)
            register_use(arg.source);
          else if constexpr (std::is_same_v<T, tacky::AugAssign>) {
            register_use(arg.target);
            register_use(arg.operand);
          }
        },
        instr);
  }

  auto is_dead = [&](const tacky::Instruction &instr) {
    return std::visit(
        [&](auto &&arg) -> bool {
          using T = std::decay_t<decltype(arg)>;
          const tacky::Val *dst_ptr = nullptr;
          if constexpr (requires { arg.dst; }) {
            if constexpr (!std::is_same_v<T, tacky::Call>) {
              dst_ptr = &arg.dst;
            }
          }

          if (dst_ptr) {
            if (auto t = std::get_if<tacky::Temporary>(dst_ptr)) {
              return used_temps.find(t->name) == used_temps.end();
            }
          }
          return false;
        },
        instr);
  };

  func.body.erase(std::remove_if(func.body.begin(), func.body.end(), is_dead),
                  func.body.end());
}

void Optimizer::propagate_copies(tacky::Function &func) {
  std::map<std::string, tacky::Val> temp_copies;
  // Temporaries that have been written on multiple paths — permanently excluded
  // from propagation so that a later lone definition doesn't incorrectly win.
  std::set<std::string> blacklisted_temps;
  // Tracks variables whose value is a known compile-time constant
  // (from Copy{Constant{N}, Variable{v}}).  Used to propagate constants into
  // Binary/Unary operands so that fold_constants can then eliminate them.
  std::map<std::string, int> var_consts;

  // Helper: invalidate a variable in var_consts when it is written.
  auto invalidate_var = [&](const tacky::Val &dst) {
    if (auto *v = std::get_if<tacky::Variable>(&dst))
      var_consts.erase(v->name);
    else if (auto *t = std::get_if<tacky::Temporary>(&dst))
      temp_copies.erase(t->name);
  };

  for (auto &instr : func.body) {
    // 1. Substitute uses of temporaries and known-constant variables
    std::visit(
        [&](auto &&arg) {
          using T = std::decay_t<decltype(arg)>;
          auto replace_val = [&](tacky::Val &v) {
            if (auto t = std::get_if<tacky::Temporary>(&v)) {
              if (temp_copies.contains(t->name)) {
                v = temp_copies[t->name];
              }
            } else if (auto var = std::get_if<tacky::Variable>(&v)) {
              // Propagate known constant value into operand so fold_constants
              // can collapse the instruction.
              if (var_consts.contains(var->name)) {
                v = tacky::Constant{var_consts.at(var->name)};
              }
            }
          };

          if constexpr (std::is_same_v<T, tacky::Return>)
            replace_val(arg.value);
          else if constexpr (std::is_same_v<T, tacky::Unary>)
            replace_val(arg.src);
          else if constexpr (std::is_same_v<T, tacky::Binary>) {
            replace_val(arg.src1);
            replace_val(arg.src2);
          } else if constexpr (std::is_same_v<T, tacky::Copy>)
            replace_val(arg.src);
          else if constexpr (std::is_same_v<T, tacky::JumpIfZero>)
            replace_val(arg.condition);
          else if constexpr (std::is_same_v<T, tacky::JumpIfNotZero>)
            replace_val(arg.condition);
          else if constexpr (std::is_same_v<T, tacky::Call>) {
            for (auto &v : arg.args) replace_val(v);
          } else if constexpr (std::is_same_v<T, tacky::BitCheck>)
            replace_val(arg.source);
          else if constexpr (std::is_same_v<T, tacky::BitWrite>) {
            replace_val(arg.src);
            replace_val(arg.target);
          } else if constexpr (std::is_same_v<T, tacky::BitSet>)
            replace_val(arg.target);
          else if constexpr (std::is_same_v<T, tacky::BitClear>)
            replace_val(arg.target);          else if constexpr (std::is_same_v<T, tacky::JumpIfEqual>) {
            replace_val(arg.src1);
            replace_val(arg.src2);
          } else if constexpr (std::is_same_v<T, tacky::JumpIfNotEqual>) {
            replace_val(arg.src1);
            replace_val(arg.src2);
          } else if constexpr (std::is_same_v<T, tacky::JumpIfLessThan>) {
            replace_val(arg.src1);
            replace_val(arg.src2);
          } else if constexpr (std::is_same_v<T, tacky::JumpIfLessOrEqual>) {
            replace_val(arg.src1);
            replace_val(arg.src2);
          } else if constexpr (std::is_same_v<T, tacky::JumpIfGreaterThan>) {
            replace_val(arg.src1);
            replace_val(arg.src2);
          } else if constexpr (std::is_same_v<T, tacky::JumpIfGreaterOrEqual>) {
            replace_val(arg.src1);
            replace_val(arg.src2);
          } else if constexpr (std::is_same_v<T, tacky::JumpIfBitSet>)
            replace_val(arg.source);
          else if constexpr (std::is_same_v<T, tacky::JumpIfBitClear>)
            replace_val(arg.source);
          else if constexpr (std::is_same_v<T, tacky::AugAssign>) {
            // Do NOT replace arg.target: AugAssign both reads from and writes
            // to the target.  Replacing it with a constant makes the load
            // return the wrong value and the store a no-op.
            replace_val(arg.operand);
          }
        },
        instr);

    // 2. Track new copies: Temporary → any Val, Variable → Constant only
    if (auto *copy = std::get_if<tacky::Copy>(&instr)) {
      if (auto t_dst = std::get_if<tacky::Temporary>(&copy->dst)) {
        // If this Temporary has already been assigned on a different code path
        // (multi-definition), permanently blacklist it — never propagate so that
        // a later lone definition doesn't incorrectly dominate earlier paths.
        if (blacklisted_temps.contains(t_dst->name)) {
          // Already blacklisted — do not add back, even for a "new" definition.
        } else if (temp_copies.contains(t_dst->name)) {
          temp_copies.erase(t_dst->name);
          blacklisted_temps.insert(t_dst->name);
        } else {
          temp_copies[t_dst->name] = copy->src;
        }
      } else if (auto v_dst = std::get_if<tacky::Variable>(&copy->dst)) {
        if (auto c = std::get_if<tacky::Constant>(&copy->src)) {
          var_consts[v_dst->name] = c->value;  // track constant assignment
        } else {
          var_consts.erase(v_dst->name);  // non-constant write: invalidate
        }
      }
    } else if (auto *aug = std::get_if<tacky::AugAssign>(&instr)) {
      invalidate_var(aug->target);
    } else if (auto *binary = std::get_if<tacky::Binary>(&instr)) {
      invalidate_var(binary->dst);
    } else if (auto *unary = std::get_if<tacky::Unary>(&instr)) {
      invalidate_var(unary->dst);
    } else if (std::get_if<tacky::Label>(&instr)) {
      // Label = control-flow merge point: variable values may differ across
      // incoming edges, so constants can no longer be assumed.
      var_consts.clear();
    }
  }
}

void Optimizer::coalesce_instructions(tacky::Function &func) {
  std::map<std::string, int> use_count;
  auto register_use = [&](const tacky::Val &v) {
    if (auto t = std::get_if<tacky::Temporary>(&v)) {
      use_count[t->name]++;
    }
  };

  for (const auto &instr : func.body) {
    std::visit(
        [&](auto &&arg) {
          using T = std::decay_t<decltype(arg)>;
          if constexpr (std::is_same_v<T, tacky::Return>)
            register_use(arg.value);
          else if constexpr (std::is_same_v<T, tacky::Unary>)
            register_use(arg.src);
          else if constexpr (std::is_same_v<T, tacky::Binary>) {
            register_use(arg.src1);
            register_use(arg.src2);
          } else if constexpr (std::is_same_v<T, tacky::Copy>)
            register_use(arg.src);
          else if constexpr (std::is_same_v<T, tacky::JumpIfZero>)
            register_use(arg.condition);
          else if constexpr (std::is_same_v<T, tacky::JumpIfNotZero>)
            register_use(arg.condition);
          else if constexpr (std::is_same_v<T, tacky::Call>) {
            for (const auto &v : arg.args) register_use(v);
          } else if constexpr (std::is_same_v<T, tacky::BitCheck>)
            register_use(arg.source);
          else if constexpr (std::is_same_v<T, tacky::BitWrite>) {
            register_use(arg.src);
            register_use(arg.target);
          } else if constexpr (std::is_same_v<T, tacky::BitSet>)
            register_use(arg.target);
          else if constexpr (std::is_same_v<T, tacky::BitClear>)
            register_use(arg.target);          else if constexpr (std::is_same_v<T, tacky::JumpIfEqual>) {
            register_use(arg.src1);
            register_use(arg.src2);
          } else if constexpr (std::is_same_v<T, tacky::JumpIfNotEqual>) {
            register_use(arg.src1);
            register_use(arg.src2);
          } else if constexpr (std::is_same_v<T, tacky::JumpIfLessThan>) {
            register_use(arg.src1);
            register_use(arg.src2);
          } else if constexpr (std::is_same_v<T, tacky::JumpIfLessOrEqual>) {
            register_use(arg.src1);
            register_use(arg.src2);
          } else if constexpr (std::is_same_v<T, tacky::JumpIfGreaterThan>) {
            register_use(arg.src1);
            register_use(arg.src2);
          } else if constexpr (std::is_same_v<T, tacky::JumpIfGreaterOrEqual>) {
            register_use(arg.src1);
            register_use(arg.src2);
          } else if constexpr (std::is_same_v<T, tacky::JumpIfBitSet>)
            register_use(arg.source);
          else if constexpr (std::is_same_v<T, tacky::JumpIfBitClear>)
            register_use(arg.source);
          else if constexpr (std::is_same_v<T, tacky::AugAssign>) {
            register_use(arg.target);
            register_use(arg.operand);
          }
        },
        instr);
  }

  std::vector<tacky::Instruction> new_body;
  for (size_t i = 0; i < func.body.size(); ++i) {
    if (i + 1 < func.body.size()) {
      auto *next_copy = std::get_if<tacky::Copy>(&func.body[i + 1]);
      if (next_copy) {
        if (auto t_src = std::get_if<tacky::Temporary>(&next_copy->src)) {
          if (use_count[t_src->name] == 1) {
            tacky::Instruction current = func.body[i];
            bool coalesced = std::visit(
                [&](auto &&arg) -> bool {
                  using T = std::decay_t<decltype(arg)>;
                  if constexpr (requires { arg.dst; }) {
                    if (auto t_dst = std::get_if<tacky::Temporary>(&arg.dst)) {
                      if (t_dst->name == t_src->name) {
                        arg.dst = next_copy->dst;
                        return true;
                      }
                    }
                  }
                  return false;
                },
                current);

            if (coalesced) {
              new_body.push_back(current);
              i++;  // skip the copy
              continue;
            }
          }
        }
      }
    }
    new_body.push_back(func.body[i]);
  }
  func.body = new_body;
}

void Optimizer::collapse_bit_checks(tacky::Function &func) {
  // Collapse patterns of the form:
  //   BitCheck(src, bit) → tmp_N
  //   JumpIfEqual(tmp_N, 1, label)    → JumpIfBitSet(src, bit, label)
  //   JumpIfEqual(tmp_N, 0, label)    → JumpIfBitClear(src, bit, label)
  //   JumpIfNotEqual(tmp_N, 0, label) → JumpIfBitSet(src, bit, label)
  //   JumpIfNotEqual(tmp_N, 1, label) → JumpIfBitClear(src, bit, label)
  //
  // The collapsed JumpIfBitSet/Clear generates 2-3 instructions at the AVR
  // codegen level (SBIS/SBIC for IO range; LDS+ANDI+BRNE for extended range)
  // instead of the 10+ instruction boolean materialization sequence.
  for (size_t i = 0; i + 1 < func.body.size(); ++i) {
    auto *bc = std::get_if<tacky::BitCheck>(&func.body[i]);
    if (!bc) continue;
    auto *dst_tmp = std::get_if<tacky::Temporary>(&bc->dst);
    if (!dst_tmp) continue;

    // Find the next non-label instruction
    size_t j = i + 1;
    while (j < func.body.size() &&
           std::holds_alternative<tacky::Label>(func.body[j])) {
      j++;
    }
    if (j >= func.body.size()) continue;

    bool replaced = false;

    // JumpIfEqual(tmp_N, 1, label) → JumpIfBitSet
    // JumpIfEqual(tmp_N, 0, label) → JumpIfBitClear
    if (auto *je = std::get_if<tacky::JumpIfEqual>(&func.body[j])) {
      auto *s = std::get_if<tacky::Temporary>(&je->src1);
      if (!s) s = nullptr;
      // Also check if tmp is in src2 position
      auto *s2 = std::get_if<tacky::Temporary>(&je->src2);
      const tacky::Constant *c = std::get_if<tacky::Constant>(&je->src2);
      if (!c) c = std::get_if<tacky::Constant>(&je->src1);

      if (s && s->name == dst_tmp->name && c) {
        if (c->value == 1)
          func.body[j] = tacky::JumpIfBitSet{bc->source, bc->bit, je->target};
        else if (c->value == 0)
          func.body[j] = tacky::JumpIfBitClear{bc->source, bc->bit, je->target};
        replaced = true;
      } else if (s2 && s2->name == dst_tmp->name && c) {
        // tmp is in src2 — swap: JumpIfEqual(1, tmp_N, label) is unusual but handle it
        if (c->value == 1)
          func.body[j] = tacky::JumpIfBitSet{bc->source, bc->bit, je->target};
        else if (c->value == 0)
          func.body[j] = tacky::JumpIfBitClear{bc->source, bc->bit, je->target};
        replaced = true;
      }
    }
    // JumpIfNotEqual(tmp_N, 0, label) → JumpIfBitSet
    // JumpIfNotEqual(tmp_N, 1, label) → JumpIfBitClear
    else if (auto *jne = std::get_if<tacky::JumpIfNotEqual>(&func.body[j])) {
      auto *s = std::get_if<tacky::Temporary>(&jne->src1);
      const tacky::Constant *c = std::get_if<tacky::Constant>(&jne->src2);
      if (!c) c = std::get_if<tacky::Constant>(&jne->src1);

      if (s && s->name == dst_tmp->name && c) {
        if (c->value == 0)
          func.body[j] = tacky::JumpIfBitSet{bc->source, bc->bit, jne->target};
        else if (c->value == 1)
          func.body[j] = tacky::JumpIfBitClear{bc->source, bc->bit, jne->target};
        replaced = true;
      }
    }

    if (replaced) {
      // Mark the BitCheck as a no-op by replacing it with a dead Copy to itself.
      // DCE will remove it because the dst tmp_N is no longer used.
      func.body[i] = tacky::Copy{tacky::Constant{0}, bc->dst};
    }
  }
}

void Optimizer::collapse_bool_jumps(tacky::Function &func) {
  // Bridge pass: fold patterns like
  //   Binary(Equal,    a, b) → tmp;  JumpIfZero(tmp, L)    → JumpIfNotEqual(a, b, L)
  //   Binary(Equal,    a, b) → tmp;  JumpIfNotZero(tmp, L) → JumpIfEqual(a, b, L)
  //   Binary(NotEqual, a, b) → tmp;  JumpIfZero(tmp, L)    → JumpIfEqual(a, b, L)
  //   Binary(NotEqual, a, b) → tmp;  JumpIfNotZero(tmp, L) → JumpIfNotEqual(a, b, L)
  //   (and symmetric cases for LessThan/LessEqual/GreaterThan/GreaterEqual)
  //
  // This must run before collapse_bit_checks so that:
  //   BitCheck(src,bit)→tmp_0; Binary(Equal,tmp_0,0)→tmp_1; JumpIfZero(tmp_1,L)
  // becomes:
  //   BitCheck(src,bit)→tmp_0; JumpIfNotEqual(tmp_0, 0, L)
  // which collapse_bit_checks then folds to JumpIfBitSet(src, bit, L).
  for (size_t i = 0; i + 1 < func.body.size(); ++i) {
    auto *bin = std::get_if<tacky::Binary>(&func.body[i]);
    if (!bin) continue;
    auto *dst_tmp = std::get_if<tacky::Temporary>(&bin->dst);
    if (!dst_tmp) continue;

    // Find next non-label instruction
    size_t j = i + 1;
    while (j < func.body.size() &&
           std::holds_alternative<tacky::Label>(func.body[j])) {
      j++;
    }
    if (j >= func.body.size()) continue;

    std::string target;
    bool is_zero_check = false;

    if (auto *jiz = std::get_if<tacky::JumpIfZero>(&func.body[j])) {
      auto *t = std::get_if<tacky::Temporary>(&jiz->condition);
      if (!t || t->name != dst_tmp->name) continue;
      target = jiz->target;
      is_zero_check = true;
    } else if (auto *jinz = std::get_if<tacky::JumpIfNotZero>(&func.body[j])) {
      auto *t = std::get_if<tacky::Temporary>(&jinz->condition);
      if (!t || t->name != dst_tmp->name) continue;
      target = jinz->target;
      is_zero_check = false;
    } else {
      continue;
    }

    bool replaced = false;
    switch (bin->op) {
      case tacky::BinaryOp::Equal:
        if (is_zero_check)
          func.body[j] = tacky::JumpIfNotEqual{bin->src1, bin->src2, target};
        else
          func.body[j] = tacky::JumpIfEqual{bin->src1, bin->src2, target};
        replaced = true;
        break;
      case tacky::BinaryOp::NotEqual:
        if (is_zero_check)
          func.body[j] = tacky::JumpIfEqual{bin->src1, bin->src2, target};
        else
          func.body[j] = tacky::JumpIfNotEqual{bin->src1, bin->src2, target};
        replaced = true;
        break;
      case tacky::BinaryOp::LessThan:
        if (is_zero_check)
          func.body[j] = tacky::JumpIfGreaterOrEqual{bin->src1, bin->src2, target};
        else
          func.body[j] = tacky::JumpIfLessThan{bin->src1, bin->src2, target};
        replaced = true;
        break;
      case tacky::BinaryOp::LessEqual:
        if (is_zero_check)
          func.body[j] = tacky::JumpIfGreaterThan{bin->src1, bin->src2, target};
        else
          func.body[j] = tacky::JumpIfLessOrEqual{bin->src1, bin->src2, target};
        replaced = true;
        break;
      case tacky::BinaryOp::GreaterThan:
        if (is_zero_check)
          func.body[j] = tacky::JumpIfLessOrEqual{bin->src1, bin->src2, target};
        else
          func.body[j] = tacky::JumpIfGreaterThan{bin->src1, bin->src2, target};
        replaced = true;
        break;
      case tacky::BinaryOp::GreaterEqual:
        if (is_zero_check)
          func.body[j] = tacky::JumpIfLessThan{bin->src1, bin->src2, target};
        else
          func.body[j] = tacky::JumpIfGreaterOrEqual{bin->src1, bin->src2, target};
        replaced = true;
        break;
      default:
        break;
    }

    if (replaced) {
      // Mark the Binary as dead; DCE will remove it since dst tmp is unused.
      func.body[i] = tacky::Copy{tacky::Constant{0}, bin->dst};
    }
  }
}
