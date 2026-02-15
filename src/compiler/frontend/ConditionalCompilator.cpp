/*
 * -----------------------------------------------------------------------------
 * PyMCU Compiler (pymcuc)
 * Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
 *
 * This file is part of the PyMCU Development Ecosystem.
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
 *
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

ConditionalCompilator::ConditionalCompilator(std::string target_chip)
    : target_chip(std::move(target_chip)) {}

void ConditionalCompilator::process(Program& program) {
  std::vector<std::unique_ptr<Statement>> new_globals;

  for (auto& stmt : program.global_statements) {
    if (!process_statement(stmt.get(), program, new_globals)) {
      // If not handled/consumed, keep it
      new_globals.push_back(std::move(stmt));
    }
  }

  program.global_statements = std::move(new_globals);
}

bool ConditionalCompilator::process_statement(
    const Statement* stmt, Program& prog,
    std::vector<std::unique_ptr<Statement>>& new_globals) {
  if (const auto ifStmt = dynamic_cast<const IfStmt*>(stmt)) {
    if (evaluate_condition(ifStmt->condition.get())) {
      // Then Branch is ACTIVE
      if (const auto block =
              dynamic_cast<const Block*>(ifStmt->then_branch.get())) {
        for (const auto& inner : block->statements) {
          if (const auto imp = dynamic_cast<const ImportStmt*>(inner.get())) {
            // Move to imports
            // We need to clone or const-cast move. Since AST is unique_ptr, we
            // can't easily move from const. However, we are in a unique
            // context. Actually, 'inner' is unique_ptr. We can't move from
            // const reference. We need to implement a clone or reconstruct. For
            // now, let's reconstruct ImportStmt.
            prog.imports.push_back(std::make_unique<ImportStmt>(
                imp->module_name, imp->symbols, imp->relative_level));
          } else if (const auto expr =
                         dynamic_cast<const ExprStmt*>(inner.get())) {
            // Keep expressions/assignments
            // Reconstruct ExprStmt is hard without clone.
            // Let's const_cast to steal ownership (Hack, but effective for this
            // AST manipulation)
            auto& mut_inner = const_cast<std::unique_ptr<Statement>&>(inner);
            new_globals.push_back(std::move(mut_inner));
          } else {
            auto& mut_inner = const_cast<std::unique_ptr<Statement>&>(inner);
            new_globals.push_back(std::move(mut_inner));
          }
        }
      }
      return true;  // Handled
    }

    // Check elifs
    for (const auto& branch : ifStmt->elif_branches) {
      if (evaluate_condition(branch.first.get())) {
        if (const auto block =
                dynamic_cast<const Block*>(branch.second.get())) {
          for (const auto& inner : block->statements) {
            if (const auto imp = dynamic_cast<const ImportStmt*>(inner.get())) {
              prog.imports.push_back(std::make_unique<ImportStmt>(
                  imp->module_name, imp->symbols, imp->relative_level));
            } else {
              auto& mut_inner = const_cast<std::unique_ptr<Statement>&>(inner);
              new_globals.push_back(std::move(mut_inner));
            }
          }
        }
        return true;
      }
    }

    // check else
    if (ifStmt->else_branch) {
      if (const auto block =
              dynamic_cast<const Block*>(ifStmt->else_branch.get())) {
        for (const auto& inner : block->statements) {
          if (const auto imp = dynamic_cast<const ImportStmt*>(inner.get())) {
            prog.imports.push_back(std::make_unique<ImportStmt>(
                imp->module_name, imp->symbols, imp->relative_level));
          } else {
            auto& mut_inner = const_cast<std::unique_ptr<Statement>&>(inner);
            new_globals.push_back(std::move(mut_inner));
          }
        }
      }
      return true;
    }

    return true;  // Handled (condition was false, discarded)
  }

  return false;  // Not an IfStmt, keep it
}

bool ConditionalCompilator::evaluate_condition(const Expression* expr) {
  if (const auto bin = dynamic_cast<const BinaryExpr*>(expr)) {
    if (bin->op == BinaryOp::Equal) {
      std::string left_val, right_val;

      // Check left
      if (const auto var = dynamic_cast<const VariableExpr*>(bin->left.get())) {
        left_val = var->name;
      } else if (const auto str =
                     dynamic_cast<const StringLiteral*>(bin->left.get())) {
        left_val = str->value;
      }

      // Check right
      if (const auto var =
              dynamic_cast<const VariableExpr*>(bin->right.get())) {
        right_val = var->name;
      } else if (const auto str =
                     dynamic_cast<const StringLiteral*>(bin->right.get())) {
        right_val = str->value;
      }

      // Comparison
      if (left_val == "__CHIP__") {
        return right_val == target_chip;
      } else if (right_val == "__CHIP__") {
        return left_val == target_chip;
      }
    }
  }
  return false;
}
