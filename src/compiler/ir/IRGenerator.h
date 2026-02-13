#ifndef IRGENERATOR_H
#define IRGENERATOR_H

#pragma once
#include "../frontend/Ast.h"
#include "Tacky.h"
#include <map>
#include <set>
#include <string>
#include <vector>

struct SymbolInfo {
    bool is_memory_address;
    int value;
};

class IRGenerator {
public:
    tacky::Program generate(const Program &main_ast,
                            const std::vector<const Program *> &imported_modules);

private:
    std::vector<tacky::Instruction> current_instructions;
    int temp_counter = 0;
    int label_counter = 0;
    std::map<std::string, SymbolInfo> globals;
    std::set<std::string> mutable_globals;
    std::map<std::string, std::string> function_return_types;
    std::map<std::string, std::vector<std::string> > function_params;
    std::string current_function;

    struct LoopLabels {
        std::string continue_label;
        std::string break_label;
    };

    std::vector<LoopLabels> loop_stack;

    tacky::Temporary make_temp();

    std::string make_label();

    void emit(const tacky::Instruction &inst);

    tacky::Val resolve_binding(const std::string &name);

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

    void visitVarDecl(const VarDecl *stmt);

    void visitExprStmt(const ExprStmt *stmt);

    tacky::Val visitExpression(const Expression *expr);

    tacky::Val visitBinary(const BinaryExpr *expr);

    tacky::Val visitUnary(const UnaryExpr *expr);

    static tacky::Val visitLiteral(const IntegerLiteral *expr);

    tacky::Val visitVariable(const VariableExpr *expr);

    tacky::Val visitCall(const CallExpr *expr);

    tacky::Val visitIndex(const IndexExpr *expr);

    static int evaluate_constant_expr(const Expression *expr);
};

#endif // IRGENERATOR_H
