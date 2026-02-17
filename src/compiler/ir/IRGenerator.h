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

#include "../common/DeviceConfig.h"
#include "../common/Errors.h"
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
      const DeviceConfig &config,
      const std::vector<std::string> &source_lines = {},
      const std::map<std::string, std::vector<std::string>>
          &module_source_lines = {});

 private:
  std::vector<tacky::Instruction> current_instructions;
  int temp_counter = 0;
  int label_counter = 0;
  std::map<std::string, SymbolInfo> globals;
  std::map<std::string, DataType> mutable_globals;
  std::map<std::string, DataType> variable_types;
  std::map<std::string, std::string> instance_classes;  // Tracks led -> Pin
  std::map<std::string, std::string> method_instance_types;  // method -> class
  std::map<std::string, std::string> function_return_types;
  std::map<std::string, std::vector<std::string>> function_params;
  std::map<std::string, std::vector<DataType>> function_param_types;
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
    std::map<std::string, std::vector<std::string>> function_params;
    std::map<std::string, const FunctionDef *> inline_functions;
  };

  std::map<std::string, ModuleScope> modules;
  std::set<std::string> class_names;  // Tracks known class names for callee resolution
  std::map<std::string, std::string>
      imported_aliases;  // Tracks Pin -> pymcu.hal.gpio
  std::map<std::string, int>
      constant_variables;  // Tracks variables holding constants (for folding)
  std::map<std::string, std::string>
      variable_aliases;  // Tracks param -> arg mappings for properties
  std::string
      pending_constructor_target;  // Target variable for constructor inlining

  // Zero-Cost Abstraction: Virtual Instance Registry
  // Tracks instance variables that are compile-time constructs (no RAM needed).
  // An instance is "virtual" when its class has ALL methods @inline and the
  // constructor arguments are compile-time constants. Virtual instances:
  //   - Do NOT allocate RAM for the instance variable itself
  //   - Do NOT emit Copy instructions for member assignments with constant values
  //   - Have all member values tracked in constant_variables for folding
  std::set<std::string> virtual_instances;

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
  std::map<std::string, std::vector<std::string>> module_source_lines;
  std::string current_source_file;
  int last_line = -1;
  int current_stmt_line = 0;  // Tracks the current statement's source line

  // Intrinsic tracking
  std::set<std::string> intrinsic_names;

  struct FunctionEntry {
    std::string prefix;
    const FunctionDef *func;
    std::string source_file;
  };
  std::vector<FunctionEntry> functions_to_compile;
  std::map<std::string, int> string_literal_ids;
  int next_string_id = 1;

  tacky::Temporary make_temp(DataType type = DataType::UINT8);

  std::string make_label();

  void emit(const tacky::Instruction &inst);

  tacky::Val resolve_binding(const std::string &name);

  std::string resolve_callee(const std::string &name);
  DataType resolve_type(const std::string &type_str);

  void scan_globals(const Program &ast, ModuleScope *scope = nullptr);

  void scan_functions(const Program &ast, ModuleScope *scope = nullptr);

  tacky::Function visitFunction(const FunctionDef *funcNode);
  void visitClassDef(const ClassDef *classNode);

  void visitBlock(const Block *block);

  void visitStatement(const Statement *stmt);

  void visitReturn(const ReturnStmt *stmt);

  void visitIf(const IfStmt *stmt);

  void visitMatch(const MatchStmt *stmt);

  void visitWhile(const WhileStmt *stmt);

  void visitFor(const ForStmt *stmt);

  void visitBreak(const BreakStmt *stmt);

  void visitContinue(const ContinueStmt *stmt);

  void visitAssign(const AssignStmt *stmt);

  void visitAugAssign(const AugAssignStmt *stmt);

  void visitVarDecl(const VarDecl *stmt);

  void visitAnnAssign(const AnnAssign *stmt);

  void visitExprStmt(const ExprStmt *stmt);

  void visitGlobal(const GlobalStmt *stmt);

  // Helper for boolean optimization
  /// Returns: 0 = not optimized, 1 = statically true, -1 = statically false
  int emit_optimized_conditional_jump(const Expression *cond,
                                      const std::string &target_label,
                                      bool jump_if_true = false);

  tacky::Val visitExpression(const Expression *expr);

  tacky::Val visitBinary(const BinaryExpr *expr);

  tacky::Val visitUnary(const UnaryExpr *expr);
  tacky::Val visitCall(const CallExpr *expr);
  tacky::Val visitYield(const YieldExpr *expr);
  tacky::Val visitIndex(const IndexExpr *expr);

  static tacky::Val visitLiteral(const IntegerLiteral *expr);

  tacky::Val visitVariable(const VariableExpr *expr);

  tacky::Val visitMemberAccess(const MemberAccessExpr *expr);

  int evaluate_constant_expr(const Expression *expr);
};

#endif  // IRGENERATOR_H