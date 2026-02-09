#ifndef LEXER_H
#define LEXER_H

#pragma once

#include <string_view>
#include <vector>
#include <string>
#include "Token.h"

class Lexer {
public:
    explicit Lexer(std::string_view source);
    std::vector<Token> tokenize();
private:
    std::string_view src;
    size_t pos = 0;
    int line = 1;
    int column = 1;

    std::vector<int> indent_stack;
    std::vector<Token> token_queue;
    bool at_line_start = true;

    [[nodiscard]] char peek() const;
    [[nodiscard]] char peek_next() const;
    char advance();
    bool match(char expected);

    void handle_indentation();
    void skip_whitespace();
    void skip_comment();

    [[noreturn]] void error(std::string_view message) const;

    Token number();
    Token identifier();
    Token scan_token();
};

#endif //LEXER_H