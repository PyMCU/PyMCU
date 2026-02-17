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
    // Machine-readable line (matched by $pymcuc problem matcher)
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