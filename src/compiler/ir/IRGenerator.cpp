#include "IRGenerator.h"
#include <format>
#include <iostream>
#include <stdexcept>
#include <typeinfo>

tacky::Temporary IRGenerator::make_temp(DataType type) {
  return tacky::Temporary{std::format("tmp.{}", temp_counter++), type};
}

std::string IRGenerator::make_label() {
  return std::format("L.{}", label_counter++);
}

void IRGenerator::emit(const tacky::Instruction &inst) {
  current_instructions.push_back(inst);
}

tacky::Program
IRGenerator::generate(const Program &main_ast,
                      const std::vector<const Program *> &imported_modules) {
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
      return tacky::MemoryAddress{it->second.value};
    } else {
      return tacky::Constant{it->second.value};
    }
  }

  // Mutable globals use un-prefixed names so they're shared across functions
  if (mutable_globals.contains(name)) {
    return tacky::Variable{name, mutable_globals.at(name)};
  }

  std::string local_name = current_function + "." + name;
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
      type = varDecl->type;
      initializer = varDecl->initializer.get();
    } else if (const auto assign =
                   dynamic_cast<const AssignStmt *>(stmt.get())) {
      if (const auto varExpr =
              dynamic_cast<const VariableExpr *>(assign->target.get())) {
        name = varExpr->name;
        initializer = assign->value.get();
      }
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
          globals[name] = SymbolInfo{true, val};
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

void IRGenerator::visitIf(const IfStmt *stmt) {
  // Generate If Stmt Logic
  // 1. Condition
  // 2. JumpIfZero -> Next Branch (elif_1)
  // 3. Then Block
  // 4. Jump -> End
  // 5. Label: elif_1
  // 6. Elif Condition
  // 7. JumpIfZero -> Next Branch (elif_2 or else)
  // 8. Elif Block
  // 9. Jump -> End
  // ...
  // N. Label: else
  // N+1. Else Block
  // N+2. Label: End

  std::string end_label = make_label();

  // 1. Main If Condition
  tacky::Val cond_val = visitExpression(stmt->condition.get());
  std::string next_label = (stmt->elif_branches.empty() && !stmt->else_branch)
                               ? end_label
                               : make_label();

  emit(tacky::JumpIfZero{cond_val, next_label});
  visitStatement(stmt->then_branch.get());
  emit(tacky::Jump{end_label});

  // 2. Elif Branches
  for (size_t i = 0; i < stmt->elif_branches.size(); ++i) {
    emit(tacky::Label{next_label});

    // Determine label for *next* branch (or else, or end)
    bool is_last_elif = (i == stmt->elif_branches.size() - 1);
    next_label =
        (is_last_elif && !stmt->else_branch) ? end_label : make_label();

    const auto &[elif_cond, elif_block] = stmt->elif_branches[i];
    tacky::Val elif_val = visitExpression(elif_cond.get());

    emit(tacky::JumpIfZero{elif_val, next_label});
    visitStatement(elif_block.get());
    emit(tacky::Jump{end_label});
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

  // Optimization: detect bit-polling pattern
  // Pattern: while reg[bit] == 0/1  or  while reg[bit] != 0/1
  if (auto *cmp = dynamic_cast<const BinaryExpr *>(stmt->condition.get())) {
    if (cmp->op == BinaryOp::Equal || cmp->op == BinaryOp::NotEqual) {
      const IndexExpr *idx = nullptr;
      const IntegerLiteral *lit = nullptr;

      // Check: IndexExpr on left, literal on right
      idx = dynamic_cast<const IndexExpr *>(cmp->left.get());
      lit = dynamic_cast<const IntegerLiteral *>(cmp->right.get());

      // Or reversed: literal on left, IndexExpr on right
      if (!idx || !lit) {
        idx = dynamic_cast<const IndexExpr *>(cmp->right.get());
        lit = dynamic_cast<const IntegerLiteral *>(cmp->left.get());
      }

      if (idx && lit && (lit->value == 0 || lit->value == 1)) {
        // Resolve the register and bit index
        tacky::Val target = visitExpression(idx->target.get());
        tacky::Val indexVal = visitExpression(idx->index.get());

        int bit = 0;
        if (auto c = std::get_if<tacky::Constant>(&indexVal)) {
          bit = c->value;
        }

        // Determine: when should we EXIT the loop?
        // while (bit == 1): exit when bit is 0  → JumpIfBitClear
        // while (bit == 0): exit when bit is 1  → JumpIfBitSet
        // while (bit != 0): exit when bit is 0  → JumpIfBitClear
        // while (bit != 1): exit when bit is 1  → JumpIfBitSet
        bool exit_when_set;
        if (cmp->op == BinaryOp::Equal) {
          exit_when_set = (lit->value == 0); // == 0 → exit when set
        } else {
          // NotEqual
          exit_when_set = (lit->value == 1); // != 1 → exit when set
        }

        if (exit_when_set) {
          emit(tacky::JumpIfBitSet{target, bit, end_label});
        } else {
          emit(tacky::JumpIfBitClear{target, bit, end_label});
        }

        visitStatement(stmt->body.get());
        emit(tacky::Jump{start_label});
        emit(tacky::Label{end_label});
        loop_stack.pop_back();
        return;
      }
    }
  }

  // Generic path: evaluate condition normally
  const tacky::Val cond = visitExpression(stmt->condition.get());
  emit(tacky::JumpIfZero{cond, end_label});

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
  if (auto unaryExpr = dynamic_cast<const UnaryExpr *>(stmt->target.get())) {
    if (unaryExpr->op == UnaryOp::Deref) {
      tacky::Val addrVal = visitExpression(unaryExpr->operand.get());
      if (auto c = std::get_if<tacky::Constant>(&addrVal)) {
        tacky::Val val = visitExpression(stmt->value.get());
        emit(tacky::Copy{val, tacky::MemoryAddress{c->value}});
        return;
      }
      throw std::runtime_error(
          "Assignment to non-constant address not supported");
    }
  }

  tacky::Val value = visitExpression(stmt->value.get());

  if (auto varExpr = dynamic_cast<const VariableExpr *>(stmt->target.get())) {
    tacky::Val target = resolve_binding(varExpr->name);
    emit(tacky::Copy{value, target});
  } else {
    throw std::runtime_error("Invalid assignment target");
  }
}

void IRGenerator::visitVarDecl(const VarDecl *stmt) {
  // Track variable type
  DataType dt = resolve_type(stmt->type);
  std::string var_key = mutable_globals.count(stmt->name)
                            ? stmt->name
                            : current_function + "." + stmt->name;
  variable_types[var_key] = dt;

  if (stmt->initializer) {
    tacky::Val val = visitExpression(stmt->initializer.get());
    tacky::Val target = resolve_binding(stmt->name);
    // Set type on the target variable
    if (auto v = std::get_if<tacky::Variable>(&target)) {
      v->type = dt;
    }
    emit(tacky::Copy{val, target});
  }
}

DataType IRGenerator::resolve_type(const std::string &type_str) {
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

tacky::Val IRGenerator::visitCall(const CallExpr *expr) {
  // Inlining Support
  if (inline_functions.contains(expr->callee)) {
    const FunctionDef *func = inline_functions.at(expr->callee);
    std::string exit_label = make_label();
    std::optional<tacky::Temporary> result;

    if (func->return_type != "void" && func->return_type != "None") {
      result = make_temp(resolve_type(func->return_type));
    }

    inline_stack.push_back({exit_label, result});

    // Argument binding
    for (size_t i = 0; i < expr->args.size(); ++i) {
      tacky::Val argVal = visitExpression(expr->args[i].get());
      tacky::Val param = resolve_binding(func->params[i].name);
      emit(tacky::Copy{argVal, param});
    }

    visitBlock(func->body.get());

    emit(tacky::Label{exit_label});
    inline_stack.pop_back();

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
