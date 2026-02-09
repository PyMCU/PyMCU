#ifndef AST_H
#define AST_H

#include <memory>
#include <vector>
#include <string>
#include <optional>

enum class BinaryOp {
    Add, Sub, Mul, Div, Mod,        // Arithmetic
    Equal, NotEqual, Less, Greater, // Comparison
    LessEq, GreaterEq,
    And, Or,                        // Logic (Short-circuit)
    BitAnd, BitOr, BitXor,          // Bitwise
    LShift, RShift
};

enum class UnaryOp {
    Negate, // -x
    Not,    // not x
    BitNot  // ~x
};

struct ASTNode {
    virtual ~ASTNode() = default;
};

struct Statement : ASTNode {};
struct Expression : ASTNode {};


struct IntegerLiteral : Expression {
    int value;
    explicit IntegerLiteral(int v) : value(v) {}
};

struct BooleanLiteral : Expression {
    bool value;
    explicit BooleanLiteral(bool v) : value(v) {}
};

struct StringLiteral : Expression {
    std::string value;
    explicit StringLiteral(std::string v) : value(std::move(v)) {}
};

struct VariableExpr : Expression {
    std::string name;
    explicit VariableExpr(std::string n) : name(std::move(n)) {}
};

struct IndexExpr : Expression {
    std::unique_ptr<Expression> target;
    std::unique_ptr<Expression> index;
    IndexExpr(std::unique_ptr<Expression> t, std::unique_ptr<Expression> i)
        : target(std::move(t)), index(std::move(i)) {}
};

struct CallExpr : Expression {
    std::string callee;
    std::vector<std::unique_ptr<Expression>> args;
    CallExpr(std::string name, std::vector<std::unique_ptr<Expression>> arguments)
        : callee(std::move(name)), args(std::move(arguments)) {}
};

struct BinaryExpr : Expression {
    std::unique_ptr<Expression> left;
    BinaryOp op;
    std::unique_ptr<Expression> right;
    BinaryExpr(std::unique_ptr<Expression> l, BinaryOp o, std::unique_ptr<Expression> r)
        : left(std::move(l)), op(o), right(std::move(r)) {}
};

struct UnaryExpr : Expression {
    UnaryOp op;
    std::unique_ptr<Expression> operand;
    UnaryExpr(UnaryOp o, std::unique_ptr<Expression> e)
        : op(o), operand(std::move(e)) {}
};

struct Block : Statement {
    std::vector<std::unique_ptr<Statement>> statements;
};

struct VarDecl : Statement {
    std::string name;
    std::string type;
    std::unique_ptr<Expression> initializer;
    VarDecl(std::string n, std::string t, std::unique_ptr<Expression> init = nullptr)
        : name(std::move(n)), type(std::move(t)), initializer(std::move(init)) {}
};

struct AssignStmt : Statement {
    std::unique_ptr<Expression> target;
    std::unique_ptr<Expression> value;
    AssignStmt(std::unique_ptr<Expression> t, std::unique_ptr<Expression> v)
        : target(std::move(t)), value(std::move(v)) {}
};

struct ReturnStmt : Statement {
    std::unique_ptr<Expression> value;
    explicit ReturnStmt(std::unique_ptr<Expression> v = nullptr) : value(std::move(v)) {}
};

struct IfStmt : Statement {
    std::unique_ptr<Expression> condition;
    std::unique_ptr<Statement> then_branch;
    std::unique_ptr<Statement> else_branch;
    IfStmt(std::unique_ptr<Expression> cond, std::unique_ptr<Statement> then_b, std::unique_ptr<Statement> else_b = nullptr)
        : condition(std::move(cond)), then_branch(std::move(then_b)), else_branch(std::move(else_b)) {}
};

struct WhileStmt : Statement {
    std::unique_ptr<Expression> condition;
    std::unique_ptr<Statement> body;
    WhileStmt(std::unique_ptr<Expression> cond, std::unique_ptr<Statement> b)
        : condition(std::move(cond)), body(std::move(b)) {}
};

struct ExprStmt : Statement {
    std::unique_ptr<Expression> expr;
    explicit ExprStmt(std::unique_ptr<Expression> e) : expr(std::move(e)) {}
};

struct BreakStmt : Statement {};
struct ContinueStmt : Statement {};
struct PassStmt : Statement {};

struct Param {
    std::string name;
    std::string type;
};

struct FunctionDef : ASTNode {
    std::string name;
    std::vector<Param> params;
    std::string return_type; // "void", "uint8"
    std::unique_ptr<Block> body;

    FunctionDef(std::string n, std::vector<Param> p, std::string ret, std::unique_ptr<Block> b)
        : name(std::move(n)), params(std::move(p)), return_type(std::move(ret)), body(std::move(b)) {}
};

struct Program : ASTNode {
    std::vector<std::unique_ptr<FunctionDef>> functions;
    std::vector<std::unique_ptr<Statement>> global_statements;
    // In the future, here would be global variables as well
};

#endif // AST_H