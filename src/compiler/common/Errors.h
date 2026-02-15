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

#ifndef ERRORS_H
#define ERRORS_H

#pragma once
#include <stdexcept>
#include <string>
#include <utility>

// Base class for all compilation errors
class CompilerError : public std::runtime_error {
 public:
  int line;
  int column;
  std::string type_name;  // "SyntaxError", "IndentationError"

  CompilerError(std::string name, const std::string &msg, const int l,
                const int c)
      : std::runtime_error(msg),
        line(l),
        column(c),
        type_name(std::move(name)) {}
};

// Python: SyntaxError
class SyntaxError : public CompilerError {
 public:
  SyntaxError(const std::string &msg, const int l, const int c)
      : CompilerError("SyntaxError", msg, l, c) {}
};

// Python: IndentationError
class IndentationError : public CompilerError {
 public:
  IndentationError(const std::string &msg, const int l, const int c)
      : CompilerError("IndentationError", msg, l, c) {}
};

class LexicalError : public CompilerError {
 public:
  LexicalError(const std::string &msg, const int l, const int c)
      : CompilerError("LexicalError", msg, l, c) {}
};

#endif  // ERRORS_H
