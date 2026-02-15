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

#ifndef LEXER_H
#define LEXER_H

#pragma once

#include <string>
#include <string_view>
#include <vector>

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

  Token string();

  Token scan_token();
};

#endif  // LEXER_H