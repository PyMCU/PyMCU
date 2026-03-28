/*
 * -----------------------------------------------------------------------------
 * PyMCU Compiler (pymcuc)
 * Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
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

#ifndef IRGENERATOR_H
#define IRGENERATOR_H

#pragma once
#include <map>
#include <optional>
#include <set>
#include <unordered_map>
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
  int ctor_anon_id_ = 0;  // Counter for synthetic ZCA constructor-as-arg targets
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
  // Maps "ClassName.property_name" -> qualified setter inline function key.
  // Populated by scan_functions when a @name.setter method is encountered.
  // Used by visitAssign to desugar "obj.attr = val" into an inline setter call.
  std::map<std::string, std::string> property_setters;

  // Function overloading: tracks qualified function names that have multiple
  // @inline overloads distinguished by parameter types.
  // scan_functions populates this; visitCall uses it for type-based dispatch.
  std::set<std::string> overloaded_functions;

  // Class inheritance: maps "ChildClassName" -> "base_prefix_" (e.g., "GPIODevice_")
  // so that super().__init__() and default-ctor inheritance can be resolved.
  std::map<std::string, std::string> class_base_prefixes;
  std::map<std::string, std::string>
      imported_aliases;  // Tracks Pin/_Pin -> pymcu.hal.gpio
  std::map<std::string, std::string>
      alias_to_original;  // Tracks _Pin -> Pin (for "from X import Pin as _Pin")
  std::map<std::string, int>
      constant_variables;  // Tracks variables holding constants (for folding)
  std::map<std::string, std::string>
      variable_aliases;  // Tracks param -> arg mappings for properties
  std::string
      pending_constructor_target;  // Target variable for constructor inlining

  // Tuple-unpack multi-return support.
  // Set by visitTupleUnpack before calling visitCall to tell the inline
  // expansion how many result slots to allocate.
  int pending_tuple_count_ = 0;
  // After an inline multi-return call completes, holds the qualified names of
  // the result variables (one per tuple element) so visitTupleUnpack can copy
  // them to the declared targets.
  std::vector<std::string> last_tuple_results_;

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
    // Multi-return tuple: each result slot is a named variable "prefix.result_K"
    // Non-empty only when the inline function returns a TupleExpr.
    std::vector<std::string> result_vars;
    // Full resolved callee name, used to look up function_return_types in visitReturn.
    std::string callee_name;
    // Set to true after the first return value has been assigned.
    // Subsequent return statements (dead code after match/if branches) only emit
    // Jump without overwriting the result — prevents `return 0` fallbacks from
    // clobbering values assigned by earlier branches (e.g., return eeprom_read(addr)).
    bool result_assigned = false;
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

  // compile_isr() registrations: bare function name -> interrupt vector.
  // Filled during visitCall when compile_isr() is encountered; applied to
  // ir_program.functions after all functions are compiled in generate().
  std::map<std::string, int> pending_isr_registrations;

  // @extern("symbol") registrations: Whipsnake function name -> C symbol name.
  // Functions in this map have no IR body; call sites emit Call{c_symbol, ...}.
  // The set of C symbol values is exported to tacky::Program::extern_symbols.
  std::map<std::string, std::string> extern_function_map;

  struct FunctionEntry {
    std::string prefix;
    const FunctionDef *func;
    std::string source_file;
  };
  std::vector<FunctionEntry> functions_to_compile;
  std::map<std::string, int> string_literal_ids;
  std::map<int, std::string> string_id_to_str;  // reverse map: id → string value
  int next_string_id = 256;  // Start above uint8 range to avoid aliasing True(1)/False(0)
  // Tracks temporaries/variables that hold MemoryAddress values from inline returns.
  // Used to resolve ptr-returning inline helpers (e.g. select_port("PB5") → PORTB addr).
  std::map<std::string, int> constant_address_variables;
  // Tracks compile-time string constant variables (for const[str] params / string for-in)
  std::unordered_map<std::string, std::string> str_constant_variables;
  // Tracks compile-time float constant variables (never emitted to AVR; folded to int at use)
  std::unordered_map<std::string, double> float_constant_variables;
  int float_ct_counter_ = 0;
  // Maps class name → module prefix where the class is defined.
  // Allows Direction.OUTPUT inside inline bodies to resolve to the correct global
  // (e.g. class_module_map["Direction"] = "digitalio_" → "digitalio_Direction_OUTPUT").
  std::unordered_map<std::string, std::string> class_module_map;

  // Fixed-size array support: maps qualified variable name → element count / element type.
  // Only variables declared with a TYPE[N] annotation (e.g. "uint8[4]") are in these maps.
  // Subscript on an array-declared variable → element access (Copy to/from synth var NAME__k).
  // Subscript on any other variable → bit-slice (existing BitCheck/BitSet/BitClear path).
  std::map<std::string, int>      array_sizes;       // qualified_name → element count
  std::map<std::string, DataType> array_elem_types;  // qualified_name → element DataType

  // Arrays that are subscripted with at least one non-constant index anywhere in the current
  // function. These use contiguous SRAM allocation + ArrayLoad/ArrayStore IR instructions.
  // Arrays NOT in this set keep the original synthetic-scalar approach (zero overhead).
  std::set<std::string> arrays_with_variable_index;

  // Module-level arrays that unconditionally use SRAM (bytearray declarations at global scope).
  // Unlike arrays_with_variable_index this set is NEVER cleared between functions so that
  // non-inline functions can access module-level ring buffers with variable indices.
  std::set<std::string> module_sram_arrays;

  // Lambda support (F9).
  // lambda_functions_map: lambda key → const LambdaExpr* (owned by AST).
  // lambda_variable_names: qualified var name → lambda key.
  // pending_lambda_key: set by visitLambdaExpr, consumed by visitAssign / call site.
  std::unordered_map<std::string, const LambdaExpr *> lambda_functions_map;
  std::unordered_map<std::string, std::string> lambda_variable_names;
  int lambda_counter = 0;
  std::string pending_lambda_key;

  // Pre-scan a function body (list of statements) to detect variable-index array accesses.
  // Populates arrays_with_variable_index before IR generation begins.
  void scanForVariableIndexedArrays(const std::vector<std::unique_ptr<Statement>> &body,
                                    const std::string &function_prefix);

  tacky::Temporary make_temp(DataType type = DataType::UINT8);

  std::string make_label();

  void emit(const tacky::Instruction &inst);

  tacky::Val resolve_binding(const std::string &name);

  std::optional<std::string> resolve_str_constant(const std::string &name) const;
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

  void visitWith(const WithStmt *stmt);      // T2.2

  void visitAssert(const AssertStmt *stmt);  // T2.3

  void visitAssign(const AssignStmt *stmt);

  void visitAugAssign(const AugAssignStmt *stmt);

  void visitVarDecl(const VarDecl *stmt);

  void visitAnnAssign(const AnnAssign *stmt);

  void visitExprStmt(const ExprStmt *stmt);

  void visitGlobal(const GlobalStmt *stmt);

  // F10 (PEP 3104): nonlocal name1, name2
  // Inside an @inline expansion, aliases inner-prefixed names to the outer
  // function's qualified variables so reads and writes bypass the inline scope.
  void visitNonlocal(const NonlocalStmt *stmt);

  // Handles `a, b = expr` tuple unpacking assignments.
  void visitTupleUnpack(const TupleUnpackStmt *stmt);

  // Helper for boolean optimization
  /// Returns: 0 = not optimized, 1 = statically true, -1 = statically false
  int emit_optimized_conditional_jump(const Expression *cond,
                                      const std::string &target_label,
                                      bool jump_if_true = false);

  tacky::Val visitExpression(const Expression *expr);

  tacky::Val visitBinary(const BinaryExpr *expr);
  tacky::Val visitTernary(const TernaryExpr *expr);

  tacky::Val visitUnary(const UnaryExpr *expr);
  tacky::Val visitCall(const CallExpr *expr);
  tacky::Val visitYield(const YieldExpr *expr);
  tacky::Val visitIndex(const IndexExpr *expr);

  static tacky::Val visitLiteral(const IntegerLiteral *expr);

  tacky::Val visitVariable(const VariableExpr *expr);

  tacky::Val visitMemberAccess(const MemberAccessExpr *expr);

  tacky::Val visitFStringExpr(const FStringExpr *expr);

  tacky::Val visitLambdaExpr(const LambdaExpr *expr);  // F9

  // F7: Dunder dispatch helper. Inlines ClassName___method(self, args...).
  // self_val: the Val of the LHS/receiver. self_qname: its qualified variable name.
  // class_name: the ClassName prefix. func_key: inline_functions key.
  // extra_args: additional Val arguments (beyond self).
  // Returns the result Val, or Constant{0} for void.
  tacky::Val emit_dunder_call(const std::string &self_qname,
                              const std::string &class_name,
                              const std::string &func_key,
                              const std::vector<tacky::Val> &extra_args);

  // Returns the class name of a Val (via instance_classes lookup), or empty string.
  std::string get_val_class(const tacky::Val &v) const;

  int evaluate_constant_expr(const Expression *expr);

  // Infer DataType of an expression without emitting IR (best effort).
  // Used for overload resolution at call sites.
  DataType infer_expr_type(const Expression *expr) const;

  // Build a type-suffix string from a parameter list (skipping "self").
  // Used for function overload mangling: e.g., "uint8", "uint16_uint8".
  static std::string build_overload_suffix(const std::vector<Param> &params);

  // Unroll a list comprehension into per-element assignments for a named array.
  // Called from visitAnnAssign when the RHS is a ListCompExpr.
  void visitListComp(const ListCompExpr *lc, const std::string &qualified_name,
                     int count, DataType elem_dt);
};

#endif  // IRGENERATOR_H