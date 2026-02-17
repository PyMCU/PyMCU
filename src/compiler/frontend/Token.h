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

  // Literals
  Identifier,
  Number,
  String,

  // Structure & Types
  Colon,      // :
  Semicolon,  // ;
  Comma,      // ,
  Dot,        // .
  Arrow,      // ->
  LParen,     // (
  RParen,     // )
  LBracket,   // [
  RBracket,   // ]

  // Operators
  Plus,     // +
  Minus,    // -
  Star,     // *
  Slash,    // /
  Percent,  // %

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
  PlusEqual,     // +=
  MinusEqual,    // -=
  StarEqual,     // *=
  SlashEqual,    // /=
  PercentEqual,  // %=
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