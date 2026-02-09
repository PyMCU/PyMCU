#ifndef IRGENERATOR_H
#define IRGENERATOR_H

#pragma once
#include "Tacky.h"
#include "../frontend/Ast.h"
#include <vector>

class IRGenerator {
public:
    tacky::Program generate(const Program& ast);
private:
    std::vector<tacky::Instruction> current_instructions;
    int temp_counter = 0;

    // --- Helpers ---
    tacky::Temporary make_temp();

    // --- Visitor Methods ---
    tacky::Function visitFunction(const FunctionDef* funcNode);
    void visitBlock(const std::vector<std::unique_ptr<Statement>>& block);
    void visitStatement(const Statement* stmt);

    // The expressions return a Value (Val) to be used by others
    tacky::Val visitExpression(const Expression* expr);
};

#endif // IRGENERATOR_H