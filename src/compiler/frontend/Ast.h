#ifndef AST_H
#define AST_H

#include <memory>
#include <optional>
#include <string>
#include <vector>

enum class BinaryOp {
  Add,
  Sub,
  Mul,
  Div,
  Mod, // Arithmetic
  Equal,
  NotEqual,
  Less,
  Greater, // Comparison
  LessEq,
  GreaterEq,
  And,
  Or, // Logic (Short-circuit)
  BitAnd,
  BitOr,
  BitXor, // Bitwise
  LShift,
  RShift
};

enum class UnaryOp {
  Negate, // -x
  Not, // not x
  BitNot // ~x
};

struct ASTNode {
  virtual ~ASTNode() = default;

  int line = 0;
};

struct Statement : ASTNode {
};

struct Expression : ASTNode {
};

struct IntegerLiteral : Expression {
  int value;

  explicit IntegerLiteral(int v) : value(v) {
  }
};

struct FloatLiteral : Expression {
  double value;

  explicit FloatLiteral(double v) : value(v) {
  }
};

struct BooleanLiteral : Expression {
  bool value;

  explicit BooleanLiteral(bool v) : value(v) {
  }
};

struct StringLiteral : Expression {
  std::string value;

  explicit StringLiteral(std::string v) : value(std::move(v)) {
  }
};

struct VariableExpr : Expression {
  std::string name;

  explicit VariableExpr(std::string n) : name(std::move(n)) {
  }
};

struct IndexExpr : Expression {
  std::unique_ptr<Expression> target;
  std::unique_ptr<Expression> index;

  IndexExpr(std::unique_ptr<Expression> t, std::unique_ptr<Expression> i)
    : target(std::move(t)), index(std::move(i)) {
  }
};

struct MemberAccessExpr : Expression {
  std::unique_ptr<Expression> object;
  std::string member;

  MemberAccessExpr(std::unique_ptr<Expression> obj, std::string mem)
    : object(std::move(obj)), member(std::move(mem)) {
  }
};

struct CallExpr : Expression {
  std::string callee;
  std::vector<std::unique_ptr<Expression> > args;

  CallExpr(std::string name, std::vector<std::unique_ptr<Expression> > arguments)
    : callee(std::move(name)), args(std::move(arguments)) {
  }
};

struct BinaryExpr : Expression {
  std::unique_ptr<Expression> left;
  BinaryOp op;
  std::unique_ptr<Expression> right;

  BinaryExpr(std::unique_ptr<Expression> l, BinaryOp o,
             std::unique_ptr<Expression> r)
    : left(std::move(l)), op(o), right(std::move(r)) {
  }
};

struct UnaryExpr : Expression {
  UnaryOp op;
  std::unique_ptr<Expression> operand;

  UnaryExpr(UnaryOp o, std::unique_ptr<Expression> e)
    : op(o), operand(std::move(e)) {
  }
};

struct Block : Statement {
  std::vector<std::unique_ptr<Statement> > statements;
};

struct VarDecl : Statement {
  std::string name;
  std::string var_type; // "uint8", "ptr", etc.
  std::unique_ptr<Expression> init;

  VarDecl(std::string n, std::string t, std::unique_ptr<Expression> i)
    : name(std::move(n)), var_type(std::move(t)), init(std::move(i)) {
  }
};

// Annotated assignment: name: type = value
struct AnnAssign : Statement {
  std::string target; // Variable name
  std::string annotation; // Type string (e.g., "ptr[uint16]")
  std::unique_ptr<Expression> value; // Initializer

  AnnAssign(std::string t, std::string ann, std::unique_ptr<Expression> v)
    : target(std::move(t)), annotation(std::move(ann)), value(std::move(v)) {
  }
};

struct AssignStmt : Statement {
  std::unique_ptr<Expression> target;
  std::unique_ptr<Expression> value;

  AssignStmt(std::unique_ptr<Expression> t, std::unique_ptr<Expression> v)
    : target(std::move(t)), value(std::move(v)) {
  }
};

enum class AugOp {
  Add,
  Sub,
  Mul,
  Div,
  Mod,
  BitAnd,
  BitOr,
  BitXor,
  LShift,
  RShift
};

struct AugAssignStmt : Statement {
  std::unique_ptr<Expression> target;
  AugOp op;
  std::unique_ptr<Expression> value;

  AugAssignStmt(std::unique_ptr<Expression> t, AugOp o,
                std::unique_ptr<Expression> v)
    : target(std::move(t)), op(o), value(std::move(v)) {
  }
};

struct ReturnStmt : Statement {
  std::unique_ptr<Expression> value;

  explicit ReturnStmt(std::unique_ptr<Expression> v = nullptr)
    : value(std::move(v)) {
  }
};

struct IfStmt : Statement {
  std::unique_ptr<Expression> condition;
  std::unique_ptr<Statement> then_branch;
  // Expanded for elif support: list of (condition, block)
  std::vector<
    std::pair<std::unique_ptr<Expression>, std::unique_ptr<Statement> > >
  elif_branches;
  std::unique_ptr<Statement> else_branch;

  IfStmt(std::unique_ptr<Expression> cond, std::unique_ptr<Statement> then_b,
         std::vector<
           std::pair<std::unique_ptr<Expression>, std::unique_ptr<Statement> > >
         elif_b = {},
         std::unique_ptr<Statement> else_b = nullptr)
    : condition(std::move(cond)), then_branch(std::move(then_b)),
      elif_branches(std::move(elif_b)), else_branch(std::move(else_b)) {
  }
};

struct CaseBranch {
  std::unique_ptr<Expression> pattern; // nullptr if it's the wildcard case (_)
  std::unique_ptr<Statement> body;
};

struct MatchStmt : Statement {
  std::unique_ptr<Expression> target;
  std::vector<CaseBranch> branches;

  MatchStmt(std::unique_ptr<Expression> t, std::vector<CaseBranch> b)
    : target(std::move(t)), branches(std::move(b)) {
  }
};

struct WhileStmt : Statement {
  std::unique_ptr<Expression> condition;
  std::unique_ptr<Statement> body;

  WhileStmt(std::unique_ptr<Expression> cond, std::unique_ptr<Statement> b)
    : condition(std::move(cond)), body(std::move(b)) {
  }
};

struct ExprStmt : Statement {
  std::unique_ptr<Expression> expr;

  explicit ExprStmt(std::unique_ptr<Expression> e) : expr(std::move(e)) {
  }
};

struct GlobalStmt : Statement {
  std::vector<std::string> names;

  explicit GlobalStmt(std::vector<std::string> n) : names(std::move(n)) {
  }
};

struct BreakStmt : Statement {
};

struct ContinueStmt : Statement {
};

struct PassStmt : Statement {
};

struct DelayStmt : Statement {
  bool is_ms; // true = ms, false = us
  std::unique_ptr<Expression> duration;

  DelayStmt(bool ms, std::unique_ptr<Expression> dur)
    : is_ms(ms), duration(std::move(dur)) {
  }
};

struct Param {
  std::string name;
  std::string type;
};

struct FunctionDef : ASTNode {
  std::string name;
  std::vector<Param> params;
  std::string return_type; // "void", "uint8"
  std::unique_ptr<Block> body;
  bool is_inline;
  bool is_interrupt;
  int interrupt_vector;

  FunctionDef(std::string n, std::vector<Param> p, std::string ret,
              std::unique_ptr<Block> b, bool inl = false, bool is_int = false,
              int vector = 0)
    : name(std::move(n)), params(std::move(p)), return_type(std::move(ret)),
      body(std::move(b)), is_inline(inl), is_interrupt(is_int),
      interrupt_vector(vector) {
  }
};

struct ImportStmt : Statement {
  std::string module_name;
  std::vector<std::string> symbols;
  int relative_level;

  ImportStmt(std::string mod, std::vector<std::string> syms, int level = 0)
    : module_name(std::move(mod)), symbols(std::move(syms)),
      relative_level(level) {
  }
};

struct Program : ASTNode {
  std::vector<std::unique_ptr<ImportStmt> > imports;
  std::vector<std::unique_ptr<FunctionDef> > functions;
  std::vector<std::unique_ptr<Statement> > global_statements;
};

#endif // AST_H
