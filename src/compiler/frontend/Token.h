#ifndef TOKEN_H
#define TOKEN_H

#pragma once
#include <string>

enum class TokenType {
  // Keywords
  Def,
  Return,
  If,
  Elif,
  Else,
  While,
  For,
  In,
  Break,
  Continue,
  Pass,
  Match,
  Case,
  Import,
  From,
  As,
  True,
  False,
  None,
  Or,
  And,
  Not,
  Global,

  // Literals
  Identifier,
  Number,
  String,

  // Structure & Types
  Colon,     // :
  Semicolon, // ;
  Comma,     // ,
  Dot,       // .
  Arrow,     // ->
  LParen,    // (
  RParen,    // )
  LBracket,  // [
  RBracket,  // ]

  // Operators
  Plus,    // +
  Minus,   // -
  Star,    // *
  Slash,   // /
  Percent, // %

  // Bitwise
  Ampersand, // &
  Pipe,      // |
  Caret,     // ^
  Tilde,     // ~
  LShift,    // <<
  RShift,    // >>

  // Comparison & Assignment
  Equal,        // =
  EqualEqual,   // ==
  BangEqual,    // !=
  Less,         // <
  LessEqual,    // <=
  Greater,      // >
  GreaterEqual, // >=

  // Augmented Assignment
  PlusEqual,    // +=
  MinusEqual,   // -=
  StarEqual,    // *=
  SlashEqual,   // /=
  PercentEqual, // %=
  AmpEqual,     // &=
  PipeEqual,    // |=
  CaretEqual,   // ^=
  LShiftEqual,  // <<=
  RShiftEqual,  // >>=

  // Control
  Newline, // \n
  Indent,
  Dedent,
  EndOfFile,
  At, // @
  Unknown
};

struct Token {
  TokenType type;
  std::string value;
  int line;
  int column;
};
#endif
