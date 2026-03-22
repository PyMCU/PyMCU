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
// (whipsnake/chips/pic16f18877.py), parses it, and extracts metadata via the
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
    // The chip module's dot-qualified name (e.g., "whipsnake.chips.pic16f18877")
    std::string module_name;
    // The resolved filesystem path to the chip definition file
    std::string file_path;
    // Parsed AST of the chip module (ownership transferred to caller)
    std::unique_ptr<Program> ast;
    // Source lines for diagnostics
    std::vector<std::string> source_lines;
  };

  // Resolve the chip definition file within the include paths.
  // Searches for whipsnake/chips/{chip_name}.py in each -I directory.
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
