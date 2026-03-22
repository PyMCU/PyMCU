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

#include "ConditionalCompilator.h"

#include <iostream>
#include <optional>

ConditionalCompilator::ConditionalCompilator(DeviceConfig config)
    : config(std::move(config)) {}

void ConditionalCompilator::process(Program& program) {
  std::vector<std::unique_ptr<Statement>> new_globals;

  for (auto& stmt : program.global_statements) {
    if (!process_statement(stmt.get(), program, new_globals)) {
      // If not handled/consumed, keep it
      new_globals.push_back(std::move(stmt));
    }
  }

  program.global_statements = std::move(new_globals);

  // Recursively process inside functions and classes remaining
  for (auto& stmt : program.global_statements) {
    process_block(stmt.get(), program);
  }

  // Also process top-level functions (stored separately in Program::functions)
  for (auto& func : program.functions) {
    if (func) {
      process_block(func->body.get(), program);
    }
  }
}

void ConditionalCompilator::process_block(Statement* stmt, Program& prog) {
  if (!stmt) return;

  if (auto block = dynamic_cast<Block*>(stmt)) {
    std::vector<std::unique_ptr<Statement>> new_stmts;
    for (auto& s : block->statements) {
      if (!process_statement(s.get(), prog, new_stmts)) {
        new_stmts.push_back(std::move(s));
      }
    }
    block->statements = std::move(new_stmts);

    // Recurse into remaining statements
    for (auto& s : block->statements) {
      process_block(s.get(), prog);
    }
  } else if (auto func = dynamic_cast<FunctionDef*>(stmt)) {
    process_block(func->body.get(), prog);
  } else if (auto cls = dynamic_cast<ClassDef*>(stmt)) {
    process_block(cls->body.get(), prog);
  } else if (auto ifStmt = dynamic_cast<IfStmt*>(stmt)) {
    // Recurse into if/elif/else branches to hoist imports from runtime branches
    process_block(ifStmt->then_branch.get(), prog);
    for (auto& [cond, body] : ifStmt->elif_branches) {
      process_block(body.get(), prog);
    }
    if (ifStmt->else_branch) {
      process_block(ifStmt->else_branch.get(), prog);
    }
  } else if (auto matchStmt = dynamic_cast<MatchStmt*>(stmt)) {
    for (auto& branch : matchStmt->branches) {
      process_block(branch.body.get(), prog);
    }
  } else if (auto loop = dynamic_cast<WhileStmt*>(stmt)) {
    process_block(loop->body.get(), prog);
  } else if (auto forLoop = dynamic_cast<ForStmt*>(stmt)) {
    process_block(forLoop->body.get(), prog);
  }
}

bool ConditionalCompilator::process_statement(
    const Statement* stmt, Program& prog,
    std::vector<std::unique_ptr<Statement>>& new_stmts) {
  if (const auto imp = dynamic_cast<const ImportStmt*>(stmt)) {
    prog.imports.push_back(std::make_unique<ImportStmt>(
        imp->module_name, imp->symbols, imp->relative_level));
    return true;  // Stripped from original location
  }

  if (const auto ifStmt = dynamic_cast<const IfStmt*>(stmt)) {
    // If we can evaluate it, do so. Otherwise leave it alone (it might be a
    // runtime IF)
    std::optional<bool> condition_result;

    try {
      if (evaluate_condition(ifStmt->condition.get())) {
        condition_result = true;
      } else {
        // Check elifs
        bool elif_taken = false;
        for (const auto& branch : ifStmt->elif_branches) {
          if (evaluate_condition(branch.first.get())) {
            // Take this branch
            if (const auto block =
                    dynamic_cast<const Block*>(branch.second.get())) {
              for (const auto& inner : block->statements) {
                if (const auto imp =
                        dynamic_cast<const ImportStmt*>(inner.get())) {
                  prog.imports.push_back(std::make_unique<ImportStmt>(
                      imp->module_name, imp->symbols, imp->relative_level));
                } else {
                  auto& mut_inner =
                      const_cast<std::unique_ptr<Statement>&>(inner);
                  new_stmts.push_back(std::move(mut_inner));
                }
              }
            }
            elif_taken = true;
            break;
          }
        }

        if (!elif_taken) {
          if (ifStmt->else_branch) {
            if (const auto block =
                    dynamic_cast<const Block*>(ifStmt->else_branch.get())) {
              for (const auto& inner : block->statements) {
                if (const auto imp =
                        dynamic_cast<const ImportStmt*>(inner.get())) {
                  prog.imports.push_back(std::make_unique<ImportStmt>(
                      imp->module_name, imp->symbols, imp->relative_level));
                } else {
                  auto& mut_inner =
                      const_cast<std::unique_ptr<Statement>&>(inner);
                  new_stmts.push_back(std::move(mut_inner));
                }
              }
            }
          }
        }
        return true;  // Handled
      }
    } catch (...) {
      // If evaluation fails (e.g. not a compile-time condition), we don't
      // handle it here. Return false to keep the IfStmt as a runtime IF.
      return false;
    }

    if (condition_result.has_value() && *condition_result) {
      // Then Branch is ACTIVE
      if (const auto block =
              dynamic_cast<const Block*>(ifStmt->then_branch.get())) {
        for (const auto& inner : block->statements) {
          if (const auto imp = dynamic_cast<const ImportStmt*>(inner.get())) {
            prog.imports.push_back(std::make_unique<ImportStmt>(
                imp->module_name, imp->symbols, imp->relative_level));
          } else {
            auto& mut_inner = const_cast<std::unique_ptr<Statement>&>(inner);
            new_stmts.push_back(std::move(mut_inner));
          }
        }
      }
      return true;  // Handled
    }
  }

  if (const auto matchStmt = dynamic_cast<const MatchStmt*>(stmt)) {
    // Try to evaluate the match target at compile time
    try {
      std::string target_val = evaluate_match_target(matchStmt->target.get());

      // Find the matching case branch
      for (const auto& branch : matchStmt->branches) {
        if (!branch.pattern) {
          // Wildcard (_) — always matches as fallback
          if (const auto block =
                  dynamic_cast<const Block*>(branch.body.get())) {
            for (const auto& inner : block->statements) {
              if (const auto imp =
                      dynamic_cast<const ImportStmt*>(inner.get())) {
                prog.imports.push_back(std::make_unique<ImportStmt>(
                    imp->module_name, imp->symbols, imp->relative_level));
              } else {
                auto& mut_inner =
                    const_cast<std::unique_ptr<Statement>&>(inner);
                new_stmts.push_back(std::move(mut_inner));
              }
            }
          }
          return true;
        }

        // Check if this case pattern matches the target
        if (const auto str =
                dynamic_cast<const StringLiteral*>(branch.pattern.get())) {
          if (str->value == target_val) {
            // This case matches — emit its body
            if (const auto block =
                    dynamic_cast<const Block*>(branch.body.get())) {
              for (const auto& inner : block->statements) {
                if (const auto imp =
                        dynamic_cast<const ImportStmt*>(inner.get())) {
                  prog.imports.push_back(std::make_unique<ImportStmt>(
                      imp->module_name, imp->symbols, imp->relative_level));
                } else {
                  auto& mut_inner =
                      const_cast<std::unique_ptr<Statement>&>(inner);
                  new_stmts.push_back(std::move(mut_inner));
                }
              }
            }
            return true;
          }
        }
      }
      // No case matched — eliminate the entire match statement
      return true;
    } catch (...) {
      // Not a compile-time match target — keep as runtime match
      return false;
    }
  }

  return false;
}

bool ConditionalCompilator::evaluate_condition(const Expression* expr) {
  if (!expr) return false;

  if (const auto bin = dynamic_cast<const BinaryExpr*>(expr)) {
    if (bin->op == BinaryOp::Or) {
      return evaluate_condition(bin->left.get()) ||
             evaluate_condition(bin->right.get());
    }
    if (bin->op == BinaryOp::And) {
      return evaluate_condition(bin->left.get()) &&
             evaluate_condition(bin->right.get());
    }
    if (bin->op == BinaryOp::Equal || bin->op == BinaryOp::NotEqual) {
      // Evaluate sides
      auto get_val = [&](const Expression* e) -> std::string {
        if (const auto var = dynamic_cast<const VariableExpr*>(e)) {
          if (var->name == "__CHIP__") return config.chip;
          throw std::runtime_error("Unknown var");
        }
        if (const auto mem = dynamic_cast<const MemberAccessExpr*>(e)) {
          if (const auto obj =
                  dynamic_cast<const VariableExpr*>(mem->object.get())) {
            if (obj->name == "__CHIP__") {
              if (mem->member == "arch") return config.arch;
              if (mem->member == "chip" || mem->member == "name")
                return config.chip;
            }
          }
          throw std::runtime_error("Unknown member");
        }
        if (const auto str = dynamic_cast<const StringLiteral*>(e)) {
          return str->value;
        }
        throw std::runtime_error("Not a constant");
      };

      try {
        std::string left = get_val(bin->left.get());
        std::string right = get_val(bin->right.get());

        if (bin->op == BinaryOp::Equal) return left == right;
        return left != right;
      } catch (...) {
        throw;  // Propagate to skip this IF
      }
    }
  }

  // Handle startswith: expr.startswith("literal")
  if (const auto call = dynamic_cast<const CallExpr*>(expr)) {
    if (const auto mem =
            dynamic_cast<const MemberAccessExpr*>(call->callee.get())) {
      if (mem->member == "startswith") {
        if (call->args.size() == 1) {
          if (const auto arg =
                  dynamic_cast<const StringLiteral*>(call->args[0].get())) {
            // Evaluate object
            auto object_val = [&](const Expression* e) -> std::string {
              if (const auto m = dynamic_cast<const MemberAccessExpr*>(e)) {
                if (const auto obj =
                        dynamic_cast<const VariableExpr*>(m->object.get())) {
                  if (obj->name == "__CHIP__") {
                    if (m->member == "arch") return config.arch;
                    if (m->member == "chip" || m->member == "name")
                      return config.chip;
                  }
                }
              }
              throw std::runtime_error("Unsupported startswith object");
            }(mem->object.get());

            return object_val.find(arg->value) == 0;
          }
        }
      }
    }
  }

  throw std::runtime_error("Unsupported condition");
}

std::string ConditionalCompilator::evaluate_match_target(
    const Expression* expr) {
  if (const auto var = dynamic_cast<const VariableExpr*>(expr)) {
    if (var->name == "__CHIP__") return config.chip;
  }
  if (const auto mem = dynamic_cast<const MemberAccessExpr*>(expr)) {
    if (const auto obj =
            dynamic_cast<const VariableExpr*>(mem->object.get())) {
      if (obj->name == "__CHIP__") {
        if (mem->member == "arch") return config.arch;
        if (mem->member == "chip" || mem->member == "name")
          return config.chip;
      }
    }
  }
  throw std::runtime_error("Unsupported match target");
}
