#include "IRGenerator.h"
#include <stdexcept>
#include <format>

tacky::Temporary IRGenerator::make_temp() {
    return tacky::Temporary{ std::format("tmp.{}", temp_counter++) };
}

tacky::Program IRGenerator::generate(const Program& ast) {
    tacky::Program ir_program;

    temp_counter = 0;

    for (const auto& func_def : ast.functions) {
        ir_program.functions.push_back(visitFunction(func_def.get()));
    }

    return ir_program;
}

tacky::Function IRGenerator::visitFunction(const FunctionDef* funcNode) {
    tacky::Function ir_func;
    ir_func.name = funcNode->name;

    current_instructions.clear();

    visitBlock(funcNode->body);

    if (current_instructions.empty() ||
        !std::holds_alternative<tacky::Return>(current_instructions.back())) {

        current_instructions.push_back(tacky::Return{tacky::Constant{0}});
    }

    ir_func.body = current_instructions;
    return ir_func;
}

void IRGenerator::visitBlock(const std::vector<std::unique_ptr<Statement>>& block) {
    for (const auto& stmt : block) {
        visitStatement(stmt.get());
    }
}

void IRGenerator::visitStatement(const Statement* stmt) {
    if (auto* retStmt = dynamic_cast<const ReturnStmt*>(stmt)) {
        tacky::Val val;

        if (retStmt->value) {
            val = visitExpression(retStmt->value.get());
        } else {
            val = tacky::Constant{0};
        }

        current_instructions.push_back(tacky::Return{val});
        return;
    }

    // B. Future: IfStmt, WhileStmt, VarDeclStmt...
    // if (auto* ifStmt = dynamic_cast<const IfStmt*>(stmt)) { ... }

    throw std::runtime_error("IR Generation: Unknown or unimplemented Statement type");
}

tacky::Val IRGenerator::visitExpression(const Expression* expr) {
    // A. Case: Number Literal
    if (auto* numExpr = dynamic_cast<const NumberExpr*>(expr)) {
        return tacky::Constant{numExpr->value};
    }

    // B. Future: Binary Operations (Addition, Subtraction)
    /*
    if (auto* binExpr = dynamic_cast<const BinaryExpr*>(expr)) {
        tacky::Val v1 = visitExpression(binExpr->left.get());
        tacky::Val v2 = visitExpression(binExpr->right.get());
        tacky::Temporary dst = make_temp();

        // Map the AST operator to TACKY operator
        tacky::BinaryOp op;
        // switch(binExpr->op) ...

        current_instructions.push_back(tacky::Binary{
            op, v1, v2, dst
        });
        return dst; // Return the temporary where the result is stored
    }
    */

    throw std::runtime_error("IR Generation: Unknown or unimplemented Expression type");
}