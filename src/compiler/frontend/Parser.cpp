/*
 * -----------------------------------------------------------------------------
 * Whisnake Compiler (whipc)
 * Copyright (C) 2026 Ivan Montiel Cardona and the Whisnake Project Authors
 *
 * SPDX-License-Identifier: MIT
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction, including without limitation the rights
 * to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
 * copies of the Software, and to permit persons to whom the Software is
 * furnished to do so, subject to the following conditions:
 *
 * The above copyright notice and this permission notice shall be included in
 * all copies or substantial portions of the Software.
 *
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
 * IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
 * FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
 * AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
 * LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
 * OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
 * THE SOFTWARE.
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
#include "Lexer.h"

Parser::Parser(const std::vector<Token> &tokens) : tokens(tokens), pos(0) {}

const Token &Parser::peek() const {
  if (pos >= tokens.size()) return tokens.back();
  return tokens[pos];
}

const Token &Parser::peek_next() const {
  size_t next = pos + 1;
  if (next >= tokens.size()) return tokens.back();
  return tokens[next];
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
    // Accept either an identifier (e.g. const[str]) or an integer literal
    // (e.g. uint8[4] for fixed-size arrays).
    if (check(TokenType::Identifier)) {
      const Token inner = consume(TokenType::Identifier, "Expected inner type");
      typeStr += inner.value;
    } else if (check(TokenType::Number)) {
      const Token inner = consume(TokenType::Number, "Expected array size");
      typeStr += inner.value;
    } else {
      error("Expected type name or array size inside '['");
    }
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
  bool is_property_getter = false;
  bool is_property_setter = false;
  std::string prop_setter_of;
  bool is_extern = false;
  std::string extern_symbol;

  while (check(TokenType::At)) {
    advance();  // Consume '@'
    const Token decorator =
        consume(TokenType::Identifier, "Expected decorator name");
    if (decorator.value == "inline") {
      is_inline = true;
    } else if (decorator.value == "extern") {
      // @extern("symbol_name"): declares a C function callable from Whisnake.
      // The function body is a stub (ignored); the compiler emits CALL symbol.
      is_extern = true;
      consume(TokenType::LParen, "Expected '(' after @extern");
      const Token sym_tok = consume(TokenType::String,
          "Expected C symbol name as a string literal in @extern(\"name\")");
      extern_symbol = sym_tok.value;
      consume(TokenType::RParen, "Expected ')' after @extern symbol name");
    } else if (decorator.value == "property") {
      // @property: marks a getter; implicitly inline for ZCA
      is_property_getter = true;
      is_inline = true;
    } else if (check(TokenType::Dot)) {
      // @name.setter or @name.getter
      advance();  // consume '.'
      const Token suffix =
          consume(TokenType::Identifier, "Expected 'setter' or 'getter' after '.'");
      if (suffix.value == "setter") {
        is_property_setter = true;
        is_inline = true;  // property setters are implicitly inline for ZCA
        prop_setter_of = decorator.value;
      } else if (suffix.value == "getter") {
        is_property_getter = true;
        is_inline = true;
      } else {
        error("Unknown property modifier '@" + decorator.value + "." +
              suffix.value + "'");
      }
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

  auto func = std::make_unique<FunctionDef>(name, std::move(params), returnType,
                                            std::move(body), is_inline,
                                            is_interrupt, vector);
  func->is_property_getter = is_property_getter;
  func->is_property_setter = is_property_setter;
  func->property_name = prop_setter_of;
  func->is_extern = is_extern;
  func->extern_symbol = extern_symbol;
  return func;
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
    // Type annotation is optional: `self` and ZCA-typed params (e.g. `pin: Pin`)
    // default to uint8 when no annotation is provided.
    std::string type;
    if (match(TokenType::Colon)) {
      type = parseTypeAnnotation();
    }

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
      // Allow @inline nested functions (for nonlocal variable capture, PEP 3104).
      // A bare `def` without @inline inside a function is still unsupported.
      bool is_inline_decorator = check(TokenType::At) &&
                                 peek_next().value == "inline";
      if (!is_inline_decorator) {
        error("Nested function definitions require the @inline decorator");
      }
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

  if (check(TokenType::Nonlocal)) {
    return parseNonlocalStatement();
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

  if (check(TokenType::With)) return parseWithStatement();    // T2.2

  if (check(TokenType::Assert)) return parseAssertStatement(); // T2.3

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

// T2.2: with context_expr [as name] [, context_expr [as name]]* : body
// PEP 343: multi-item with desugared to nested WithStmt at parse time.
std::unique_ptr<Statement> Parser::parseWithStatement() {
  int line = peek().line;
  consume(TokenType::With, "Expected 'with'");

  // Collect all (ctx, as_name) items.
  struct WithItem {
    std::unique_ptr<Expression> ctx;
    std::string as_name;
  };
  std::vector<WithItem> items;

  do {
    auto ctx = parseExpression();
    std::string as_name;
    if (match(TokenType::As)) {
      as_name = consume(TokenType::Identifier, "Expected name after 'as'").value;
    }
    items.push_back({std::move(ctx), std::move(as_name)});
  } while (match(TokenType::Comma));

  consume(TokenType::Colon, "Expected ':' after 'with' header");
  consumeStatementEnd();
  std::unique_ptr<Statement> body = parseBlock();

  // Desugar right-to-left: innermost item wraps the original body.
  for (int i = static_cast<int>(items.size()) - 1; i >= 0; --i) {
    auto ws = std::make_unique<WithStmt>(std::move(items[i].ctx),
                                        std::move(items[i].as_name),
                                        std::move(body));
    ws->line = line;
    body = std::unique_ptr<Statement>(std::move(ws));
  }
  return body;
}

// T2.3: assert condition [, message]
std::unique_ptr<Statement> Parser::parseAssertStatement() {
  int line = peek().line;
  consume(TokenType::Assert, "Expected 'assert'");
  auto cond = parseExpression();
  std::string message;
  if (match(TokenType::Comma)) {
    if (check(TokenType::String)) {
      message = advance().value;
    } else {
      // Accept any expression as message (stringify it as empty for now)
      parseExpression();
    }
  }
  consumeStatementEnd();
  auto stmt = std::make_unique<AssertStmt>(std::move(cond), message);
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
    auto stmt =
        std::make_unique<ImportStmt>(mod_name, std::vector<std::string>{}, 0);
    if (match(TokenType::As)) {
      stmt->module_alias =
          consume(TokenType::Identifier, "Expected alias name after 'as'").value;
    }
    return stmt;
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
  std::map<std::string, std::string> sym_aliases;

  if (match(TokenType::Star)) {
    symbols.emplace_back("*");
  } else {
    do {
      Token sym = consume(TokenType::Identifier, "Expected symbol name");
      symbols.push_back(sym.value);
      if (match(TokenType::As)) {
        Token alias = consume(TokenType::Identifier, "Expected alias name after 'as'");
        sym_aliases[sym.value] = alias.value;
      }
    } while (match(TokenType::Comma));
  }

  consumeStatementEnd();

  auto stmt = std::make_unique<ImportStmt>(mod_name, symbols, relative_level);
  stmt->aliases = std::move(sym_aliases);
  return stmt;
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

// F10 (PEP 3104): nonlocal name1, name2
std::unique_ptr<NonlocalStmt> Parser::parseNonlocalStatement() {
  int line = peek().line;
  consume(TokenType::Nonlocal, "Expected 'nonlocal'");
  std::vector<std::string> names;

  do {
    Token name = consume(TokenType::Identifier, "Expected variable name");
    names.push_back(name.value);
  } while (match(TokenType::Comma));

  consumeStatementEnd();

  auto stmt = std::make_unique<NonlocalStmt>(names);
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
    std::string capture_name;

    // Check for wildcard '_'
    if (check(TokenType::Identifier) && peek().value == "_") {
      advance();          // consume '_'
      pattern = nullptr;  // Wildcard
    }
    // PEP 634: bare unqualified identifier = capture pattern (binds subject to name).
    // Distinguish from dotted-name value patterns: `case Pin.OUT:` has a dot.
    else if (check(TokenType::Identifier) &&
             (pos + 1 >= tokens.size() ||
              (tokens[pos + 1].type != TokenType::Dot &&
               tokens[pos + 1].type != TokenType::LParen))) {
      // Peek ahead: if the next meaningful token after the identifier is ':',
      // 'if', or 'as', this is a bare-name capture (not a value comparison).
      // If the identifier is followed by '|', it could be part of an OR pattern
      // — treat the whole expression as a value pattern in that case.
      size_t lookahead = pos + 1;
      while (lookahead < tokens.size() && tokens[lookahead].type == TokenType::Newline)
        ++lookahead;
      bool next_is_or = lookahead < tokens.size() && tokens[lookahead].type == TokenType::Pipe;
      if (!next_is_or) {
        capture_name = peek().value;
        advance();   // consume the identifier
        pattern = nullptr;   // wildcard-capture: always matches
      } else {
        pattern = parseExpression();  // part of OR pattern, treat as value
      }
    } else {
      pattern = parseExpression();
    }

    // PEP 634: optional `as name` capture after the pattern.
    if (check(TokenType::As)) {
      advance();  // consume 'as'
      if (!check(TokenType::Identifier))
        throw std::runtime_error("Expected identifier after 'as' in case pattern");
      capture_name = peek().value;
      advance();
    }

    // PEP 634: optional guard `if expr` after pattern (and optional as-clause).
    std::unique_ptr<Expression> guard = nullptr;
    if (check(TokenType::If)) {
      advance();  // consume 'if'
      guard = parseExpression();
    }

    consume(TokenType::Colon, "Expected ':'");
    consume(TokenType::Newline, "Expected newline");

    auto body = parseBlock();
    branches.push_back({std::move(pattern), std::move(guard), capture_name, std::move(body)});
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

  // Check for tuple-unpack loop variables: for i, x in enumerate(...)
  std::string var2_name;
  if (match(TokenType::Comma)) {
    Token var2_tok = consume(TokenType::Identifier, "Expected second loop variable");
    var2_name = var2_tok.value;
  }

  consume(TokenType::In, "Expected 'in'");

  // Check for range()-based vs general iterable
  if (check(TokenType::Identifier) && peek().value == "range") {
    consume(TokenType::Identifier, "Expected 'range'");
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
      start = nullptr;
      stop = std::move(arg1);
      step = nullptr;
    } else if (!arg3) {
      start = std::move(arg1);
      stop = std::move(arg2);
      step = nullptr;
    } else {
      start = std::move(arg1);
      stop = std::move(arg2);
      step = std::move(arg3);
    }

    auto stmt = std::make_unique<ForStmt>(
        var_tok.value, std::move(start), std::move(stop),
        std::move(step), std::move(body));
    stmt->var2_name = var2_name;
    stmt->line = line;
    return stmt;
  }

  // General for-in loop: for var in <expr>: (compile-time iterable)
  auto iterable = parseExpression();
  consume(TokenType::Colon, "Expected ':'");
  consume(TokenType::Newline, "Expected newline");
  auto body = parseBlock();

  auto stmt = std::make_unique<ForStmt>(
      var_tok.value, std::move(iterable), std::move(body));
  stmt->var2_name = var2_name;
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

  // Tuple-unpack assignment: a, b = expr  (comma immediately after first ident)
  // PEP 3132: a, *rest = expr  or  first, *mid, last = expr
  if (check(TokenType::Comma)) {
    if (const auto *first_var = dynamic_cast<const VariableExpr *>(expr.get())) {
      std::vector<std::string> targets;
      int starred_index = -1;
      targets.push_back(first_var->name);
      while (match(TokenType::Comma)) {
        if (check(TokenType::Star)) {
          advance();  // consume '*'
          if (starred_index != -1)
            throw std::runtime_error("Only one starred expression allowed in assignment");
          Token t = consume(TokenType::Identifier, "Expected name after '*' in tuple unpack");
          starred_index = static_cast<int>(targets.size());
          targets.push_back(t.value);
        } else {
          Token t = consume(TokenType::Identifier, "Expected variable name in tuple unpack");
          targets.push_back(t.value);
        }
      }
      consume(TokenType::Equal, "Expected '=' in tuple unpack assignment");
      auto value = parseExpression();
      consumeStatementEnd();
      auto stmt = std::make_unique<TupleUnpackStmt>(std::move(targets), std::move(value), starred_index);
      stmt->line = line;
      return stmt;
    }
  }

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
    // Chained assignment: a = b = 0
    // Collect all targets; the final expression is the RHS.
    if (check(TokenType::Equal)) {
      std::vector<std::unique_ptr<Expression>> targets;
      targets.push_back(std::move(expr));
      std::unique_ptr<Expression> rhs = std::move(value);
      while (match(TokenType::Equal)) {
        targets.push_back(std::move(rhs));
        rhs = parseExpression();
      }
      consumeStatementEnd();
      // Emit as a Block: first assign innermost target, then copy to the rest.
      // For simplicity: assign innermost first, then subsequent targets = innermost.
      auto block = std::make_unique<Block>();
      // targets[0] = leftmost (a), targets.back() = innermost (b in a=b=0)
      // innermost = rhs; rest = innermost variable
      auto &inner = targets.back();
      // inner_name for subsequent assignments
      std::string inner_name;
      if (auto *ve = dynamic_cast<VariableExpr *>(inner.get())) {
        inner_name = ve->name;
      }
      // assign innermost = rhs
      auto inner_stmt = std::make_unique<AssignStmt>(
          std::move(inner), std::move(rhs));
      inner_stmt->line = line;
      block->statements.push_back(std::move(inner_stmt));
      // assign each remaining target = innermost_name
      if (!inner_name.empty()) {
        for (int ci = (int)targets.size() - 2; ci >= 0; --ci) {
          auto chain_stmt = std::make_unique<AssignStmt>(
              std::move(targets[ci]),
              std::make_unique<VariableExpr>(inner_name));
          chain_stmt->line = line;
          block->statements.push_back(std::move(chain_stmt));
        }
      }
      return block;
    }
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
    if (match(TokenType::FloorDivEqual)) return AugOp::FloorDiv;
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
  // Ternary: true_val if condition else false_val
  // Parse left (true_val) first, then check for 'if' keyword.
  auto left = parseLogicalOr();
  if (match(TokenType::If)) {
    auto condition = parseExpression();
    consume(TokenType::Else, "Expected 'else' in ternary expression");
    auto false_val = parseExpression();
    return std::make_unique<TernaryExpr>(std::move(left), std::move(condition),
                                         std::move(false_val));
  }
  return left;
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
    auto right = parseLogicalNot();
    left = std::make_unique<BinaryExpr>(std::move(left), BinaryOp::And,
                                        std::move(right));
  }
  return left;
}

std::unique_ptr<Expression> Parser::parseLogicalNot() {
  // "not in" is a binary operator — don't consume "not" when followed by "in"
  if (check(TokenType::Not) && peek_next().type != TokenType::In) {
    advance();  // consume "not"
    auto operand = parseLogicalNot();
    return std::make_unique<UnaryExpr>(UnaryOp::Not, std::move(operand));
  }
  return parseComparison();
}

std::unique_ptr<Expression> Parser::parseComparison() {
  auto left = parseBitwiseOr();

  while (check(TokenType::EqualEqual) || check(TokenType::BangEqual) ||
         check(TokenType::Less) || check(TokenType::LessEqual) ||
         check(TokenType::Greater) || check(TokenType::GreaterEqual) ||
         check(TokenType::In) || check(TokenType::Is) ||
         (check(TokenType::Not) && peek_next().type == TokenType::In)) {
    BinaryOp op;

    if (check(TokenType::Not)) {
      // "not in"
      advance();  // consume "not"
      consume(TokenType::In, "Expected 'in' after 'not'");
      op = BinaryOp::NotIn;
    } else if (check(TokenType::Is)) {
      advance();  // consume "is"
      if (check(TokenType::Not)) {
        advance();  // consume "not"
        op = BinaryOp::IsNot;
      } else {
        op = BinaryOp::Is;
      }
    } else if (check(TokenType::In)) {
      advance();  // consume "in"
      op = BinaryOp::In;
    } else {
      const Token opToken = advance();
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
          op = BinaryOp::Equal;  // Unreachable
          break;
      }
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
  auto left = parsePower();
  while (check(TokenType::Star) || check(TokenType::Slash) ||
         check(TokenType::FloorDiv) || check(TokenType::Percent)) {
    const Token opToken = advance();
    BinaryOp op;
    if (opToken.type == TokenType::Star)
      op = BinaryOp::Mul;
    else if (opToken.type == TokenType::Slash)
      op = BinaryOp::Div;
    else if (opToken.type == TokenType::FloorDiv)
      op = BinaryOp::FloorDiv;
    else
      op = BinaryOp::Mod;

    auto right = parsePower();
    left = std::make_unique<BinaryExpr>(std::move(left), op, std::move(right));
  }
  return left;
}

// parsePower: right-associative ** operator (compile-time constant fold only)
std::unique_ptr<Expression> Parser::parsePower() {
  auto left = parseUnary();
  if (check(TokenType::DoubleStar)) {
    advance();
    auto right = parsePower();  // right-associative
    left = std::make_unique<BinaryExpr>(std::move(left), BinaryOp::Pow,
                                        std::move(right));
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
      // PEP 197 (F8): detect slice syntax [start:stop:step].
      // If a ':' appears before ']', parse as SliceExpr.
      std::unique_ptr<Expression> index;
      if (check(TokenType::Colon)) {
        // [: ...] — start is omitted
        advance();  // consume ':'
        std::unique_ptr<Expression> stop = check(TokenType::RBracket) || check(TokenType::Colon)
                                               ? nullptr : parseExpression();
        std::unique_ptr<Expression> step;
        if (match(TokenType::Colon))
          step = check(TokenType::RBracket) ? nullptr : parseExpression();
        index = std::make_unique<SliceExpr>(nullptr, std::move(stop), std::move(step));
      } else {
        auto first = parseExpression();
        if (check(TokenType::Colon)) {
          // [start: ...] — have start, may have stop and step
          advance();  // consume ':'
          std::unique_ptr<Expression> stop = check(TokenType::RBracket) || check(TokenType::Colon)
                                                 ? nullptr : parseExpression();
          std::unique_ptr<Expression> step;
          if (match(TokenType::Colon))
            step = check(TokenType::RBracket) ? nullptr : parseExpression();
          index = std::make_unique<SliceExpr>(std::move(first), std::move(stop), std::move(step));
        } else {
          index = std::move(first);  // plain index
        }
      }
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
  // PEP 3 (F9): lambda [params]: expr
  if (match(TokenType::Lambda)) {
    std::vector<Param> params;
    // Parse comma-separated parameter names (optionally typed: name: type)
    while (!check(TokenType::Colon) && !check(TokenType::EndOfFile)) {
      std::string pname = consume(TokenType::Identifier, "Expected parameter name").value;
      std::string ptype = "uint8";  // default type
      // Save pos BEFORE consuming ':' so we can rewind it if ':' is the body separator.
      size_t colon_pos = pos;
      if (match(TokenType::Colon)) {
        // Typed parameter: lambda x: uint8, y: uint8: body
        // But ':' also terminates the param list — peek ahead: if followed by
        // the body expression (not another identifier+colon), stop.
        // Heuristic: if the token after ':' is an identifier and the one after
        // that is also ':', treat it as a type annotation.
        size_t save_pos = pos;
        if (check(TokenType::Identifier)) {
          std::string type_tok = peek().value;
          size_t next = pos + 1;
          while (next < tokens.size() && tokens[next].type == TokenType::LBracket) {
            // e.g. ptr[uint8] — consume brackets
            next += 3;  // '[', type, ']'
          }
          // If what follows is ',' or another param, it's a type annotation
          if (next < tokens.size() &&
              (tokens[next].type == TokenType::Comma ||
               tokens[next].type == TokenType::Colon)) {
            // Consume type tokens (simple: one identifier, optionally [inner])
            ptype = advance().value;
            if (check(TokenType::LBracket)) {
              advance(); ptype += "[" + advance().value + "]";
              consume(TokenType::RBracket, "Expected ']'");
            }
          } else {
            // The colon is the lambda body separator — rewind past the ':' too
            pos = colon_pos;
          }
        } else {
          pos = colon_pos;  // rewind past the ':'
        }
      }
      params.emplace_back(pname, ptype);
      if (!match(TokenType::Comma)) break;
    }
    consume(TokenType::Colon, "Expected ':' after lambda parameters");
    auto body = parseExpression();
    return std::make_unique<LambdaExpr>(std::move(params), std::move(body));
  }

  if (match(TokenType::True)) return std::make_unique<BooleanLiteral>(true);
  if (match(TokenType::False)) return std::make_unique<BooleanLiteral>(false);
  if (match(TokenType::None)) return std::make_unique<IntegerLiteral>(-1);

  if (match(TokenType::Identifier)) {
    Token t = previous();
    // Walrus operator: name := expr
    if (check(TokenType::Walrus)) {
      advance();  // consume :=
      auto val = parseExpression();
      return std::make_unique<WalrusExpr>(t.value, std::move(val));
    }
    return std::make_unique<VariableExpr>(t.value);
  }

  if (match(TokenType::BytesLiteral)) {
    // b"..." token value is comma-separated decimal byte values.
    // Parse and return as a ListExpr of IntegerLiterals for uniform handling.
    const std::string &encoded = previous().value;
    std::vector<std::unique_ptr<Expression>> elems;
    if (!encoded.empty()) {
      // Parse comma-separated integers
      size_t start = 0;
      while (start <= encoded.size()) {
        size_t comma = encoded.find(',', start);
        if (comma == std::string::npos) comma = encoded.size();
        std::string tok = encoded.substr(start, comma - start);
        if (!tok.empty()) {
          elems.push_back(std::make_unique<IntegerLiteral>(std::stoi(tok)));
        }
        start = comma + 1;
      }
    }
    return std::make_unique<ListExpr>(std::move(elems));
  }

  if (match(TokenType::String)) {
    return std::make_unique<StringLiteral>(previous().value);
  }

  if (match(TokenType::FString)) {
    // Parse f-string raw interior into a list of literal and expression parts.
    // Raw interior example: "arch=" followed by {__CHIP__.arch}
    const std::string &raw = previous().value;
    std::vector<FStringPart> parts;
    size_t i = 0;
    while (i < raw.size()) {
      if (raw[i] == '{') {
        // Find the matching closing brace (no nesting).
        size_t j = i + 1;
        while (j < raw.size() && raw[j] != '}') j++;
        if (j >= raw.size()) {
          error("Unterminated '{' in f-string");
        }
        std::string expr_src = raw.substr(i + 1, j - i - 1);
        // Parse the inner expression using a sub-Lexer + sub-Parser.
        Lexer sub_lex(expr_src);
        std::vector<Token> sub_tokens = sub_lex.tokenize();
        Parser sub_parser(sub_tokens);
        auto inner_expr = sub_parser.parseExpression_public();
        FStringPart part;
        part.is_expr = true;
        part.expr = std::move(inner_expr);
        parts.push_back(std::move(part));
        i = j + 1;
      } else if (raw[i] == '}') {
        error("Unexpected '}' in f-string");
      } else {
        // Collect literal text until next '{' or end.
        std::string text;
        while (i < raw.size() && raw[i] != '{' && raw[i] != '}') {
          // Process escape sequences in literal text.
          if (raw[i] == '\\' && i + 1 < raw.size()) {
            char esc = raw[i + 1];
            switch (esc) {
              case 'n':  text += '\n'; break;
              case 't':  text += '\t'; break;
              case 'r':  text += '\r'; break;
              case '0':  text += '\0'; break;
              case '\\': text += '\\'; break;
              case '\'': text += '\''; break;
              case '"':  text += '"';  break;
              default:   text += '\\'; text += esc; break;
            }
            i += 2;
          } else {
            text += raw[i++];
          }
        }
        if (!text.empty()) {
          FStringPart part;
          part.is_expr = false;
          part.text = std::move(text);
          parts.push_back(std::move(part));
        }
      }
    }
    return std::make_unique<FStringExpr>(std::move(parts));
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
    // Empty tuple: ()  — not valid in Whisnake, but handle gracefully
    if (check(TokenType::RParen)) {
      advance();
      return std::make_unique<TupleExpr>(
          std::vector<std::unique_ptr<Expression>>{});
    }
    auto first = parseExpression();
    if (check(TokenType::Comma)) {
      // Tuple literal: (expr, expr, ...)
      std::vector<std::unique_ptr<Expression>> elems;
      elems.push_back(std::move(first));
      while (match(TokenType::Comma)) {
        if (check(TokenType::RParen)) break;  // trailing comma allowed
        elems.push_back(parseExpression());
      }
      consume(TokenType::RParen, "Expected ')'");
      return std::make_unique<TupleExpr>(std::move(elems));
    }
    consume(TokenType::RParen, "Expected ')'");
    return first;
  }

  if (match(TokenType::LBracket)) {
    // Empty list
    if (check(TokenType::RBracket)) {
      advance();
      return std::make_unique<ListExpr>(
          std::vector<std::unique_ptr<Expression>>{});
    }
    // Parse first element; decide if this is a comprehension or a literal.
    auto first = parseExpression();
    if (match(TokenType::For)) {
      // List comprehension: [first for var in iterable [for var2 in iterable2] [if cond]]
      // Use parseLogicalOr() for iterables so that 'if' and 'for' keywords are not
      // consumed as part of a ternary expression within the iterable.
      Token var_tok = consume(TokenType::Identifier, "Expected loop variable");
      consume(TokenType::In, "Expected 'in'");
      auto iterable = parseLogicalOr();

      // Check for optional inner 'for' clause (nested comprehension)
      std::string var2_name;
      std::unique_ptr<Expression> iterable2;
      if (match(TokenType::For)) {
        Token var2_tok = consume(TokenType::Identifier, "Expected loop variable");
        consume(TokenType::In, "Expected 'in'");
        iterable2 = parseLogicalOr();
        var2_name = var2_tok.value;
      }

      // Check for optional 'if' filter clause.
      // The filter is also parsed with parseLogicalOr() to avoid ambiguity.
      std::unique_ptr<Expression> filter;
      if (match(TokenType::If)) {
        filter = parseLogicalOr();
      }

      consume(TokenType::RBracket, "Expected ']'");
      return std::make_unique<ListCompExpr>(std::move(first), var_tok.value,
                                            std::move(iterable),
                                            std::move(var2_name),
                                            std::move(iterable2),
                                            std::move(filter));
    }
    // Regular list literal
    std::vector<std::unique_ptr<Expression>> elems;
    elems.push_back(std::move(first));
    while (match(TokenType::Comma)) {
      elems.push_back(parseExpression());
    }
    consume(TokenType::RBracket, "Expected ']'");
    return std::make_unique<ListExpr>(std::move(elems));
  }

  error("Expected expression");
  return nullptr;
}