#ifndef AST_H
#define AST_H
#include <memory>
#include <vector>

struct ASTNode { virtual ~ASTNode() = default; };

struct Expression : ASTNode {};

struct Statement : ASTNode {};

struct NumberExpr : Expression {
    int value;
    explicit NumberExpr(const int v) : value(v) {}
};

struct ReturnStmt : Statement {
    std::unique_ptr<Expression> value; // For now only returns numbers
    explicit ReturnStmt(std::unique_ptr<Expression> v) : value(std::move(v)) {}
};

struct FunctionDef : ASTNode {
    std::string name;
    std::vector<std::unique_ptr<Statement>> body;
};

struct Program : ASTNode {
    std::vector<std::unique_ptr<FunctionDef>> functions;
};

#endif