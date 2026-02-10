#include "IRGenerator.h"
#include <stdexcept>
#include <format>
#include <iostream>

tacky::Temporary IRGenerator::make_temp() {
    return tacky::Temporary{ std::format("tmp.{}", temp_counter++) };
}

std::string IRGenerator::make_label() {
    return std::format("L.{}", label_counter++);
}

void IRGenerator::emit(const tacky::Instruction &inst) {
    current_instructions.push_back(inst);
}

tacky::Program IRGenerator::generate(const Program& main_ast, const std::vector<const Program*>& imported_modules) {
    tacky::Program ir_program;
    globals.clear();
    function_return_types.clear();

    for (const auto* mod : imported_modules) {
        scan_globals(*mod);
        scan_functions(*mod);
    }

    scan_globals(main_ast);
    scan_functions(main_ast);

    for (const auto& func_def : main_ast.functions) {
        ir_program.functions.push_back(visitFunction(func_def.get()));
    }

    return ir_program;
}

tacky::Val IRGenerator::resolve_binding(const std::string& name) {
    if (const auto it = globals.find(name); it != globals.end()) {
        if (it->second.is_memory_address) {
            return tacky::MemoryAddress{it->second.value};
        } else {
            return tacky::Constant{it->second.value};
        }
    }

    return tacky::Variable{name};
}

void IRGenerator::scan_globals(const Program& ast) {

    for (const auto& stmt : ast.global_statements) {
        if (const auto varDecl = dynamic_cast<const VarDecl*>(stmt.get())) {
            if (varDecl->initializer) {
                try {
                    const int val = evaluate_constant_expr(varDecl->initializer.get());
                    const bool is_ptr = varDecl->type.find("ptr") != std::string::npos;

                    globals[varDecl->name] = SymbolInfo{is_ptr, val};
                } catch (...) {
                }
            }
        }
    }
}

void IRGenerator::scan_functions(const Program& ast) {
    for (const auto& func : ast.functions) {
        function_return_types[func->name] = func->return_type;
    }
}

int IRGenerator::evaluate_constant_expr(const Expression* expr) {
    if (const auto num = dynamic_cast<const IntegerLiteral*>(expr)) {
        return num->value;
    }

    if (const auto call = dynamic_cast<const CallExpr*>(expr)) {
        if (call->callee == "ptr" && call->args.size() == 1) {
            return evaluate_constant_expr(call->args[0].get());
        }
    }

    throw std::runtime_error("Not a constant expression");
}

tacky::Function IRGenerator::visitFunction(const FunctionDef* funcNode) {
    tacky::Function ir_func;
    ir_func.name = funcNode->name;

    current_instructions.clear();

    for(const auto&[name, type] : funcNode->params) {
        ir_func.params.push_back(name);
    }

    visitBlock(funcNode->body.get());

    if (current_instructions.empty() ||
        !std::holds_alternative<tacky::Return>(current_instructions.back())) {
        emit(tacky::Return{std::monostate{}});
    }

    ir_func.body = current_instructions;
    return ir_func;
}

void IRGenerator::visitBlock(const Block* block) {
    for (const auto& stmt : block->statements) {
        visitStatement(stmt.get());
    }
}

void IRGenerator::visitStatement(const Statement* stmt) {
    if (auto* block = dynamic_cast<const Block*>(stmt)) return visitBlock(block);
    if (auto* ret = dynamic_cast<const ReturnStmt*>(stmt)) return visitReturn(ret);
    if (auto* ifStmt = dynamic_cast<const IfStmt*>(stmt)) return visitIf(ifStmt);
    if (auto* whileStmt = dynamic_cast<const WhileStmt*>(stmt)) return visitWhile(whileStmt);
    if (auto* assign = dynamic_cast<const AssignStmt*>(stmt)) return visitAssign(assign);
    if (auto* decl = dynamic_cast<const VarDecl*>(stmt)) return visitVarDecl(decl);
    if (auto* exprStmt = dynamic_cast<const ExprStmt*>(stmt)) return visitExprStmt(exprStmt);

    if (dynamic_cast<const PassStmt*>(stmt)) return;

    throw std::runtime_error("IR Generation: Unknown Statement type");
}

void IRGenerator::visitReturn(const ReturnStmt* stmt) {
    tacky::Val val;
    if (stmt->value) {
        val = visitExpression(stmt->value.get());
    } else {
        val = std::monostate{};
    }
    emit(tacky::Return{val});
}

void IRGenerator::visitIf(const IfStmt* stmt) {
    const tacky::Val cond = visitExpression(stmt->condition.get());

    const std::string else_label = make_label();
    const std::string end_label = make_label();

    emit(tacky::JumpIfZero{cond, else_label});

    visitStatement(stmt->then_branch.get());
    emit(tacky::Jump{end_label});

    emit(tacky::Label{else_label});

    if (stmt->else_branch) {
        visitStatement(stmt->else_branch.get());
    }

    emit(tacky::Label{end_label});
}

void IRGenerator::visitWhile(const WhileStmt* stmt) {
    const std::string start_label = make_label();
    const std::string end_label = make_label();

    emit(tacky::Label{start_label});
    const tacky::Val cond = visitExpression(stmt->condition.get());
    emit(tacky::JumpIfZero{cond, end_label});

    visitStatement(stmt->body.get());

    emit(tacky::Jump{start_label});
    emit(tacky::Label{end_label});
}

void IRGenerator::visitAssign(const AssignStmt* stmt) {
    if (auto indexExpr = dynamic_cast<const IndexExpr*>(stmt->target.get())) {
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

    if (auto varExpr = dynamic_cast<const VariableExpr*>(stmt->target.get())) {
        tacky::Val target = resolve_binding(varExpr->name);
        emit(tacky::Copy{value, target});
    } else {
        throw std::runtime_error("Invalid assignment target");
    }
}

void IRGenerator::visitVarDecl(const VarDecl* stmt) {
    if (stmt->initializer) {
        tacky::Val val = visitExpression(stmt->initializer.get());
        tacky::Val target = resolve_binding(stmt->name);
        emit(tacky::Copy{val, target});
    }
}

void IRGenerator::visitExprStmt(const ExprStmt* stmt) {
    visitExpression(stmt->expr.get());
}

tacky::Val IRGenerator::visitExpression(const Expression* expr) {
    if (auto* bin = dynamic_cast<const BinaryExpr*>(expr)) return visitBinary(bin);
    if (auto* un = dynamic_cast<const UnaryExpr*>(expr)) return visitUnary(un);
    if (auto* num = dynamic_cast<const IntegerLiteral*>(expr)) return visitLiteral(num);
    if (auto* var = dynamic_cast<const VariableExpr*>(expr)) return visitVariable(var);
    if (auto* call = dynamic_cast<const CallExpr*>(expr)) return visitCall(call);
    if (auto* idx = dynamic_cast<const IndexExpr*>(expr)) return visitIndex(idx);

    if (auto* boolean = dynamic_cast<const BooleanLiteral*>(expr)) {
        return tacky::Constant{boolean->value ? 1 : 0};
    }

    throw std::runtime_error("IR Generation: Unknown Expression type");
}

tacky::Val IRGenerator::visitLiteral(const IntegerLiteral* expr) {
    return tacky::Constant{expr->value};
}

tacky::Val IRGenerator::visitVariable(const VariableExpr* expr) {
    return resolve_binding(expr->name);
}

tacky::Val IRGenerator::visitBinary(const BinaryExpr* expr) {
    tacky::Val v1 = visitExpression(expr->left.get());
    tacky::Val v2 = visitExpression(expr->right.get());
    tacky::Temporary dst = make_temp();

    tacky::BinaryOp op;
    switch (expr->op) {
        case BinaryOp::Add: op = tacky::BinaryOp::Add; break;
        case BinaryOp::Sub: op = tacky::BinaryOp::Sub; break;
        case BinaryOp::Mul: op = tacky::BinaryOp::Mul; break;
        case BinaryOp::Div: op = tacky::BinaryOp::Div; break;
        case BinaryOp::Mod: op = tacky::BinaryOp::Mod; break;
        case BinaryOp::Equal: op = tacky::BinaryOp::Equal; break;
        case BinaryOp::NotEqual: op = tacky::BinaryOp::NotEqual; break;
        case BinaryOp::Less: op = tacky::BinaryOp::LessThan; break;
        case BinaryOp::LessEq: op = tacky::BinaryOp::LessEqual; break;
        case BinaryOp::Greater: op = tacky::BinaryOp::GreaterThan; break;
        case BinaryOp::GreaterEq: op = tacky::BinaryOp::GreaterEqual; break;

        case BinaryOp::BitAnd: op = tacky::BinaryOp::BitAnd; break;
        case BinaryOp::BitOr:  op = tacky::BinaryOp::BitOr; break;
        case BinaryOp::BitXor: op = tacky::BinaryOp::BitXor; break;
        case BinaryOp::LShift: op = tacky::BinaryOp::LShift; break;
        case BinaryOp::RShift: op = tacky::BinaryOp::RShift; break;
        default: throw std::runtime_error("Unsupported Binary Op");
    }

    emit(tacky::Binary{op, v1, v2, dst});
    return dst;
}

tacky::Val IRGenerator::visitUnary(const UnaryExpr* expr) {
    const tacky::Val src = visitExpression(expr->operand.get());
    tacky::Temporary dst = make_temp();

    tacky::UnaryOp op;
    switch (expr->op) {
        case UnaryOp::Negate: op = tacky::UnaryOp::Neg; break;
        case UnaryOp::Not:    op = tacky::UnaryOp::Not; break;
        case UnaryOp::BitNot: op = tacky::UnaryOp::BitNot; break;
        default: throw std::runtime_error("Unsupported Unary Op");
    }

    emit(tacky::Unary{op, src, dst});
    return dst;
}

tacky::Val IRGenerator::visitIndex(const IndexExpr* expr) {
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

tacky::Val IRGenerator::visitCall(const CallExpr* expr) {
    tacky::Call callInstr;
    callInstr.function_name = expr->callee;

    for (const auto& arg : expr->args) {
        callInstr.args.push_back(visitExpression(arg.get()));
    }

    if (function_return_types.contains(expr->callee) && function_return_types[expr->callee] == "void") {
        callInstr.dst = std::monostate{};
        emit(callInstr);
        return std::monostate{};
    }

    tacky::Temporary dst = make_temp();
    callInstr.dst = dst;

    emit(callInstr);
    return dst;
}