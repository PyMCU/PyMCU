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
