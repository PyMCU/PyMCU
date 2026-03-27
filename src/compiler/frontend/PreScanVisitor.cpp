/*
 * -----------------------------------------------------------------------------
 * PyMCU Compiler (pymcuc)
 * Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
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

#include "PreScanVisitor.h"

#include <iostream>
#include <stdexcept>

#include "../common/Errors.h"
#include "Ast.h"

PreScanVisitor::PreScanVisitor(DeviceConfig& config) : config(config) {}

void PreScanVisitor::scan(const Program& program) const {
  for (const auto& stmt : program.global_statements) {
    visit_statement(stmt.get());
  }
}

void PreScanVisitor::visit_statement(const Statement* stmt) const {
  if (const auto exprStmt = dynamic_cast<const ExprStmt*>(stmt)) {
    if (const auto call = dynamic_cast<const CallExpr*>(exprStmt->expr.get())) {
      if (const auto var =
              dynamic_cast<const VariableExpr*>(call->callee.get())) {
        if (var->name == "device_info") {
          handle_device_info(call);
        }
      }
    }
  }
}

void PreScanVisitor::handle_device_info(const CallExpr* call) const {
  int positional_index = 0;

  for (const auto& arg : call->args) {
    std::string key;
    const Expression* valueExpr = nullptr;

    if (const auto kw = dynamic_cast<const KeywordArgExpr*>(arg.get())) {
      key = kw->key;
      valueExpr = kw->value.get();
    } else {
      switch (positional_index) {
        case 0:
          key = "arch";
          break;
        case 1:
          key = "chip";
          break;
        case 2:
          key = "ram_size";
          break;
        case 3:
          key = "flash_size";
          break;
        case 4:
          key = "eeprom_size";
          break;
        default:
          throw CompilerError("ConfigError",
                              "Too many positional arguments in device_info",
                              call->line, 0);
      }
      valueExpr = arg.get();
      positional_index++;
    }

    if (key == "chip") {
      if (const auto lit = dynamic_cast<const StringLiteral*>(valueExpr)) {
        std::string parsed_chip = lit->value;
        config.detected_chip = parsed_chip;
        config.chip = parsed_chip;

        // Validation: CLI/TOML vs source code chip
        // Source code device_info() takes precedence over CLI --arch,
        // since the code was written for a specific chip.
        if (!config.target_chip.empty() && config.target_chip != parsed_chip) {
          std::cerr << "[Warning] Build specifies '" << config.target_chip
                    << "', but code imports '" << parsed_chip
                    << "'. Using source code chip.\n";
          config.target_chip = parsed_chip;
        }

        // Auto-detect architecture if not already set
        if (config.arch.empty()) {
          if (parsed_chip.find("pic16f1") == 0 && parsed_chip.length() >= 9) {
            config.arch = "pic14e";
          } else {
            config.arch = "pic14";
          }
        }
      } else {
        throw CompilerError("ConfigError", "chip must be a string literal",
                            call->line, 0);
      }
    } else if (key == "arch") {
      if (const auto lit = dynamic_cast<const StringLiteral*>(valueExpr)) {
        config.arch = lit->value;
      } else {
        throw CompilerError("ConfigError", "arch must be a string literal",
                            call->line, 0);
      }
    } else if (key == "ram_size") {
      if (const auto lit = dynamic_cast<const IntegerLiteral*>(valueExpr)) {
        config.ram_size = lit->value;
      }
      // Non-literal (e.g., variable reference) is allowed; value will be 0
    } else if (key == "flash_size") {
      if (const auto lit = dynamic_cast<const IntegerLiteral*>(valueExpr)) {
        config.flash_size = lit->value;
      }
    } else if (key == "eeprom_size") {
      if (const auto lit = dynamic_cast<const IntegerLiteral*>(valueExpr)) {
        config.eeprom_size = lit->value;
      }
    }
  }

  std::cout << "[PreScan] Configured device: " << config.chip
            << " (Arch: " << config.arch << ")"
            << " (RAM: " << config.ram_size << ", Flash: " << config.flash_size
            << ")\n";
}
