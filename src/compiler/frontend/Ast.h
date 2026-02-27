/*
 * -----------------------------------------------------------------------------
 * PyMCU Compiler (pymcuc)
 * Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
 *
 * This file is part of the PyMCU Development Ecosystem.
 *
 * PyMCU is free software: you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * PyMCU is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with PyMCU.  If not, see <https://www.gnu.org/licenses/>.
 *
 * -----------------------------------------------------------------------------
 * SAFETY WARNING / HIGH RISK ACTIVITIES:
 * THE SOFTWARE IS NOT DESIGNED, MANUFACTURED, OR INTENDED FOR USE IN HAZARDOUS
 * ENVIRONMENTS REQUIRING FAIL-SAFE PERFORMANCE, SUCH AS IN THE OPERATION OF
 * NUCLEAR FACILITIES, AIRCRAFT NAVIGATION OR COMMUNICATION SYSTEMS, AIR
 * TRAFFIC CONTROL, DIRECT LIFE SUPPORT MACHINES, OR WEAPONS SYSTEMS.
 * -----------------------------------------------------------------------------
 */

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
  FloorDiv,
  Mod,  // Arithmetic
  Equal,
  NotEqual,
  Less,
  Greater,  // Comparison
  LessEq,
  GreaterEq,
  And,
  Or,  // Logic (Short-circuit)
  BitAnd,
  BitOr,
  BitXor,  // Bitwise
  LShift,
  RShift
};

enum class UnaryOp {
  Negate,  // -x
  Not,     // not x
  BitNot,  // ~x
  Deref    // *ptr (pointer dereference)
};

struct ASTNode {
  virtual ~ASTNode() = default;

  int line = 0;
};

struct Statement : public ASTNode {};

struct ClassDef : public Statement {
  std::string name;
  std::vector<std::string> bases;
  std::unique_ptr<Statement> body;  // Block
  bool is_static = false;
  bool is_dataclass = false;

  ClassDef(std::string n, std::vector<std::string> b,
           std::unique_ptr<Statement> body_stmt)
      : name(std::move(n)), bases(std::move(b)), body(std::move(body_stmt)) {}
};

struct Expression : ASTNode {};

struct IntegerLiteral : Expression {
  int value;

  explicit IntegerLiteral(int v) : value(v) {}
};

struct FloatLiteral : Expression {
  double value;

  explicit FloatLiteral(double v) : value(v) {}
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

struct MemberAccessExpr : Expression {
  std::unique_ptr<Expression> object;
  std::string member;

  MemberAccessExpr(std::unique_ptr<Expression> obj, std::string mem)
      : object(std::move(obj)), member(std::move(mem)) {}
};

struct CallExpr : public Expression {
  std::unique_ptr<Expression> callee;
  std::vector<std::unique_ptr<Expression>> args;

  CallExpr(std::unique_ptr<Expression> target,
           std::vector<std::unique_ptr<Expression>> arguments)
      : callee(std::move(target)), args(std::move(arguments)) {}
};

struct KeywordArgExpr : Expression {
  std::string key;
  std::unique_ptr<Expression> value;

  KeywordArgExpr(std::string k, std::unique_ptr<Expression> v)
      : key(std::move(k)), value(std::move(v)) {}
};

struct BinaryExpr : public Expression {
  std::unique_ptr<Expression> left;
  BinaryOp op;
  std::unique_ptr<Expression> right;

  BinaryExpr(std::unique_ptr<Expression> l, BinaryOp o,
             std::unique_ptr<Expression> r)
      : left(std::move(l)), op(o), right(std::move(r)) {}
};

struct YieldExpr : public Expression {
  std::unique_ptr<Expression> value;  // Optional in Python, but we can enforce
                                      // strictness or wrap in optional
  explicit YieldExpr(std::unique_ptr<Expression> v) : value(std::move(v)) {}
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
  std::string var_type;  // "uint8", "ptr", etc.
  std::unique_ptr<Expression> init;

  VarDecl(std::string n, std::string t, std::unique_ptr<Expression> i)
      : name(std::move(n)), var_type(std::move(t)), init(std::move(i)) {}
};

// Annotated assignment: name: type = value
struct AnnAssign : Statement {
  std::string target;                 // Variable name
  std::string annotation;             // Type string (e.g., "ptr[uint16]")
  std::unique_ptr<Expression> value;  // Initializer

  AnnAssign(std::string t, std::string ann, std::unique_ptr<Expression> v)
      : target(std::move(t)), annotation(std::move(ann)), value(std::move(v)) {}
};

struct AssignStmt : Statement {
  std::unique_ptr<Expression> target;
  std::unique_ptr<Expression> value;

  AssignStmt(std::unique_ptr<Expression> t, std::unique_ptr<Expression> v)
      : target(std::move(t)), value(std::move(v)) {}
};

enum class AugOp {
  Add,
  Sub,
  Mul,
  Div,
  FloorDiv,
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
      : target(std::move(t)), op(o), value(std::move(v)) {}
};

struct ReturnStmt : Statement {
  std::unique_ptr<Expression> value;

  explicit ReturnStmt(std::unique_ptr<Expression> v = nullptr)
      : value(std::move(v)) {}
};

struct IfStmt : Statement {
  std::unique_ptr<Expression> condition;
  std::unique_ptr<Statement> then_branch;
  // Expanded for elif support: list of (condition, block)
  std::vector<
      std::pair<std::unique_ptr<Expression>, std::unique_ptr<Statement>>>
      elif_branches;
  std::unique_ptr<Statement> else_branch;

  IfStmt(std::unique_ptr<Expression> cond, std::unique_ptr<Statement> then_b,
         std::vector<
             std::pair<std::unique_ptr<Expression>, std::unique_ptr<Statement>>>
             elif_b = {},
         std::unique_ptr<Statement> else_b = nullptr)
      : condition(std::move(cond)),
        then_branch(std::move(then_b)),
        elif_branches(std::move(elif_b)),
        else_branch(std::move(else_b)) {}
};

struct CaseBranch {
  std::unique_ptr<Expression> pattern;  // nullptr if it's the wildcard case (_)
  std::unique_ptr<Statement> body;
};

struct MatchStmt : Statement {
  std::unique_ptr<Expression> target;
  std::vector<CaseBranch> branches;

  MatchStmt(std::unique_ptr<Expression> t, std::vector<CaseBranch> b)
      : target(std::move(t)), branches(std::move(b)) {}
};

struct WhileStmt : Statement {
  std::unique_ptr<Expression> condition;
  std::unique_ptr<Statement> body;

  WhileStmt(std::unique_ptr<Expression> cond, std::unique_ptr<Statement> b)
      : condition(std::move(cond)), body(std::move(b)) {}
};

struct ForStmt : Statement {
  std::string var_name;
  std::unique_ptr<Expression> range_start;  // nullptr → 0 (range-based)
  std::unique_ptr<Expression> range_stop;   // nullptr → iterable-based
  std::unique_ptr<Expression> range_step;   // nullptr → 1
  std::unique_ptr<Expression> iterable;     // non-null → for-in over iterable
  std::unique_ptr<Statement> body;

  // Range-based: for var in range(...)
  ForStmt(std::string var, std::unique_ptr<Expression> start,
          std::unique_ptr<Expression> stop, std::unique_ptr<Expression> step,
          std::unique_ptr<Statement> b)
      : var_name(std::move(var)), range_start(std::move(start)),
        range_stop(std::move(stop)), range_step(std::move(step)),
        body(std::move(b)) {}

  // Iterable-based: for var in expr (compile-time unrolled)
  ForStmt(std::string var, std::unique_ptr<Expression> iter,
          std::unique_ptr<Statement> b)
      : var_name(std::move(var)), iterable(std::move(iter)),
        body(std::move(b)) {}
};

struct ExprStmt : Statement {
  std::unique_ptr<Expression> expr;

  explicit ExprStmt(std::unique_ptr<Expression> e) : expr(std::move(e)) {}
};

struct GlobalStmt : Statement {
  std::vector<std::string> names;

  explicit GlobalStmt(std::vector<std::string> n) : names(std::move(n)) {}
};

struct BreakStmt : Statement {};

struct ContinueStmt : Statement {};

struct PassStmt : Statement {};

struct RaiseStmt : Statement {
  std::string error_type;
  std::string message;

  RaiseStmt(std::string type, std::string msg)
      : error_type(std::move(type)), message(std::move(msg)) {}
};

struct Param {
  std::string name;
  std::string type;
  std::unique_ptr<Expression> default_value;

  Param(std::string n, std::string t, std::unique_ptr<Expression> def = nullptr)
      : name(std::move(n)), type(std::move(t)), default_value(std::move(def)) {}
};

struct FunctionDef : Statement {
  std::string name;
  std::vector<Param> params;
  std::string return_type;  // "void", "uint8"
  std::unique_ptr<Block> body;
  bool is_inline;
  bool is_interrupt;
  int interrupt_vector;

  FunctionDef(std::string n, std::vector<Param> p, std::string ret,
              std::unique_ptr<Block> b, bool inl = false, bool is_int = false,
              int vector = 0)
      : name(std::move(n)),
        params(std::move(p)),
        return_type(std::move(ret)),
        body(std::move(b)),
        is_inline(inl),
        is_interrupt(is_int),
        interrupt_vector(vector) {}
};

struct ImportStmt : Statement {
  std::string module_name;
  std::vector<std::string> symbols;
  int relative_level;

  ImportStmt(std::string mod, std::vector<std::string> syms, int level = 0)
      : module_name(std::move(mod)),
        symbols(std::move(syms)),
        relative_level(level) {}
};

struct Program : ASTNode {
  std::vector<std::unique_ptr<ImportStmt>> imports;
  std::vector<std::unique_ptr<FunctionDef>> functions;
  std::vector<std::unique_ptr<Statement>> global_statements;
};

#endif  // AST_H