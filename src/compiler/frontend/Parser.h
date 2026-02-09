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

    std::unique_ptr<Program> parseProgram();

private:
    const std::vector<Token>& tokens;
    size_t pos = 0;

    [[nodiscard]] const Token& peek() const;
    [[nodiscard]] const Token& previous() const;
    Token advance();

    [[nodiscard]] bool check(TokenType type) const;

    bool match(TokenType type);

    Token consume(TokenType type, std::string_view errorMessage);

    void consumeStatementEnd();

    [[noreturn]] void error(std::string_view message) const;
    void indentError(std::string_view message) const;

    std::string parseTypeAnnotation();

    std::unique_ptr<FunctionDef> parseFunction();

    std::vector<Param> parseParameters();

    std::unique_ptr<Block> parseBlock();

    std::unique_ptr<Statement> parseStatement();

    std::unique_ptr<Statement> parseIfStatement();
    std::unique_ptr<Statement> parseWhileStatement();

    std::unique_ptr<Statement> parseSimpleStatement();
    std::unique_ptr<Statement> parseReturnStatement();

    std::unique_ptr<Statement> parseAssignmentOrDeclaration();

    std::unique_ptr<Expression> parseExpression();     // Entry point
    std::unique_ptr<Expression> parseLogicalOr();      // or
    std::unique_ptr<Expression> parseLogicalAnd();     // and
    std::unique_ptr<Expression> parseBitwiseOr();      // |
    std::unique_ptr<Expression> parseBitwiseXor();     // ^
    std::unique_ptr<Expression> parseBitwiseAnd();     // &
    std::unique_ptr<Expression> parseEquality();       // ==, !=
    std::unique_ptr<Expression> parseRelational();     // <, >, <=, >=
    std::unique_ptr<Expression> parseShift();          // <<, >>
    std::unique_ptr<Expression> parseAdditive();       // +, -
    std::unique_ptr<Expression> parseMultiplicative(); // *, /, %
    std::unique_ptr<Expression> parseUnary();          // -, not, ~, !

    std::unique_ptr<Expression> parsePostfix();

    std::unique_ptr<Expression> parsePrimary();        // Literals, (Expr), Identifiers

    std::unique_ptr<ImportStmt> parseImportStatement();
};

#endif //PARSER_H