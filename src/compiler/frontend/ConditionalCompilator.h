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

#ifndef CONDITIONAL_H
#define CONDITIONAL_H

#include <string>
#include <vector>

#include "../common/DeviceConfig.h"
#include "Ast.h"

class ConditionalCompilator {
 public:
  explicit ConditionalCompilator(DeviceConfig config);
  void process(Program& program);

 private:
  DeviceConfig config;

  void process_block(Statement* block, Program& prog);

  // Returns true if the statement was an IfStmt handled (and thus should be
  // removed/replaced) Returns false if it's a normal statement to keep
  bool process_statement(const Statement* stmt, Program& prog,
                         std::vector<std::unique_ptr<Statement>>& new_globals);

  // Evaluates a condition. Only supports: __CHIP__ == "literal"
  bool evaluate_condition(const Expression* expr);

  // Evaluates a match target to a string. Only supports: __CHIP__.name, etc.
  std::string evaluate_match_target(const Expression* expr);
};

#endif
