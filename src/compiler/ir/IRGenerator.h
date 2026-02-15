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

#ifndef IRGENERATOR_H
#define IRGENERATOR_H

#pragma once
#include <map>
#include <optional>
#include <set>
#include <string>
#include <variant>
#include <vector>

#include "../frontend/Ast.h"
#include "Tacky.h"

struct SymbolInfo {
  bool is_memory_address;
  int value;
  DataType type = DataType::UINT8;
};

class IRGenerator {
 public:
  tacky::Program generate(
      const Program &main_ast,
      const std::map<std::string, const Program *> &imported_modules,
      const std::vector<std::string> &source_lines = {});

 private:
  std::vector<tacky::Instruction> current_instructions;
  int temp_counter = 0;
  int label_counter = 0;
  std::map<std::string, SymbolInfo> globals;
  std::map<std::string, DataType> mutable_globals;
  std::map<std::string, DataType> variable_types;
  std::map<std::string, std::string> function_return_types;
  std::map<std::string, std::vector<std::string> > function_params;
  std::map<std::string, const FunctionDef *>
      inline_functions;  // Map for inlining
  std::string current_function;
  std::set<std::string> current_function_globals;
  int inline_depth = 0;
  std::string current_inline_prefix;
  std::string current_module_prefix;

  struct ModuleScope {
    std::map<std::string, SymbolInfo> globals;
    std::map<std::string, DataType> mutable_globals;
    std::map<std::string, std::string> function_return_types;
    std::map<std::string, std::vector<std::string> > function_params;
    std::map<std::string, const FunctionDef *> inline_functions;
  };

  std::map<std::string, ModuleScope> modules;

  struct LoopLabels {
    std::string continue_label;
    std::string break_label;
  };

  std::vector<LoopLabels> loop_stack;

  struct InlineContext {
    std::string exit_label;
    std::optional<tacky::Temporary> result_temp;
  };

  std::vector<InlineContext> inline_stack;

  // Debugging
  std::vector<std::string> source_lines;
  int last_line = -1;

  tacky::Temporary make_temp(DataType type = DataType::UINT8);

  std::string make_label();

  void emit(const tacky::Instruction &inst);

  tacky::Val resolve_binding(const std::string &name);

  DataType resolve_type(const std::string &type_str);

  void scan_globals(const Program &ast);

  void scan_functions(const Program &ast);

  tacky::Function visitFunction(const FunctionDef *funcNode);

  void visitBlock(const Block *block);

  void visitStatement(const Statement *stmt);

  void visitReturn(const ReturnStmt *stmt);

  void visitIf(const IfStmt *stmt);

  void visitMatch(const MatchStmt *stmt);

  void visitWhile(const WhileStmt *stmt);

  void visitBreak(const BreakStmt *stmt);

  void visitContinue(const ContinueStmt *stmt);

  void visitAssign(const AssignStmt *stmt);

  void visitAugAssign(const AugAssignStmt *stmt);

  void visitVarDecl(const VarDecl *stmt);

  void visitAnnAssign(const AnnAssign *stmt);

  void visitExprStmt(const ExprStmt *stmt);

  void visitGlobal(const GlobalStmt *stmt);

  void visitDelayStmt(const DelayStmt *stmt);

  // Helper for boolean optimization
  bool emit_optimized_conditional_jump(const Expression *cond,
                                       const std::string &target_label,
                                       bool jump_if_true = false);

  tacky::Val visitExpression(const Expression *expr);

  tacky::Val visitBinary(const BinaryExpr *expr);

  tacky::Val visitUnary(const UnaryExpr *expr);

  static tacky::Val visitLiteral(const IntegerLiteral *expr);

  tacky::Val visitVariable(const VariableExpr *expr);

  tacky::Val visitCall(const CallExpr *expr);

  tacky::Val visitIndex(const IndexExpr *expr);

  tacky::Val visitMemberAccess(const MemberAccessExpr *expr);

  int evaluate_constant_expr(const Expression *expr);
};

#endif  // IRGENERATOR_H