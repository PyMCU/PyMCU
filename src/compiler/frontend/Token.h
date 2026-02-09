#ifndef TOKEN_H
#define TOKEN_H

#pragma once
#include <string>

enum class TokenType {
    // Keywords
    Def, Return, If, Elif, Else, While, For, In, Break, Continue, Pass,
    Import, From, As,
    True, False, None,

    // Literals
    Identifier, Number, String,

    // Structure & Types
    Colon,      // :
    Comma,      // ,
    Dot,        // .
    Arrow,      // ->
    LParen,     // (
    RParen,     // )
    LBracket,   // [
    RBracket,   // ]

    // Operators
    Plus,       // +
    Minus,      // -
    Star,       // *
    Slash,      // /
    Percent,    // %

    // Bitwise
    Ampersand,  // &
    Pipe,       // |
    Caret,      // ^
    Tilde,      // ~
    LShift,     // <<
    RShift,     // >>

    // Comparison & Assignment
    Equal,      // =
    EqualEqual, // ==
    BangEqual,  // !=
    Less,       // <
    LessEqual,  // <=
    Greater,    // >
    GreaterEqual,// >=

    // Control
    Newline,    // \n
    Indent,
    Dedent,
    EndOfFile,
    Unknown
};

struct Token {
    TokenType type;
    std::string value;
    int line;
    int column;
};
#endif