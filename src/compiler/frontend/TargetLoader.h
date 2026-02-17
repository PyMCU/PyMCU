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

#ifndef TARGET_LOADER_H
#define TARGET_LOADER_H

#pragma once
#include <filesystem>
#include <map>
#include <memory>
#include <string>
#include <vector>

#include "../common/DeviceConfig.h"
#include "Ast.h"

// TargetLoader: Phase 0 of the compilation pipeline.
//
// Resolves a chip name (e.g., "pic16f18877") to its definition file
// (pymcu/chips/pic16f18877.py), parses it, and extracts metadata via the
// device_info() hook. This populates the DeviceConfig BEFORE any user code
// is parsed, ensuring __CHIP__ is available for Dead Code Elimination in
// library modules like gpio.py.
//
// Pipeline position:
//   Phase 0: TargetLoader::bootstrap()     ← HERE
//   Phase 1: load_imports_recursively()
//   Phase 2: PreScanVisitor (user modules)
//   Phase 3: ConditionalCompilator
//   Phase 4: Final import resolution
//
class TargetLoader {
 public:
  struct Result {
    DeviceConfig config;
    // The chip module's dot-qualified name (e.g., "pymcu.chips.pic16f18877")
    std::string module_name;
    // The resolved filesystem path to the chip definition file
    std::string file_path;
    // Parsed AST of the chip module (ownership transferred to caller)
    std::unique_ptr<Program> ast;
    // Source lines for diagnostics
    std::vector<std::string> source_lines;
  };

  // Resolve the chip definition file within the include paths.
  // Searches for pymcu/chips/{chip_name}.py in each -I directory.
  // Returns the absolute path, or throws if not found.
  static std::string resolve_chip_module(
      const std::string &chip_name,
      const std::vector<std::string> &include_paths);

  // Full bootstrap: resolve → parse → extract device_info().
  // This is the main entry point called from main.cpp Phase 0.
  //
  // The returned Result contains:
  //   - config: Populated DeviceConfig (arch, ram_size, etc.)
  //   - ast: Parsed AST ready to be inserted into CompilerContext
  //   - module_name: For registration in named_modules map
  //   - source_lines: For diagnostic output
  static Result bootstrap(const std::string &chip_name,
                          const std::vector<std::string> &include_paths);
};

#endif  // TARGET_LOADER_H
