#include "IRGenerator.h"
#include <format>
#include <iostream>
#include <stdexcept>
#include <typeinfo>

tacky::Temporary IRGenerator::make_temp(DataType type) {
  return tacky::Temporary{"tmp." + std::to_string(temp_counter++), type};
}

std::string IRGenerator::make_label() {
  return std::format("L.{}", label_counter++);
}

void IRGenerator::emit(const tacky::Instruction &inst) {
  current_instructions.push_back(inst);
}

tacky::Program
IRGenerator::generate(const Program &main_ast,
                      const std::vector<const Program *> &imported_modules,
                      const std::vector<std::string> &source_lines) {
  this->source_lines = source_lines;
  this->last_line = -1;
  tacky::Program ir_program;
  globals.clear();
  mutable_globals.clear();
  function_return_types.clear();
  function_params.clear();

  for (const auto *mod : imported_modules) {
    scan_globals(*mod);
    scan_functions(*mod);
  }

  scan_globals(main_ast);
  scan_functions(main_ast);

  for (const auto &func_def : main_ast.functions) {
    ir_program.functions.push_back(visitFunction(func_def.get()));
  }

  // Pass mutable global variable names and types to the backend for RAM
  // allocation
  for (const auto &[name, type] : mutable_globals) {
    ir_program.globals.push_back(tacky::Variable{name, type});
  }

  loop_stack.clear();
  return ir_program;
}

tacky::Val IRGenerator::resolve_binding(const std::string &name) {
  // Check if it's a known compile-time constant or memory-mapped register
  if (const auto it = globals.find(name); it != globals.end()) {
    if (it->second.is_memory_address) {
      return tacky::MemoryAddress{it->second.value, it->second.type};
    } else {
      return tacky::Constant{it->second.value};
    }
  }

  if (mutable_globals.contains(name)) {
    return tacky::Variable{name, mutable_globals.at(name)};
  }

  std::string local_name;
  if (!current_inline_prefix.empty()) {
    local_name = current_inline_prefix + name;
  } else {
    local_name = current_function + "." + name;
  }

  DataType type = DataType::UINT8;
  if (variable_types.contains(local_name)) {
    type = variable_types.at(local_name);
  }
  return tacky::Variable{local_name, type};
}

void IRGenerator::scan_globals(const Program &ast) {
  for (const auto &stmt : ast.global_statements) {
    std::string name;
    std::string type;
    const Expression *initializer = nullptr;

    if (const auto varDecl = dynamic_cast<const VarDecl *>(stmt.get())) {
      name = varDecl->name;
      type = varDecl->var_type;
      initializer = varDecl->init.get();
    } else if (const auto assign =
                   dynamic_cast<const AssignStmt *>(stmt.get())) {
      if (const auto varExpr =
              dynamic_cast<const VariableExpr *>(assign->target.get())) {
        name = varExpr->name;
        initializer = assign->value.get();
      }
    } else if (const auto annAssign =
                   dynamic_cast<const AnnAssign *>(stmt.get())) {
      name = annAssign->target;
      type = annAssign->annotation; // Use annotation as type
      initializer = annAssign->value.get();
    }

    if (!name.empty() && initializer) {
      try {
        const int val = evaluate_constant_expr(initializer);
        bool is_memory_address = false;

        // If we have an initializer that is a call to ptr or PIORegister, it's
        // a memory address
        if (const auto call = dynamic_cast<const CallExpr *>(initializer)) {
          if (call->callee == "ptr" || call->callee == "PIORegister") {
            is_memory_address = true;
          }
        }

        // Also check type hint
        if (!type.empty() && (type.find("ptr") != std::string::npos ||
                              type.find("PIORegister") != std::string::npos)) {
          is_memory_address = true;
        }

        if (is_memory_address) {
          globals[name] = SymbolInfo{true, val, resolve_type(type)};
        } else {
          // Distinguish true constants (ALL_CAPS naming convention, e.g.
          // BUTTON_PIN) from mutable variables (lowercase/mixed case, e.g.
          // error_flags). Constants like BUTTON_PIN = 0 remain compile-time
          // constants. Mutable variables need RAM allocation.
          bool is_all_upper = true;
          for (char c : name) {
            if (std::islower(static_cast<unsigned char>(c))) {
              is_all_upper = false;
              break;
            }
          }
          if (is_all_upper) {
            globals[name] = SymbolInfo{false, val};
          } else {
            // Mutable global: needs RAM, store initial value for later Copy
            mutable_globals[name] = resolve_type(type);
          }
        }
      } catch (...) {
        // Non-constant initializer: this is a runtime variable, needs RAM
        mutable_globals[name] = resolve_type(type);
      }
    }
  }
}

void IRGenerator::scan_functions(const Program &ast) {
  for (const auto &func : ast.functions) {
    function_return_types[func->name] = func->return_type;
    std::vector<std::string> params;
    for (const auto &p : func->params) {
      params.push_back(p.name);
    }
    function_params[func->name] = params;
    if (func->is_inline) {
      inline_functions[func->name] = func.get();
    }
  }
}

int IRGenerator::evaluate_constant_expr(const Expression *expr) {
  if (const auto num = dynamic_cast<const IntegerLiteral *>(expr)) {
    return num->value;
  }

  if (const auto call = dynamic_cast<const CallExpr *>(expr)) {
    if ((call->callee == "ptr" || call->callee == "PIORegister") &&
        call->args.size() == 1) {
      return evaluate_constant_expr(call->args[0].get());
    }
  }

  throw std::runtime_error("Not a constant expression");
}

tacky::Function IRGenerator::visitFunction(const FunctionDef *funcNode) {
  tacky::Function ir_func;
  ir_func.name = funcNode->name;
  current_function = funcNode->name;

  // Copy inline flag from AST
  ir_func.is_inline = funcNode->is_inline;

  current_instructions.clear();
  loop_stack.clear();

  for (const auto &[name, type] : funcNode->params) {
    ir_func.params.push_back(current_function + "." + name);
  }

  visitBlock(funcNode->body.get());

  if (current_instructions.empty() ||
      !std::holds_alternative<tacky::Return>(current_instructions.back())) {
    emit(tacky::Return{std::monostate{}});
  }

  ir_func.body = current_instructions;
  return ir_func;
}

void IRGenerator::visitBlock(const Block *block) {
  for (const auto &stmt : block->statements) {
    visitStatement(stmt.get());
  }
}

void IRGenerator::visitStatement(const Statement *stmt) {
  if (stmt->line > 0 && stmt->line != last_line) {
    if (stmt->line <= source_lines.size()) {
      emit(tacky::DebugLine{stmt->line, source_lines[stmt->line - 1]});
      last_line = stmt->line;
    }
  }

  if (auto *block = dynamic_cast<const Block *>(stmt))
    return visitBlock(block);
  if (auto *ret = dynamic_cast<const ReturnStmt *>(stmt))
    return visitReturn(ret);
  if (auto ifStmt = dynamic_cast<const IfStmt *>(stmt))
    return visitIf(ifStmt);
  if (auto matchStmt = dynamic_cast<const MatchStmt *>(stmt))
    return visitMatch(matchStmt);
  if (auto whileStmt = dynamic_cast<const WhileStmt *>(stmt))
    return visitWhile(whileStmt);
  if (auto *breakStmt = dynamic_cast<const BreakStmt *>(stmt))
    return visitBreak(breakStmt);
  if (auto *continueStmt = dynamic_cast<const ContinueStmt *>(stmt))
    return visitContinue(continueStmt);
  if (auto *assign = dynamic_cast<const AssignStmt *>(stmt))
    return visitAssign(assign);
  if (auto *augAssign = dynamic_cast<const AugAssignStmt *>(stmt))
    return visitAugAssign(augAssign);
  if (auto *decl = dynamic_cast<const VarDecl *>(stmt))
    return visitVarDecl(decl);
  if (auto *annAssign = dynamic_cast<const AnnAssign *>(stmt))
    return visitAnnAssign(annAssign);
  if (auto *exprStmt = dynamic_cast<const ExprStmt *>(stmt))
    return visitExprStmt(exprStmt);
  if (auto *delayStmt = dynamic_cast<const DelayStmt *>(stmt))
    return visitDelayStmt(delayStmt);

  if (dynamic_cast<const PassStmt *>(stmt))
    return;

  if (!stmt) {
    throw std::runtime_error("IR Generation: Statement pointer is null");
  }
  throw std::runtime_error(
      std::string("IR Generation: Unknown Statement type: ") +
      typeid(*stmt).name());
}

void IRGenerator::visitReturn(const ReturnStmt *stmt) {
  tacky::Val val = std::monostate{};
  if (stmt->value) {
    val = visitExpression(stmt->value.get());
  }

  if (!inline_stack.empty()) {
    const auto &ctx = inline_stack.back();
    if (ctx.result_temp.has_value()) {
      emit(tacky::Copy{val, ctx.result_temp.value()});
    }
    emit(tacky::Jump{ctx.exit_label});
  } else {
    emit(tacky::Return{val});
  }
}

// Helper: Try to detect and emit optimized bit test jumps
bool IRGenerator::emit_optimized_conditional_jump(
    const Expression *cond, const std::string &target_label,
    bool jump_if_true) {

  // Helper to resolve constant integers (literals or global constants)
  auto resolve_int = [&](const Expression *expr) -> std::optional<int> {
    if (const auto num = dynamic_cast<const IntegerLiteral *>(expr)) {
      return num->value;
    }
    if (const auto var = dynamic_cast<const VariableExpr *>(expr)) {
      if (globals.contains(var->name) &&
          !globals.at(var->name).is_memory_address) {
        return globals.at(var->name).value;
      }
    }
    return std::nullopt;
  };

  // Pattern A: BitAccess(addr, bit) == 0/1
  // Case 1: Explicit Comparison
  if (auto binExpr = dynamic_cast<const BinaryExpr *>(cond)) {
    if (binExpr->op == BinaryOp::Equal || binExpr->op == BinaryOp::NotEqual) {
      const IndexExpr *indexExpr =
          dynamic_cast<const IndexExpr *>(binExpr->left.get());
      const Expression *rhsExpr = binExpr->right.get();

      // Swap if needed
      if (!indexExpr) {
        indexExpr = dynamic_cast<const IndexExpr *>(binExpr->right.get());
        rhsExpr = binExpr->left.get();
      }

      if (indexExpr) {
        auto bitVal = resolve_int(indexExpr->index.get());
        auto targetVal = resolve_int(rhsExpr);

        if (bitVal.has_value() && targetVal.has_value()) {
          tacky::Val addr = visitExpression(indexExpr->target.get());
          int bit = bitVal.value();
          int target = targetVal.value();

          bool invert = (binExpr->op == BinaryOp::NotEqual);
          if (invert)
            target = !target;

          if (target == 0) {
            // == 0: Jump if Bit is 1 (SET)
            // != 0: Jump if Bit is 0 (CLEAR) (Not handled by user explicit
            // request but logical) User Request: == 0 -> BTFSC (Jump if Set).
            // My backend: JumpIfBitSet -> BTFSC.
            if (jump_if_true) {
              // Jump to TARGET if condition True (Bit is 0). Jump if Clear.
              emit(tacky::JumpIfBitClear{addr, bit, target_label});
            } else {
              // Jump to ELSE (Target) if condition False (Bit is 1). Jump if
              // Set.
              emit(tacky::JumpIfBitSet{addr, bit, target_label});
            }
            return true;
          } else if (target == 1) {
            // == 1: Jump if Bit is 0 (CLEAR)
            if (jump_if_true) {
              // Jump to TARGET if condition True (Bit is 1). Jump if Set.
              emit(tacky::JumpIfBitSet{addr, bit, target_label});
            } else {
              // Jump to ELSE (Target) if condition False (Bit is 0). Jump if
              // Clear.
              emit(tacky::JumpIfBitClear{addr, bit, target_label});
            }
            return true;
          }
        }
      }
    }
  }

  // Pattern C: Unary Not (if not bit)
  // Case 2: Pythonic NOT
  if (auto *unaryExpr = dynamic_cast<const UnaryExpr *>(cond)) {
    if (unaryExpr->op == UnaryOp::Not) {
      if (auto *indexExpr =
              dynamic_cast<const IndexExpr *>(unaryExpr->operand.get())) {
        auto bitVal = resolve_int(indexExpr->index.get());
        if (bitVal.has_value()) {
          tacky::Val addr = visitExpression(indexExpr->target.get());
          int bit = bitVal.value();

          // Condition: Bit is 0.
          if (jump_if_true) {
            // Jump to Target if True (Bit is 0). Jump if Clear.
            emit(tacky::JumpIfBitClear{addr, bit, target_label});
          } else {
            // Jump to Else (Target) if False (Bit is 1). Jump if Set.
            // ASM: BTFSC (Jump if Set) -> GOTO Else.
            emit(tacky::JumpIfBitSet{addr, bit, target_label});
          }
          return true;
        }
      }
    }
  }

  // Pattern B: Single BitAccess (implicit true test)
  // Case 3: Implicit Truthiness
  if (auto *indexExpr = dynamic_cast<const IndexExpr *>(cond)) {
    auto bitVal = resolve_int(indexExpr->index.get());
    if (bitVal.has_value()) {
      tacky::Val addr = visitExpression(indexExpr->target.get());
      int bit = bitVal.value();

      // Condition: Bit is 1.
      if (jump_if_true) {
        // Jump to Target if True (Bit is 1). Jump if Set.
        emit(tacky::JumpIfBitSet{addr, bit, target_label});
      } else {
        // Jump to Else (Target) if False (Bit is 0). Jump if Clear.
        // ASM: BTFSS (Jump if Clear) -> GOTO Else.
        emit(tacky::JumpIfBitClear{addr, bit, target_label});
      }
      return true;
    }
  }

  return false;
}

void IRGenerator::visitIf(const IfStmt *stmt) {
  std::string end_label = make_label();

  // 1. Main If Condition
  std::string next_label = (stmt->elif_branches.empty() && !stmt->else_branch)
                               ? end_label
                               : make_label();

  // "If boolean optimization": Jump to next_label if condition is FALSE
  if (!emit_optimized_conditional_jump(stmt->condition.get(), next_label,
                                       false)) {
    // Fall back to standard evaluation
    tacky::Val cond_val = visitExpression(stmt->condition.get());
    emit(tacky::JumpIfZero{cond_val, next_label});
  }

  visitStatement(stmt->then_branch.get());

  // Only emit jump to end if we have other branches
  if (!stmt->elif_branches.empty() || stmt->else_branch) {
    emit(tacky::Jump{end_label});
  }

  // 2. Elif Branches
  for (size_t i = 0; i < stmt->elif_branches.size(); ++i) {
    emit(tacky::Label{next_label});

    bool is_last_elif = (i == stmt->elif_branches.size() - 1);
    next_label =
        (is_last_elif && !stmt->else_branch) ? end_label : make_label();

    const auto &[elif_cond, elif_block] = stmt->elif_branches[i];

    // "Elif boolean optimization": Jump to next_label if condition is FALSE
    if (!emit_optimized_conditional_jump(elif_cond.get(), next_label, false)) {
      tacky::Val elif_val = visitExpression(elif_cond.get());
      emit(tacky::JumpIfZero{elif_val, next_label});
    }

    visitStatement(elif_block.get());

    if (!is_last_elif || stmt->else_branch) {
      emit(tacky::Jump{end_label});
    }
  }

  // 3. Else Branch
  if (stmt->else_branch) {
    emit(tacky::Label{next_label});
    visitStatement(stmt->else_branch.get());
  }

  emit(tacky::Label{end_label});
}

void IRGenerator::visitMatch(const MatchStmt *stmt) {
  // Generate Match/Case Logic
  // 1. Evaluate Target -> Temp
  // 2. Case 1:
  //    Temp == Pattern ?
  //    JumpIfZero -> Next Case
  //    Body
  //    Jump -> End
  // ...
  // N. Default Case (_)
  //    Body
  //    Jump -> End

  tacky::Val target_val = visitExpression(stmt->target.get());

  // Optimization: If target is complex, store it in a temp to avoid
  // re-evaluation? visitExpression usually returns a Variable, Constant, or
  // Temporary. So it is safe to reuse `target_val` without re-evaluation side
  // effects UNLESS visitExpression emitted instructions that we shouldn't
  // repeat. Since `visitExpression` emits instructions to calculate the value,
  // `target_val` holds the *result*. So we are good.
  // HOWEVER, if the result is a temporary that might be clobbered, we should be
  // careful. In TACKY, temporaries are unique variables, so it's fine.

  std::string end_label = make_label();

  for (const auto &branch : stmt->branches) {
    std::string next_case_label = make_label();

    if (branch.pattern) {
      // Specific Pattern
      tacky::Val pattern_val = visitExpression(branch.pattern.get());

      // Generate equality check: target == pattern
      tacky::Temporary cmp_res = make_temp();
      emit(tacky::Binary{tacky::BinaryOp::Equal, target_val, pattern_val,
                         cmp_res});

      // If false (0), jump to next case
      emit(tacky::JumpIfZero{cmp_res, next_case_label});

      // Execute body
      visitBlock(dynamic_cast<const Block *>(branch.body.get()));
      emit(tacky::Jump{end_label});
    } else {
      // Wildcard Case (_)
      // Always execute if we reached here
      visitBlock(dynamic_cast<const Block *>(branch.body.get()));
      emit(tacky::Jump{end_label});
    }

    emit(tacky::Label{next_case_label});
  }

  emit(tacky::Label{end_label});
}

void IRGenerator::visitWhile(const WhileStmt *stmt) {
  const std::string start_label = make_label();
  const std::string end_label = make_label();

  loop_stack.push_back({start_label, end_label});

  emit(tacky::Label{start_label});

  // "While boolean optimization": Jump to end_label if condition is FALSE
  if (!emit_optimized_conditional_jump(stmt->condition.get(), end_label,
                                       false)) {
    // Fall back to standard evaluation
    const tacky::Val cond_val = visitExpression(stmt->condition.get());
    emit(tacky::JumpIfZero{cond_val, end_label});
  }

  visitStatement(stmt->body.get());

  emit(tacky::Jump{start_label});
  emit(tacky::Label{end_label});
  loop_stack.pop_back();
}

void IRGenerator::visitBreak(const BreakStmt *stmt) {
  if (loop_stack.empty()) {
    throw std::runtime_error("Break statement outside of loop");
  }
  emit(tacky::Jump{loop_stack.back().break_label});
}

void IRGenerator::visitContinue(const ContinueStmt *stmt) {
  if (loop_stack.empty()) {
    throw std::runtime_error("Continue statement outside of loop");
  }
  emit(tacky::Jump{loop_stack.back().continue_label});
}

void IRGenerator::visitAssign(const AssignStmt *stmt) {
  if (auto indexExpr = dynamic_cast<const IndexExpr *>(stmt->target.get())) {
    tacky::Val target = visitExpression(indexExpr->target.get());
    tacky::Val indexVal = visitExpression(indexExpr->index.get());
    int bit = 0;

    if (auto c = std::get_if<tacky::Constant>(&indexVal)) {
      bit = c->value;
    } else {
      throw std::runtime_error("Bit index must be constant");
    }

    tacky::Val val = visitExpression(stmt->value.get());

    if (auto c = std::get_if<tacky::Constant>(&val)) {
      if (c->value != 0) {
        emit(tacky::BitSet{target, bit});
      } else {
        emit(tacky::BitClear{target, bit});
      }
    } else {
      emit(tacky::BitWrite{target, bit, val});
    }
    return;
  }

  tacky::Val value = visitExpression(stmt->value.get());

  if (auto varExpr = dynamic_cast<const VariableExpr *>(stmt->target.get())) {
    tacky::Val target = resolve_binding(varExpr->name);
    emit(tacky::Copy{value, target});
  } else if (auto memExpr =
                 dynamic_cast<const MemberAccessExpr *>(stmt->target.get())) {
    if (memExpr->member == "value") {
      tacky::Val target = visitExpression(memExpr->object.get());

      // Look up the variable type to determine if multi-byte operation
      DataType var_type = DataType::UINT8; // Default
      if (auto var = std::get_if<tacky::Variable>(&target)) {
        if (variable_types.contains(var->name)) {
          var_type = variable_types.at(var->name);
        }
      }

      // Check if this is a multi-byte type
      int byte_count = size_of(var_type);

      if (byte_count == 1) {
        // 8-bit: Single byte write
        if (std::holds_alternative<tacky::MemoryAddress>(target) ||
            std::holds_alternative<tacky::Variable>(target)) {
          emit(tacky::Copy{value, target});
        } else {
          throw std::runtime_error(
              "Cannot assign to .value of this expression type");
        }
      } else if (byte_count == 2) {
        // 16-bit: Two byte writes (low, high)
        if (auto addr = std::get_if<tacky::MemoryAddress>(&target)) {
          // If value is a constant, extract bytes directly
          if (auto const_val = std::get_if<tacky::Constant>(&value)) {
            int full_value = const_val->value;
            int low_byte = full_value & 0xFF;
            int high_byte = (full_value >> 8) & 0xFF;

            emit(tacky::Copy{tacky::Constant{low_byte},
                             tacky::MemoryAddress{addr->address}});
            emit(tacky::Copy{tacky::Constant{high_byte},
                             tacky::MemoryAddress{addr->address + 1}});
          } else {
            // For variable values, need runtime extraction
            tacky::Temporary low_byte = make_temp();
            emit(tacky::Binary{tacky::BinaryOp::BitAnd, value,
                               tacky::Constant{0xFF}, low_byte});
            emit(tacky::Copy{low_byte, tacky::MemoryAddress{addr->address}});

            tacky::Temporary high_byte = make_temp();
            emit(tacky::Binary{tacky::BinaryOp::RShift, value,
                               tacky::Constant{8}, high_byte});
            emit(tacky::Copy{high_byte,
                             tacky::MemoryAddress{addr->address + 1}});
          }
        } else {
          throw std::runtime_error(
              "16-bit .value assignment requires constant address");
        }
      } else {
        throw std::runtime_error("Unsupported type size for .value assignment");
      }
    } else {
      throw std::runtime_error("Unknown member access in assignment: " +
                               memExpr->member);
    }
  } else {
    throw std::runtime_error("Invalid assignment target");
  }
}

void IRGenerator::visitVarDecl(const VarDecl *stmt) {
  // Track variable type
  DataType dt = resolve_type(stmt->var_type);
  // Stores 'FuncName.VarName' in IR
  std::string qualified = current_function.empty()
                              ? stmt->name
                              : current_function + "." + stmt->name;
  variable_types[qualified] = dt;

  // Create the Variable
  if (stmt->init) {
    tacky::Val val = visitExpression(stmt->init.get());
    tacky::Val target = resolve_binding(stmt->name);
    // Set type on the target variable
    if (auto v = std::get_if<tacky::Variable>(&target)) {
      v->type = dt;
    }
    emit(tacky::Copy{val, target});
  }
}

void IRGenerator::visitAnnAssign(const AnnAssign *stmt) {
  // Extract type from annotation string like "ptr[uint16]"
  DataType type = DataType::UINT8; // default

  if (stmt->annotation.find("ptr[uint16]") != std::string::npos) {
    type = DataType::UINT16;
  } else if (stmt->annotation.find("ptr[uint32]") != std::string::npos) {
    type = DataType::UINT32;
  } else if (stmt->annotation.find("uint16") != std::string::npos) {
    type = DataType::UINT16;
  } else if (stmt->annotation.find("uint32") != std::string::npos) {
    type = DataType::UINT32;
  }
  // ptr[uint8] or plain "ptr" → UINT8 (default)

  // Store in variable_types map (use global name, not qualified)
  variable_types[stmt->target] = type;

  // Generate assignment IR if initializer present
  if (stmt->value) {
    tacky::Val rhs = visitExpression(stmt->value.get());

    // If RHS is a MemoryAddress, propagate the type
    if (auto *addr = std::get_if<tacky::MemoryAddress>(&rhs)) {
      addr->type = type;
    }

    // Create variable with type
    tacky::Variable var{stmt->target, type};
    emit(tacky::Copy{rhs, var});
  }
}

DataType IRGenerator::resolve_type(const std::string &type_str) {
  // Check if this is a ptr[TYPE] annotation
  if (type_str.find("ptr[") == 0 && type_str.back() == ']') {
    // Extract the element type from ptr[uint16] -> uint16
    size_t start = type_str.find('[') + 1;
    size_t end = type_str.find(']');
    std::string element_type = type_str.substr(start, end - start);
    return string_to_datatype(element_type);
  }
  return string_to_datatype(type_str);
}

void IRGenerator::visitAugAssign(const AugAssignStmt *stmt) {
  // Map AugOp to tacky::BinaryOp
  auto map_augop = [](AugOp op) -> tacky::BinaryOp {
    switch (op) {
    case AugOp::Add:
      return tacky::BinaryOp::Add;
    case AugOp::Sub:
      return tacky::BinaryOp::Sub;
    case AugOp::Mul:
      return tacky::BinaryOp::Mul;
    case AugOp::Div:
      return tacky::BinaryOp::Div;
    case AugOp::Mod:
      return tacky::BinaryOp::Mod;
    case AugOp::BitAnd:
      return tacky::BinaryOp::BitAnd;
    case AugOp::BitOr:
      return tacky::BinaryOp::BitOr;
    case AugOp::BitXor:
      return tacky::BinaryOp::BitXor;
    case AugOp::LShift:
      return tacky::BinaryOp::LShift;
    case AugOp::RShift:
      return tacky::BinaryOp::RShift;
    }
    return tacky::BinaryOp::Add; // Unreachable
  };

  tacky::Val operand = visitExpression(stmt->value.get());

  if (auto varExpr = dynamic_cast<const VariableExpr *>(stmt->target.get())) {
    tacky::Val target = resolve_binding(varExpr->name);
    emit(tacky::AugAssign{map_augop(stmt->op), target, operand});
  } else {
    throw std::runtime_error("Augmented assignment target must be a variable");
  }
}

void IRGenerator::visitExprStmt(const ExprStmt *stmt) {
  visitExpression(stmt->expr.get());
}

tacky::Val IRGenerator::visitExpression(const Expression *expr) {
  if (auto *bin = dynamic_cast<const BinaryExpr *>(expr))
    return visitBinary(bin);
  if (auto *un = dynamic_cast<const UnaryExpr *>(expr))
    return visitUnary(un);
  if (auto *num = dynamic_cast<const IntegerLiteral *>(expr))
    return visitLiteral(num);
  if (auto *var = dynamic_cast<const VariableExpr *>(expr))
    return visitVariable(var);
  if (auto *call = dynamic_cast<const CallExpr *>(expr))
    return visitCall(call);
  if (auto *idx = dynamic_cast<const IndexExpr *>(expr))
    return visitIndex(idx);
  if (auto *mem = dynamic_cast<const MemberAccessExpr *>(expr))
    return visitMemberAccess(mem);

  if (auto *boolean = dynamic_cast<const BooleanLiteral *>(expr)) {
    return tacky::Constant{boolean->value ? 1 : 0};
  }

  throw std::runtime_error("IR Generation: Unknown Expression type");
}

tacky::Val IRGenerator::visitLiteral(const IntegerLiteral *expr) {
  return tacky::Constant{expr->value};
}

tacky::Val IRGenerator::visitVariable(const VariableExpr *expr) {
  return resolve_binding(expr->name);
}

tacky::Val IRGenerator::visitBinary(const BinaryExpr *expr) {
  tacky::Val v1 = visitExpression(expr->left.get());
  tacky::Val v2 = visitExpression(expr->right.get());

  auto get_val_type = [](const tacky::Val &v) {
    if (auto var = std::get_if<tacky::Variable>(&v))
      return var->type;
    if (auto tmp = std::get_if<tacky::Temporary>(&v))
      return tmp->type;
    if (auto c = std::get_if<tacky::Constant>(&v)) {
      if (c->value > 255 || c->value < -128)
        return DataType::UINT16;
      return DataType::UINT8;
    }
    return DataType::UINT8;
  };

  DataType t1 = get_val_type(v1);
  DataType t2 = get_val_type(v2);
  DataType resType = (size_of(t1) >= size_of(t2)) ? t1 : t2;

  tacky::Temporary dst = make_temp(resType);
  tacky::BinaryOp op;
  switch (expr->op) {
  case BinaryOp::Add:
    op = tacky::BinaryOp::Add;
    break;
  case BinaryOp::Sub:
    op = tacky::BinaryOp::Sub;
    break;
  case BinaryOp::Mul:
    op = tacky::BinaryOp::Mul;
    break;
  case BinaryOp::Div:
    op = tacky::BinaryOp::Div;
    break;
  case BinaryOp::Mod:
    op = tacky::BinaryOp::Mod;
    break;
  case BinaryOp::Equal:
    op = tacky::BinaryOp::Equal;
    break;
  case BinaryOp::NotEqual:
    op = tacky::BinaryOp::NotEqual;
    break;
  case BinaryOp::Less:
    op = tacky::BinaryOp::LessThan;
    break;
  case BinaryOp::LessEq:
    op = tacky::BinaryOp::LessEqual;
    break;
  case BinaryOp::Greater:
    op = tacky::BinaryOp::GreaterThan;
    break;
  case BinaryOp::GreaterEq:
    op = tacky::BinaryOp::GreaterEqual;
    break;

  case BinaryOp::BitAnd:
    op = tacky::BinaryOp::BitAnd;
    break;
  case BinaryOp::BitOr:
    op = tacky::BinaryOp::BitOr;
    break;
  case BinaryOp::BitXor:
    op = tacky::BinaryOp::BitXor;
    break;
  case BinaryOp::LShift:
    op = tacky::BinaryOp::LShift;
    break;
  case BinaryOp::RShift:
    op = tacky::BinaryOp::RShift;
    break;
  default:
    throw std::runtime_error("Unsupported Binary Op");
  }

  emit(tacky::Binary{op, v1, v2, dst});
  return dst;
}

tacky::Val IRGenerator::visitUnary(const UnaryExpr *expr) {
  const tacky::Val src = visitExpression(expr->operand.get());
  tacky::Temporary dst = make_temp();

  tacky::UnaryOp op;
  switch (expr->op) {
  case UnaryOp::Negate:
    op = tacky::UnaryOp::Neg;
    break;
  case UnaryOp::Not:
    op = tacky::UnaryOp::Not;
    break;
  case UnaryOp::BitNot:
    op = tacky::UnaryOp::BitNot;
    break;
  default:
    throw std::runtime_error("Unsupported Unary Op");
  }

  emit(tacky::Unary{op, src, dst});
  return dst;
}

tacky::Val IRGenerator::visitIndex(const IndexExpr *expr) {
  tacky::Val target = visitExpression(expr->target.get());
  tacky::Val indexVal = visitExpression(expr->index.get());

  int bit = 0;
  if (auto c = std::get_if<tacky::Constant>(&indexVal)) {
    bit = c->value;
  } else {
    throw std::runtime_error("Bit index must be constant for reading");
  }

  tacky::Temporary dst = make_temp();
  emit(tacky::BitCheck{target, bit, dst});

  return dst;
}

tacky::Val IRGenerator::visitMemberAccess(const MemberAccessExpr *expr) {
  if (expr->member == "value") {
    // Get the underlying pointer variable
    tacky::Val obj = visitExpression(expr->object.get());

    // Look up the variable's type to propagate to MemoryAddress
    DataType var_type = DataType::UINT8; // default

    if (auto *var = std::get_if<tacky::Variable>(&obj)) {
      // Check if we have type information for this variable
      if (variable_types.contains(var->name)) {
        var_type = variable_types[var->name];
      }

      // Update the variable's type field
      var->type = var_type;
    }

    return obj;
  }
  throw std::runtime_error("Unknown member access: " + expr->member);
}

tacky::Val IRGenerator::visitCall(const CallExpr *expr) {
  // Inlining Support
  if (inline_functions.contains(expr->callee)) {
    const FunctionDef *func = inline_functions.at(expr->callee);
    std::string exit_label = make_label();

    // Calculate new prefix but don't set it yet
    int new_depth = inline_depth + 1;
    std::string new_prefix = std::format("inline{}.{}.", new_depth, func->name);

    std::optional<tacky::Temporary> result;

    if (func->return_type != "void" && func->return_type != "None") {
      result = make_temp(resolve_type(func->return_type));
    }

    // Evaluate args in CURRENT context
    std::vector<tacky::Val> argValues;
    for (const auto &arg : expr->args) {
      argValues.push_back(visitExpression(arg.get()));
    }

    // Switch context
    inline_depth++;
    std::string saved_prefix = current_inline_prefix;
    current_inline_prefix = new_prefix;
    inline_stack.push_back({exit_label, result});

    // Assign args to params in NEW context
    for (size_t i = 0; i < argValues.size(); ++i) {
      std::string paramName = current_inline_prefix + func->params[i].name;
      // We need to declare the param variable type if not exists?
      // Actually visitVarDecl isn't called for params.
      // We should verify variable_types has it?
      // scan_functions adds params to variable_types with prefix?
      // No, scan_functions adds them with function_name prefix.
      // We might need to handle this?
      // For now just assign.

      // Optimization: If argVal is Variable/Constant/Temp, Copy.
      emit(tacky::Copy{
          argValues[i],
          tacky::Variable{paramName,
                          DataType::UINT8}}); // Assuming UINT8 for now or
                                              // check param type
    }

    // Body
    visitBlock(func->body.get());

    emit(tacky::Label{exit_label});
    inline_stack.pop_back();

    current_inline_prefix = saved_prefix;
    inline_depth--;

    if (result)
      return *result;
    return std::monostate{};
  }

  // Eval args once
  std::vector<tacky::Val> arg_values;
  for (const auto &arg : expr->args) {
    arg_values.push_back(visitExpression(arg.get()));
  }

  // PIO Intrinsics Mapping
  std::string callee = expr->callee;
  if (function_params.contains(expr->callee)) {
    // Validate args count
    const auto &param_names = function_params[expr->callee];
    if (expr->args.size() != param_names.size()) {
      throw std::runtime_error(std::format(
          "Function '{}' expects {} arguments, but {} were provided",
          expr->callee, param_names.size(), expr->args.size()));
    }
    // Explicit Copy for Stack/Global ABI satisfaction (Fixes ArgumentsTest)
    for (size_t i = 0; i < arg_values.size(); ++i) {
      emit(tacky::Copy{arg_values[i],
                       tacky::Variable{expr->callee + "." + param_names[i]}});
    }
  }

  if (callee == "pull")
    callee = "__pio_pull";
  else if (callee == "push")
    callee = "__pio_push";
  else if (callee == "out")
    callee = "__pio_out";
  else if (callee == "in_")
    callee = "__pio_in";
  else if (callee == "wait")
    callee = "__pio_wait";

  tacky::Call callInstr;
  callInstr.function_name = callee;
  callInstr.args = arg_values;

  bool is_pio_intrinsic = callee.starts_with("__pio_") || callee == "delay";

  if (is_pio_intrinsic || (function_return_types.contains(expr->callee) &&
                           (function_return_types[expr->callee] == "void" ||
                            function_return_types[expr->callee] == "None"))) {
    callInstr.dst = std::monostate{};
    emit(callInstr);
    return std::monostate{};
  }

  tacky::Temporary dst = make_temp();
  callInstr.dst = dst;

  emit(callInstr);
  return dst;
}

void IRGenerator::visitDelayStmt(const DelayStmt *stmt) {
  tacky::Val duration = visitExpression(stmt->duration.get());
  emit(tacky::Delay{duration, stmt->is_ms});
}
