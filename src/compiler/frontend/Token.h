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
  Class,
  Yield,
  Raise,
  With,    // T2.2
  Assert,  // T2.3
  Is,      // is / is not
  Lambda,  // lambda  — PEP 3 (F9)
  Nonlocal,// nonlocal — PEP 3104 (F10)

  // Literals
  Identifier,
  Number,
  String,
  FString,      // f"..." — raw interior (without f"..."), parsed into FStringExpr
  BytesLiteral, // b"..." — sequence of byte values, treated as uint8 list

  // Structure & Types
  Colon,      // :
  Walrus,     // :=
  Semicolon,  // ;
  Comma,      // ,
  Dot,        // .
  Arrow,      // ->
  LParen,     // (
  RParen,     // )
  LBracket,   // [
  RBracket,   // ]

  // Operators
  Plus,      // +
  Minus,     // -
  Star,      // *
  DoubleStar,  // **
  Slash,     // /
  FloorDiv,  // //
  Percent,   // %

  // Bitwise
  Ampersand,  // &
  Pipe,       // |
  Caret,      // ^
  Tilde,      // ~
  LShift,     // <<
  RShift,     // >>

  // Comparison & Assignment
  Equal,         // =
  EqualEqual,    // ==
  BangEqual,     // !=
  Less,          // <
  LessEqual,     // <=
  Greater,       // >
  GreaterEqual,  // >=

  // Augmented Assignment
  PlusEqual,         // +=
  MinusEqual,        // -=
  StarEqual,         // *=
  SlashEqual,        // /=
  FloorDivEqual,     // //=
  PercentEqual,      // %=
  AmpEqual,      // &=
  PipeEqual,     // |=
  CaretEqual,    // ^=
  LShiftEqual,   // <<=
  RShiftEqual,   // >>=

  // Control
  Newline,  // \n
  Indent,
  Dedent,
  EndOfFile,
  At,  // @
  Unknown
};

struct Token {
  TokenType type;
  std::string value;
  int line;
  int column;
};
#endif