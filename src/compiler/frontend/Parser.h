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

#ifndef PARSER_H
#define PARSER_H

#pragma once
#include <memory>
#include <string_view>
#include <vector>

#include "Ast.h"
#include "Token.h"

class Parser {
 public:
  explicit Parser(const std::vector<Token> &tokens);

  std::unique_ptr<Program> parseProgram();

  // Public entry point for parsing a single expression (used by f-string sub-parsers).
  std::unique_ptr<Expression> parseExpression_public() { return parseExpression(); }

 private:
  const std::vector<Token> tokens;
  size_t pos = 0;
  int function_depth = 0;

  [[nodiscard]] const Token &peek() const;

  // Look at the token after the current one (one position ahead).
  [[nodiscard]] const Token &peek_next() const;

  [[nodiscard]] const Token &previous() const;

  Token advance();

  [[nodiscard]] bool check(TokenType type) const;

  bool match(TokenType type);

  Token consume(TokenType type, std::string_view errorMessage);

  void consumeStatementEnd();

  [[noreturn]] void error(std::string_view message) const;

  void indentError(std::string_view message) const;

  std::string parseTypeAnnotation();

  std::unique_ptr<FunctionDef> parseFunction();

  std::unique_ptr<ClassDef> parseClassDefinition();

  std::vector<Param> parseParameters();

  std::unique_ptr<Block> parseBlock();

  std::unique_ptr<Statement> parseStatement();

  std::unique_ptr<Statement> parseIfStatement();

  std::unique_ptr<Statement> parseMatchStatement();

  std::unique_ptr<Statement> parseWhileStatement();

  std::unique_ptr<Statement> parseForStatement();

  std::unique_ptr<Statement> parseSimpleStatement();

  std::unique_ptr<Statement> parseReturnStatement();

  std::unique_ptr<Statement> parseRaiseStatement();

  std::unique_ptr<Statement> parseWithStatement();   // T2.2

  std::unique_ptr<Statement> parseAssertStatement(); // T2.3

  std::unique_ptr<Statement> parseAssignmentOrDeclaration();

  std::unique_ptr<Expression> parseExpression();  // Entry point
  std::unique_ptr<Expression> parseLogicalOr();   // or
  std::unique_ptr<Expression> parseLogicalAnd();  // and
  std::unique_ptr<Expression> parseLogicalNot();  // not
  std::unique_ptr<Expression> parseComparison();

  std::unique_ptr<Expression> parseBitwiseOr();       // |
  std::unique_ptr<Expression> parseBitwiseXor();      // ^
  std::unique_ptr<Expression> parseBitwiseAnd();      // &
  std::unique_ptr<Expression> parseShift();           // <<, >>
  std::unique_ptr<Expression> parseAdditive();        // +, -
  std::unique_ptr<Expression> parseMultiplicative();  // *, /, %
  std::unique_ptr<Expression> parsePower();           // **
  std::unique_ptr<Expression> parseUnary();           // -, not, ~, !

  std::unique_ptr<Expression> parsePostfix();

  std::unique_ptr<Expression> parsePrimary();  // Literals, (Expr), Identifiers

  std::unique_ptr<ImportStmt> parseImportStatement();

  std::unique_ptr<GlobalStmt> parseGlobalStatement();
  std::unique_ptr<NonlocalStmt> parseNonlocalStatement();
};

#endif  // PARSER_H