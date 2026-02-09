#include "Errors.h"
#include "Parser.h"
#include <stdexcept>
#include <format> // C++20/23

Parser::Parser(const std::vector<Token>& t) : tokens(t), pos(0) {}

// --- Helpers ---

const Token& Parser::peek() const {
    if (pos >= tokens.size()) return tokens.back();
    return tokens[pos];
}

Token Parser::advance() {
    if (pos < tokens.size()) pos++;
    return tokens[pos - 1];
}

bool Parser::check(const TokenType type) const {
    return peek().type == type;
}

bool Parser::match(const TokenType type) {
    if (check(type)) {
        advance();
        return true;
    }
    return false;
}

Token Parser::consume(const TokenType type, const std::string_view errorMessage) {
    if (check(type)) return advance();
    error(errorMessage);
}

void Parser::consumeStatementEnd() {
    if (!match(TokenType::Newline) && !check(TokenType::Dedent) && !check(TokenType::EndOfFile)) {
        error("expected newline or end of block");
    }
}

// --- Error Handling ---

void Parser::error(const std::string_view message) const {
    const auto& t = peek();
    if (t.type == TokenType::EndOfFile) {
        throw SyntaxError("unexpected EOF while parsing", t.line, t.column);
    }
    throw SyntaxError(std::string(message), t.line, t.column);
}

void Parser::indentError(const std::string_view message) const {
    const auto& t = peek();
    throw IndentationError(std::string(message), t.line, t.column);
}

// --- Grammar Implementation ---

std::unique_ptr<Program> Parser::parseProgram() {
    auto prog = std::make_unique<Program>();

    while (!check(TokenType::EndOfFile)) {
        if (check(TokenType::Def)) {
            prog->functions.push_back(parseFunction());
        }
        else if (match(TokenType::Newline)) {
            // Ignore empty lines between functions
            continue;
        }
        else {
            error("Expected function definition ('def') or End of File");
        }
    }
    return prog;
}

std::unique_ptr<FunctionDef> Parser::parseFunction() {
    consume(TokenType::Def, "invalid syntax");

    const Token nameToken = consume(TokenType::Identifier, "expected function name");

    consume(TokenType::LParen, "expected '('");
    consume(TokenType::RParen, "expected ')'");
    consume(TokenType::Colon, "expected ':'");

    if (!match(TokenType::Newline)) {
        error("invalid syntax");
    }

    auto func = std::make_unique<FunctionDef>();
    func->name = nameToken.value;
    func->body = parseBlock();

    return func;
}

std::vector<std::unique_ptr<Statement>> Parser::parseBlock() {
    std::vector<std::unique_ptr<Statement>> statements;

    if (!check(TokenType::Indent)) {
        indentError("expected an indented block");
    }

    advance();

    while (!check(TokenType::Dedent) && !check(TokenType::EndOfFile)) {
        // Skip empty lines inside blocks
        if (match(TokenType::Newline)) continue;

        statements.push_back(parseStatement());
    }

    if (!match(TokenType::Dedent) && !check(TokenType::EndOfFile)) {
        indentError("unindent does not match any outer indentation level");
    }
    return statements;
}

std::unique_ptr<Statement> Parser::parseStatement() {
    // 1. Return Statement
    if (check(TokenType::Return)) {
        return parseReturnStatement();
    }

    // 2. Future statements (If, While, VarDecl) will go here
    // if (check(TokenType::If)) return parseIfStatement();

    error("invalid syntax");
    return nullptr;
}

std::unique_ptr<Statement> Parser::parseReturnStatement() {
    consume(TokenType::Return, "invalid syntax");

    std::unique_ptr<Expression> value = nullptr;

    if (!check(TokenType::Newline) && !check(TokenType::Dedent) && !check(TokenType::EndOfFile)) {
        value = parseExpression();
    }

    consumeStatementEnd();

    return std::make_unique<ReturnStmt>(std::move(value));
}

std::unique_ptr<Expression> Parser::parseExpression() {
    return parsePrimary();
}

std::unique_ptr<Expression> Parser::parsePrimary() {
    if (match(TokenType::Number)) {
        const Token t = tokens[pos-1];
        std::string text = t.value;
        std::erase(text, '_');

        int base = 10;
        size_t offset = 0;

        if (text.size() >= 2 && text[0] == '0') {
            if (const char prefix = std::tolower(text[1]); prefix == 'x') {
                base = 16;
                offset = 2;
            }
            else if (prefix == 'b') {
                base = 2;
                offset = 2;
            }
            else if (prefix == 'o') {
                base = 8;
                offset = 2;
            }
        }

        try {
            int val = std::stoi(text.substr(offset), nullptr, base);
            return std::make_unique<NumberExpr>(val);
        }
        catch (const std::out_of_range&) {
            error(std::format("integer literal is too large: '{}'", t.value));
        }
        catch (const std::invalid_argument&) {
            error(std::format("invalid integer literal: '{}'", t.value));
        }
        return nullptr;
    }

    error("Expected expression");
    return nullptr;
}