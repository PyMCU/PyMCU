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

#include "TargetLoader.h"

#include <iostream>
#include <sstream>

#include "../common/Errors.h"
#include "../common/Utils.h"
#include "Lexer.h"
#include "Parser.h"
#include "PreScanVisitor.h"

namespace fs = std::filesystem;

std::string TargetLoader::resolve_chip_module(
    const std::string &chip_name,
    const std::vector<std::string> &include_paths) {
  // Convert chip name to path: pic16f18877 → pymcu/chips/pic16f18877.py
  fs::path rel_path = fs::path("pymcu") / "chips" / (chip_name + ".py");

  for (const auto &base : include_paths) {
    fs::path candidate = fs::path(base) / rel_path;
    if (fs::exists(candidate)) {
      return fs::canonical(candidate).string();
    }
  }

  throw std::runtime_error(
      "Chip definition not found: " + chip_name +
      "\nSearched for: " + rel_path.string() +
      "\nIn directories: " +
      [&]() {
        std::string dirs;
        for (const auto &p : include_paths) {
          if (!dirs.empty()) dirs += ", ";
          dirs += p;
        }
        return dirs;
      }());
}

TargetLoader::Result TargetLoader::bootstrap(
    const std::string &chip_name,
    const std::vector<std::string> &include_paths) {
  Result result;
  result.module_name = "pymcu.chips." + chip_name;

  // Step 1: Resolve filesystem path
  result.file_path = resolve_chip_module(chip_name, include_paths);

  std::cout << "[TargetLoader] Loading chip: " << chip_name << " from "
            << result.file_path << "\n";

  // Step 2: Read and tokenize
  std::string source = read_source(result.file_path);

  {
    std::istringstream stream(source);
    std::string line;
    while (std::getline(stream, line)) {
      result.source_lines.push_back(line);
    }
  }

  Lexer lexer(source);
  auto tokens = lexer.tokenize();

  // Step 3: Parse into AST
  Parser parser(tokens);
  result.ast = parser.parseProgram();

  // Step 4: Extract device_info() metadata via PreScanVisitor
  // This intercepts the device_info(chip=..., arch=..., ram_size=...) call
  // and populates the DeviceConfig struct without generating any code.
  PreScanVisitor scanner(result.config);
  scanner.scan(*result.ast);

  // Step 5: Validate — the chip file MUST contain device_info()
  if (result.config.arch.empty()) {
    throw std::runtime_error(
        "Chip definition '" + chip_name +
        "' does not contain a valid device_info() call. "
        "Expected: device_info(chip=\"" +
        chip_name + "\", arch=\"...\", ram_size=...)");
  }

  std::cout << "[TargetLoader] Target: " << result.config.chip
            << " (arch=" << result.config.arch
            << ", RAM=" << result.config.ram_size << ")\n";

  return result;
}
