//
// Created by Ivan Montiel Cardona on 08/02/26.
//

#ifndef PARSER_H
#define PARSER_H

#pragma once
#include <vector>
#include <memory>
#include <string_view>
#include "Ast.h"
#include "Token.h"

class Parser {
public:
    explicit Parser(const std::vector<Token>& tokens);

    // Punto de entrada principal
    std::unique_ptr<Program> parseProgram();

private:
    const std::vector<Token>& tokens;
    size_t pos = 0;

    // --- Navigation Helpers ---
    [[nodiscard]] const Token& peek() const;
    Token advance();
    [[nodiscard]] bool check(TokenType type) const;
    bool match(TokenType type);

    // Returns the token if consumed, throws if not
    Token consume(TokenType type, std::string_view errorMessage);
    void consumeStatementEnd();

    [[noreturn]] void error(std::string_view message) const;
    void indentError(std::string_view message) const;

    // --- Grammar Rules ---

    std::unique_ptr<FunctionDef> parseFunction();
    std::vector<std::unique_ptr<Statement>> parseBlock();
    std::unique_ptr<Statement> parseStatement();
    std::unique_ptr<Statement> parseReturnStatement();
    // Future: std::unique_ptr<Statement> parseIfStatement();
    // Future: std::unique_ptr<Statement> parseAssignment();
    std::unique_ptr<Expression> parseExpression();
    std::unique_ptr<Expression> parsePrimary();
};

#endif //PARSER_H