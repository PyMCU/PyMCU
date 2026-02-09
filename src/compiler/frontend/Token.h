#ifndef TOKEN_H
#define TOKEN_H
#pragma once
#include <string>
#include <format>

enum class TokenType {
    // Keywords
    Def, Return,

    // Literals
    Identifier, Number,

    // Symbols
    Colon,      // :
    LParen,     // (
    RParen,     // )

    // Structure Control (CRITICAL for Python-like)
    Newline,    // \n (End of statement)
    Indent,     // Block starts (virtual)
    Dedent,     // Block ends (virtual)

    EndOfFile,
    Unknown
};

struct Token {
    TokenType type;
    std::string value;
    int line;
    int column; // Useful for indentation errors
};
#endif