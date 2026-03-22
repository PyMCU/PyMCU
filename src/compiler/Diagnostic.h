/*
 * -----------------------------------------------------------------------------
 * Whipsnake Compiler (whipc)
 * Copyright (C) 2026 Ivan Montiel Cardona and the Whipsnake Project Authors
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

#ifndef DIAGNOSTIC_H
#define DIAGNOSTIC_H

#include <format>
#include <iostream>
#include <sstream>
#include <string_view>
#include <vector>

#include "Errors.h"

class Diagnostic {
 public:
  /// Maps CompilerError type_name to a VS Code severity string.
  static std::string_view severity_for(const std::string &type_name) {
    if (type_name == "Warning") return "warning";
    if (type_name == "Info" || type_name == "Note") return "info";
    return "error";
  }

  /// Emits a machine-readable diagnostic line for VS Code problem matcher,
  /// followed by human-readable context (source line + caret).
  /// Format: file:line:column: severity: ErrorType: message
  static void report(const CompilerError &err, const std::string_view source,
                     std::string_view filename) {
    // Machine-readable line (matched by $whipc problem matcher)
    std::cerr << std::format("{}:{}:{}: {}: {}: {}\n", filename, err.line,
                             std::max(err.column, 1), severity_for(err.type_name),
                             err.type_name, err.what());

    // Human-readable context
    if (const std::string line_content = get_line(source, err.line);
        !line_content.empty()) {
      std::cerr << "    " << line_content << "\n";
      if (err.column > 0) {
        const std::string pointer(err.column + 4 - 1, ' ');
        std::cerr << pointer << "^\n";
      }
    }
  }

  /// Overload for internal compiler errors (no source location).
  static void report_internal(const std::string &message,
                              std::string_view filename) {
    std::cerr << std::format("{}:1:1: error: InternalCompilerError: {}\n",
                             filename, message);
  }

 private:
  static std::string get_line(const std::string_view src,
                              const int target_line) {
    int current = 1;
    size_t start = 0;
    for (size_t i = 0; i < src.size(); ++i) {
      if (src[i] == '\n') {
        if (current == target_line)
          return std::string(src.substr(start, i - start));
        current++;
        start = i + 1;
      }
    }
    if (current == target_line) return std::string(src.substr(start));
    return "";
  }
};

#endif