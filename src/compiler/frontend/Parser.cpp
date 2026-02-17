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

#include "Parser.h"

#include <algorithm>
#include <format>
#include <optional>
#include <stdexcept>

#include "../common/Errors.h"
#include "Ast.h"

Parser::Parser(const std::vector<Token> &tokens) : tokens(tokens), pos(0) {}

const Token &Parser::peek() const {
  if (pos >= tokens.size()) return tokens.back();
  return tokens[pos];
}

const Token &Parser::previous() const {
  if (pos == 0) return tokens[0];
  return tokens[pos - 1];
}

Token Parser::advance() {
  if (pos < tokens.size()) pos++;
  return tokens[pos - 1];
}

bool Parser::check(const TokenType type) const { return peek().type == type; }

bool Parser::match(const TokenType type) {
  if (check(type)) {
    advance();
    return true;
  }
  return false;
}

Token Parser::consume(const TokenType type,
                      const std::string_view errorMessage) {
  if (check(type)) return advance();
  error(errorMessage);
}

void Parser::consumeStatementEnd() {
  if (match(TokenType::Semicolon)) {
    match(TokenType::Newline);
    return;
  }
  if (match(TokenType::Newline)) return;
  if (check(TokenType::Dedent)) return;
  if (check(TokenType::EndOfFile)) return;

  error("Expected newline or end of block");
}

void Parser::error(const std::string_view message) const {
  const auto &t = peek();
  if (t.type == TokenType::EndOfFile) {
    throw SyntaxError("Unexpected EOF while parsing", t.line, t.column);
  }
  throw SyntaxError(std::string(message), t.line, t.column);
}

void Parser::indentError(const std::string_view message) const {
  const auto &t = peek();
  throw IndentationError(std::string(message), t.line, t.column);
}

std::string Parser::parseTypeAnnotation() {
  const Token t = consume(TokenType::Identifier, "Expected type identifier");
  std::string typeStr = t.value;

  if (match(TokenType::LBracket)) {
    typeStr += "[";
    const Token inner = consume(TokenType::Identifier, "Expected inner type");
    typeStr += inner.value;
    consume(TokenType::RBracket, "Expected ']'");
    typeStr += "]";
  }
  return typeStr;
}

std::unique_ptr<Program> Parser::parseProgram() {
  auto prog = std::make_unique<Program>();

  while (!check(TokenType::EndOfFile)) {
    if (match(TokenType::Newline)) continue;

    // Top-level Constructs
    if (check(TokenType::From) || check(TokenType::Import)) {
      prog->imports.push_back(parseImportStatement());
    } else if (check(TokenType::Def) || check(TokenType::At)) {
      prog->functions.push_back(parseFunction());
    } else {
      // Allow statements (If, Assignment, etc.)
      // Note: global_statements is vector<unique_ptr<Statement>>
      // parseStatement returns unique_ptr<Statement>, covering If, Assign, etc.
      try {
        prog->global_statements.push_back(parseStatement());
      } catch (const SyntaxError &e) {
        // Fallback for better error message if it's garbage
        error(
            "Expected function definition, import, or valid statement. "
            "Original error: " +
            std::string(e.what()));
      }
    }
  }
  return prog;
}

std::unique_ptr<FunctionDef> Parser::parseFunction() {
  bool is_inline = false;
  bool is_interrupt = false;
  int vector = 0;

  while (check(TokenType::At)) {
    advance();  // Consume '@'
    const Token decorator =
        consume(TokenType::Identifier, "Expected decorator name");
    if (decorator.value == "inline") {
      is_inline = true;
    } else if (decorator.value == "interrupt") {
      is_interrupt = true;
      vector = 0x04;  // Default generic vector for PIC14

      if (check(TokenType::LParen)) {
        advance();  // Consume '('
        const Token vectorToken =
            consume(TokenType::Number, "Expected vector address");

        // Parse number (hex or dec)
        std::string text = vectorToken.value;
        int base = 10;
        if (text.size() >= 2 && text[0] == '0' &&
            (text[1] == 'x' || text[1] == 'X')) {
          base = 16;
        }

        try {
          vector = std::stoi(text, nullptr, base);
        } catch (...) {
          error("Invalid vector address");
        }

        consume(TokenType::RParen, "Expected ')'");
      }
    } else if (decorator.value == "staticmethod") {
      // Ignored: All class methods are treated as static in this compiler
    } else {
      error("Unknown decorator: " + decorator.value);
    }
    consume(TokenType::Newline, "Expected newline after decorator");
  }

  consume(TokenType::Def, "Expected 'def'");
  if (!check(TokenType::Identifier)) {
    error("Expected function name, but found " + tokens[pos].value +
          " (Type: " + std::to_string(static_cast<int>(tokens[pos].type)) +
          ")");
  }
  const Token nameToken = advance();
  const std::string name = nameToken.value;

  consume(TokenType::LParen, "Expected '(' after function name");
  std::vector<Param> params = parseParameters();
  consume(TokenType::RParen, "Expected ')' after parameters");

  std::string returnType = "void";  // Default to void/None
  if (match(TokenType::Arrow)) {
    returnType = parseTypeAnnotation();
  }

  consume(TokenType::Colon, "Expected ':' before function body");
  consume(TokenType::Newline, "Expected newline after function definition");

  function_depth++;
  std::unique_ptr<Block> body = parseBlock();
  function_depth--;

  return std::make_unique<FunctionDef>(name, std::move(params), returnType,
                                       std::move(body), is_inline, is_interrupt,
                                       vector);
}

std::unique_ptr<ClassDef> Parser::parseClassDefinition() {
  consume(TokenType::Class, "Expected 'class'");
  std::string name =
      consume(TokenType::Identifier, "Expected class name").value;
  std::vector<std::string> bases;
  if (match(TokenType::LParen)) {
    if (!check(TokenType::RParen)) {
      do {
        bases.push_back(
            consume(TokenType::Identifier, "Expected base class name").value);
      } while (match(TokenType::Comma));
    }
    consume(TokenType::RParen, "Expected ')'");
  }
  consume(TokenType::Colon, "Expected ':'");
  consume(TokenType::Newline, "Expected newline after class definition");
  auto body = parseBlock();
  auto class_def = std::make_unique<ClassDef>(std::move(name), std::move(bases),
                                              std::move(body));
  class_def->is_static = true;
  return class_def;
}

std::vector<Param> Parser::parseParameters() {
  std::vector<Param> params;

  if (check(TokenType::RParen)) {
    return params;
  }

  do {
    const Token name =
        consume(TokenType::Identifier, "Expected parameter name");
    consume(TokenType::Colon, "Parameter type is required (e.g. 'a: int')");
    const std::string type = parseTypeAnnotation();

    std::unique_ptr<Expression> default_val = nullptr;
    if (match(TokenType::Equal)) {
      default_val = parseExpression();
    }
    params.emplace_back(name.value, type, std::move(default_val));
  } while (match(TokenType::Comma));

  return params;
}

std::unique_ptr<Block> Parser::parseBlock() {
  if (!match(TokenType::Indent)) {
    indentError("Expected an indented block");
  }

  auto block = std::make_unique<Block>();

  while (!check(TokenType::Dedent) && !check(TokenType::EndOfFile)) {
    if (match(TokenType::Newline)) continue;  // Skip empty lines
    block->statements.push_back(parseStatement());
  }

  if (!match(TokenType::Dedent) && !check(TokenType::EndOfFile)) {
    indentError("Unindent does not match any outer indentation level");
  }

  return block;
}

std::unique_ptr<Statement> Parser::parseStatement() {
  if (check(TokenType::If)) return parseIfStatement();
  if (check(TokenType::Match)) return parseMatchStatement();
  if (check(TokenType::While)) return parseWhileStatement();
  if (check(TokenType::For)) return parseForStatement();
  if (check(TokenType::Def) || check(TokenType::At)) {
    if (function_depth > 0) {
      error("Nested function definitions are not supported");
    }
    return parseFunction();
  }
  if (check(TokenType::Return)) return parseReturnStatement();

  if (check(TokenType::Import) || check(TokenType::From)) {
    return parseImportStatement();
  }

  if (check(TokenType::Global)) {
    return parseGlobalStatement();
  }

  if (check(TokenType::Class)) {
    return parseClassDefinition();
  }

  if (match(TokenType::Break)) {
    consumeStatementEnd();
    return std::make_unique<BreakStmt>();
  }

  if (match(TokenType::Continue)) {
    consumeStatementEnd();
    return std::make_unique<ContinueStmt>();
  }

  if (match(TokenType::Pass)) {
    consumeStatementEnd();
    return std::make_unique<PassStmt>();
  }

  if (check(TokenType::Raise)) return parseRaiseStatement();

  return parseSimpleStatement();
}

std::unique_ptr<Statement> Parser::parseReturnStatement() {
  int line = peek().line;
  consume(TokenType::Return, "Expected 'return'");
  std::unique_ptr<Expression> value = nullptr;
  if (!check(TokenType::Newline) && !check(TokenType::Semicolon)) {
    value = parseExpression();
  }
  consumeStatementEnd();
  auto stmt = std::make_unique<ReturnStmt>(std::move(value));
  stmt->line = line;
  return stmt;
}

std::unique_ptr<Statement> Parser::parseRaiseStatement() {
  int line = peek().line;
  consume(TokenType::Raise, "Expected 'raise'");
  std::string error_type =
      consume(TokenType::Identifier, "Expected error type after 'raise'").value;
  consume(TokenType::LParen, "Expected '(' after error type");
  std::string message;
  if (check(TokenType::String)) {
    message = advance().value;
  }
  consume(TokenType::RParen, "Expected ')' after error message");
  consumeStatementEnd();
  auto stmt = std::make_unique<RaiseStmt>(error_type, message);
  stmt->line = line;
  return stmt;
}

std::unique_ptr<ImportStmt> Parser::parseImportStatement() {
  if (match(TokenType::Import)) {
    std::string mod_name =
        consume(TokenType::Identifier, "Expected module name").value;
    while (match(TokenType::Dot)) {
      mod_name +=
          "." + consume(TokenType::Identifier, "Expected part name").value;
    }
    return std::make_unique<ImportStmt>(mod_name, std::vector<std::string>{},
                                        0);
  }

  consume(TokenType::From, "Expected 'from'");

  int relative_level = 0;
  while (match(TokenType::Dot)) {
    relative_level++;
  }

  std::string mod_name;
  if (check(TokenType::Identifier)) {
    mod_name = consume(TokenType::Identifier, "Expected module name").value;
    while (match(TokenType::Dot)) {
      mod_name +=
          "." + consume(TokenType::Identifier, "Expected part name").value;
    }
  } else if (relative_level == 0) {
    error("Expected module name in absolute import");
  }

  consume(TokenType::Import, "Expected 'import'");

  std::vector<std::string> symbols;

  if (match(TokenType::Star)) {
    symbols.emplace_back("*");
  } else {
    do {
      Token sym = consume(TokenType::Identifier, "Expected symbol name");
      symbols.push_back(sym.value);
    } while (match(TokenType::Comma));
  }

  consumeStatementEnd();

  return std::make_unique<ImportStmt>(mod_name, symbols, relative_level);
}

std::unique_ptr<GlobalStmt> Parser::parseGlobalStatement() {
  int line = peek().line;
  consume(TokenType::Global, "Expected 'global'");
  std::vector<std::string> names;

  do {
    Token name = consume(TokenType::Identifier, "Expected variable name");
    names.push_back(name.value);
  } while (match(TokenType::Comma));

  consumeStatementEnd();

  auto stmt = std::make_unique<GlobalStmt>(names);
  stmt->line = line;
  return stmt;
}

std::unique_ptr<Statement> Parser::parseIfStatement() {
  int line = peek().line;
  consume(TokenType::If, "Expected 'if'");
  auto condition = parseExpression();
  consume(TokenType::Colon, "Expected ':'");
  consume(TokenType::Newline, "Expected newline");

  auto thenBranch = parseBlock();

  // Elif branches
  std::vector<
      std::pair<std::unique_ptr<Expression>, std::unique_ptr<Statement>>>
      elifBranches;
  while (match(TokenType::Elif)) {
    auto elifCond = parseExpression();
    consume(TokenType::Colon, "Expected ':'");
    consume(TokenType::Newline, "Expected newline");
    auto elifBlock = parseBlock();
    elifBranches.emplace_back(std::move(elifCond), std::move(elifBlock));
  }

  std::unique_ptr<Statement> elseBranch = nullptr;
  if (match(TokenType::Else)) {
    consume(TokenType::Colon, "Expected ':'");
    consume(TokenType::Newline, "Expected newline");
    elseBranch = parseBlock();
  }

  auto stmt =
      std::make_unique<IfStmt>(std::move(condition), std::move(thenBranch),
                               std::move(elifBranches), std::move(elseBranch));
  stmt->line = line;
  return stmt;
}

std::unique_ptr<Statement> Parser::parseMatchStatement() {
  consume(TokenType::Match, "Expected 'match'");
  auto target = parseExpression();
  consume(TokenType::Colon, "Expected ':'");
  consume(TokenType::Newline, "Expected newline");

  if (!match(TokenType::Indent)) {
    indentError("Expected indented block for match cases");
  }

  std::vector<CaseBranch> branches;
  while (!check(TokenType::Dedent) && !check(TokenType::EndOfFile)) {
    if (match(TokenType::Newline)) continue;

    consume(TokenType::Case, "Expected 'case'");

    std::unique_ptr<Expression> pattern = nullptr;
    // Check for wildcard '_'
    // Since we don't have a dedicated Underscore token, check for Identifier
    // with value "_"
    if (check(TokenType::Identifier) && peek().value == "_") {
      advance();          // consume '_'
      pattern = nullptr;  // Wildcard
    } else {
      // Limited pattern matching: literals or variable
      pattern = parseExpression();
    }

    consume(TokenType::Colon, "Expected ':'");
    consume(TokenType::Newline, "Expected newline");

    auto body = parseBlock();
    branches.push_back({std::move(pattern), std::move(body)});
  }

  if (!match(TokenType::Dedent) && !check(TokenType::EndOfFile)) {
    indentError("Unindent does not match any outer indentation level");
  }

  return std::make_unique<MatchStmt>(std::move(target), std::move(branches));
}

std::unique_ptr<Statement> Parser::parseWhileStatement() {
  int line = peek().line;
  consume(TokenType::While, "Expected 'while'");
  auto condition = parseExpression();
  consume(TokenType::Colon, "Expected ':'");
  consume(TokenType::Newline, "Expected newline");
  auto body = parseBlock();
  auto stmt =
      std::make_unique<WhileStmt>(std::move(condition), std::move(body));
  stmt->line = line;
  return stmt;
}

std::unique_ptr<Statement> Parser::parseForStatement() {
  int line = peek().line;
  consume(TokenType::For, "Expected 'for'");
  Token var_tok = consume(TokenType::Identifier, "Expected loop variable");
  consume(TokenType::In, "Expected 'in'");

  Token range_tok = consume(TokenType::Identifier, "Expected 'range'");
  if (range_tok.value != "range")
    error("Only 'range()' is supported in for loops");

  consume(TokenType::LParen, "Expected '('");

  auto arg1 = parseExpression();
  std::unique_ptr<Expression> arg2 = nullptr, arg3 = nullptr;
  if (match(TokenType::Comma)) {
    arg2 = parseExpression();
    if (match(TokenType::Comma)) {
      arg3 = parseExpression();
    }
  }
  consume(TokenType::RParen, "Expected ')'");
  consume(TokenType::Colon, "Expected ':'");
  consume(TokenType::Newline, "Expected newline");
  auto body = parseBlock();

  // Normalize: range(stop), range(start, stop), range(start, stop, step)
  std::unique_ptr<Expression> start, stop, step;
  if (!arg2) {
    // range(stop): start=0, stop=arg1, step=1
    start = nullptr;
    stop = std::move(arg1);
    step = nullptr;
  } else if (!arg3) {
    // range(start, stop)
    start = std::move(arg1);
    stop = std::move(arg2);
    step = nullptr;
  } else {
    // range(start, stop, step)
    start = std::move(arg1);
    stop = std::move(arg2);
    step = std::move(arg3);
  }

  auto stmt = std::make_unique<ForStmt>(
      var_tok.value, std::move(start), std::move(stop),
      std::move(step), std::move(body));
  stmt->line = line;
  return stmt;
}

std::unique_ptr<Statement> Parser::parseSimpleStatement() {
  int line = peek().line;
  if (check(TokenType::Return)) {
    return parseReturnStatement();
  }

  if (match(TokenType::Pass)) {
    consumeStatementEnd();
    auto stmt = std::make_unique<PassStmt>();
    stmt->line = line;
    return stmt;
  }

  if (match(TokenType::Break)) {
    consumeStatementEnd();
    auto stmt = std::make_unique<BreakStmt>();
    stmt->line = line;
    return stmt;
  }

  if (match(TokenType::Continue)) {
    consumeStatementEnd();
    auto stmt = std::make_unique<ContinueStmt>();
    stmt->line = line;
    return stmt;
  }

  return parseAssignmentOrDeclaration();
}

std::unique_ptr<Statement> Parser::parseAssignmentOrDeclaration() {
  int line = peek().line;
  auto expr = parseExpression();

  if (match(TokenType::Colon)) {
    const auto varExpr = dynamic_cast<VariableExpr *>(expr.get());
    if (!varExpr) error("Only simple variables can be annotated with types");

    std::string name = varExpr->name;
    std::string type = parseTypeAnnotation();

    std::unique_ptr<Expression> init = nullptr;
    if (match(TokenType::Equal)) {
      init = parseExpression();
    }
    consumeStatementEnd();

    // If type contains '[', use AnnAssign for subscripted types like
    // ptr[uint16]
    if (type.find('[') != std::string::npos) {
      auto stmt = std::make_unique<AnnAssign>(name, type, std::move(init));
      stmt->line = line;
      return stmt;
    }

    // Otherwise use VarDecl for simple types
    return std::make_unique<VarDecl>(name, type, std::move(init));
  }

  if (match(TokenType::Equal)) {
    auto value = parseExpression();
    consumeStatementEnd();
    auto stmt = std::make_unique<AssignStmt>(std::move(expr), std::move(value));
    stmt->line = line;
    return stmt;
  }

  // --- Augmented Assignments (+=, -=, *=, /=, %=, &=, |=, ^=, <<=, >>=) ---
  auto try_aug_assign = [&]() -> std::optional<AugOp> {
    if (match(TokenType::PlusEqual)) return AugOp::Add;
    if (match(TokenType::MinusEqual)) return AugOp::Sub;
    if (match(TokenType::StarEqual)) return AugOp::Mul;
    if (match(TokenType::SlashEqual)) return AugOp::Div;
    if (match(TokenType::PercentEqual)) return AugOp::Mod;
    if (match(TokenType::AmpEqual)) return AugOp::BitAnd;
    if (match(TokenType::PipeEqual)) return AugOp::BitOr;
    if (match(TokenType::CaretEqual)) return AugOp::BitXor;
    if (match(TokenType::LShiftEqual)) return AugOp::LShift;
    if (match(TokenType::RShiftEqual)) return AugOp::RShift;
    return std::nullopt;
  };

  if (auto aug_op = try_aug_assign()) {
    auto value = parseExpression();
    consumeStatementEnd();
    return std::make_unique<AugAssignStmt>(std::move(expr), *aug_op,
                                           std::move(value));
  }

  consumeStatementEnd();
  auto stmt = std::make_unique<ExprStmt>(std::move(expr));
  stmt->line = line;
  return stmt;
}

std::unique_ptr<Expression> Parser::parseExpression() {
  if (match(TokenType::Yield)) {
    auto value = parseExpression();
    return std::make_unique<YieldExpr>(std::move(value));
  }
  return parseLogicalOr();
}

std::unique_ptr<Expression> Parser::parseLogicalOr() {
  auto left = parseLogicalAnd();
  while (match(TokenType::Or)) {
    auto right = parseLogicalAnd();
    left = std::make_unique<BinaryExpr>(std::move(left), BinaryOp::Or,
                                        std::move(right));
  }
  return left;
}

std::unique_ptr<Expression> Parser::parseLogicalAnd() {
  auto left = parseLogicalNot();
  while (match(TokenType::And)) {
    auto right = parseBitwiseOr();
    left = std::make_unique<BinaryExpr>(std::move(left), BinaryOp::And,
                                        std::move(right));
  }
  return left;
}

std::unique_ptr<Expression> Parser::parseLogicalNot() {
  if (match(TokenType::Not)) {
    auto operand = parseLogicalNot();
    return std::make_unique<UnaryExpr>(UnaryOp::Not, std::move(operand));
  }
  return parseComparison();
}

std::unique_ptr<Expression> Parser::parseComparison() {
  auto left = parseBitwiseOr();

  while (check(TokenType::EqualEqual) || check(TokenType::BangEqual) ||
         check(TokenType::Less) || check(TokenType::LessEqual) ||
         check(TokenType::Greater) || check(TokenType::GreaterEqual)) {
    const Token opToken = advance();
    BinaryOp op;

    switch (opToken.type) {
      case TokenType::EqualEqual:
        op = BinaryOp::Equal;
        break;
      case TokenType::BangEqual:
        op = BinaryOp::NotEqual;
        break;
      case TokenType::Less:
        op = BinaryOp::Less;
        break;
      case TokenType::LessEqual:
        op = BinaryOp::LessEq;
        break;
      case TokenType::Greater:
        op = BinaryOp::Greater;
        break;
      case TokenType::GreaterEqual:
        op = BinaryOp::GreaterEq;
        break;
      default:
        break;  // Unreachable
    }

    auto right = parseBitwiseOr();
    left = std::make_unique<BinaryExpr>(std::move(left), op, std::move(right));
  }
  return left;
}

std::unique_ptr<Expression> Parser::parseBitwiseOr() {
  auto left = parseBitwiseXor();
  while (match(TokenType::Pipe)) {
    auto right = parseBitwiseXor();
    left = std::make_unique<BinaryExpr>(std::move(left), BinaryOp::BitOr,
                                        std::move(right));
  }
  return left;
}

std::unique_ptr<Expression> Parser::parseBitwiseXor() {
  auto left = parseBitwiseAnd();
  while (match(TokenType::Caret)) {
    auto right = parseBitwiseAnd();
    left = std::make_unique<BinaryExpr>(std::move(left), BinaryOp::BitXor,
                                        std::move(right));
  }
  return left;
}

std::unique_ptr<Expression> Parser::parseBitwiseAnd() {
  auto left = parseShift();
  while (match(TokenType::Ampersand)) {
    auto right = parseShift();
    left = std::make_unique<BinaryExpr>(std::move(left), BinaryOp::BitAnd,
                                        std::move(right));
  }
  return left;
}

std::unique_ptr<Expression> Parser::parseShift() {
  auto left = parseAdditive();
  while (check(TokenType::LShift) || check(TokenType::RShift)) {
    const Token opToken = advance();
    BinaryOp op = (opToken.type == TokenType::LShift) ? BinaryOp::LShift
                                                      : BinaryOp::RShift;
    auto right = parseAdditive();
    left = std::make_unique<BinaryExpr>(std::move(left), op, std::move(right));
  }
  return left;
}

std::unique_ptr<Expression> Parser::parseAdditive() {
  auto left = parseMultiplicative();
  while (check(TokenType::Plus) || check(TokenType::Minus)) {
    const Token opToken = advance();
    BinaryOp op =
        (opToken.type == TokenType::Plus) ? BinaryOp::Add : BinaryOp::Sub;
    auto right = parseMultiplicative();
    left = std::make_unique<BinaryExpr>(std::move(left), op, std::move(right));
  }
  return left;
}

std::unique_ptr<Expression> Parser::parseMultiplicative() {
  auto left = parseUnary();
  while (check(TokenType::Star) || check(TokenType::Slash) ||
         check(TokenType::Percent)) {
    const Token opToken = advance();
    BinaryOp op;
    if (opToken.type == TokenType::Star)
      op = BinaryOp::Mul;
    else if (opToken.type == TokenType::Slash)
      op = BinaryOp::Div;
    else
      op = BinaryOp::Mod;

    auto right = parseUnary();
    left = std::make_unique<BinaryExpr>(std::move(left), op, std::move(right));
  }
  return left;
}

std::unique_ptr<Expression> Parser::parseUnary() {
  if (check(TokenType::Minus)) {
    advance();
    auto operand = parseUnary();
    return std::make_unique<UnaryExpr>(UnaryOp::Negate, std::move(operand));
  }
  if (check(TokenType::Tilde)) {
    advance();
    auto operand = parseUnary();
    return std::make_unique<UnaryExpr>(UnaryOp::BitNot, std::move(operand));
  }
  if (check(TokenType::Not)) {
    advance();
    auto operand = parseUnary();
    return std::make_unique<UnaryExpr>(UnaryOp::Not, std::move(operand));
  }
  return parsePostfix();
}

std::unique_ptr<Expression> Parser::parsePostfix() {
  auto expr = parsePrimary();

  while (true) {
    if (match(TokenType::LParen)) {
      std::vector<std::unique_ptr<Expression>> args;
      if (!check(TokenType::RParen)) {
        do {
          if (check(TokenType::Identifier) &&
              (pos + 1 < tokens.size() &&
               tokens[pos + 1].type == TokenType::Equal)) {
            // Named argument: name = value
            std::string name =
                consume(TokenType::Identifier, "Expected argument name").value;
            consume(TokenType::Equal, "Expected '='");
            auto value = parseExpression();
            args.push_back(std::make_unique<KeywordArgExpr>(std::move(name),
                                                            std::move(value)));
          } else {
            // Positional argument
            args.push_back(parseExpression());
          }
        } while (match(TokenType::Comma));
      }
      consume(TokenType::RParen, "Expected ')'");
      expr = std::make_unique<CallExpr>(std::move(expr), std::move(args));
    } else if (match(TokenType::LBracket)) {
      auto index = parseExpression();
      consume(TokenType::RBracket, "Expected ']'");
      expr = std::make_unique<IndexExpr>(std::move(expr), std::move(index));
    } else if (match(TokenType::Dot)) {
      const Token member =
          consume(TokenType::Identifier, "Expected member name");
      expr = std::make_unique<MemberAccessExpr>(std::move(expr), member.value);
    } else {
      break;
    }
  }
  return expr;
}

std::unique_ptr<Expression> Parser::parsePrimary() {
  if (match(TokenType::True)) return std::make_unique<BooleanLiteral>(true);
  if (match(TokenType::False)) return std::make_unique<BooleanLiteral>(false);

  if (match(TokenType::Identifier)) {
    return std::make_unique<VariableExpr>(previous().value);
  }

  if (match(TokenType::String)) {
    return std::make_unique<StringLiteral>(previous().value);
  }

  if (match(TokenType::Number)) {
    const Token t = previous();
    std::string text = t.value;
    text.erase(std::remove(text.begin(), text.end(), '_'), text.end());

    int base = 10;
    size_t offset = 0;

    if (text.size() >= 2 && text[0] == '0') {
      if (const char prefix = std::tolower(text[1]); prefix == 'x') {
        base = 16;
        offset = 2;
      } else if (prefix == 'b') {
        base = 2;
        offset = 2;
      } else if (prefix == 'o') {
        base = 8;
        offset = 2;
      }
    }

    try {
      if (base == 10 && text.find('.') != std::string::npos) {
        double val = std::stod(text);
        return std::make_unique<FloatLiteral>(val);
      }
      int val = std::stoi(text.substr(offset), nullptr, base);
      return std::make_unique<IntegerLiteral>(val);
    } catch (const std::out_of_range &) {
      error("Integer literal is too large: '" + t.value + "'");
    } catch (const std::invalid_argument &) {
      error("Invalid integer literal: '" + t.value + "'");
    }
    return nullptr;
  }

  if (match(TokenType::LParen)) {
    auto expr = parseExpression();
    consume(TokenType::RParen, "Expected ')'");
    return expr;
  }

  error("Expected expression");
  return nullptr;
}