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

#include <argparse/argparse.hpp>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <map>
#include <set>
#include <sstream>
#include <vector>

#include "DeviceConfig.h"
#include "Diagnostic.h"
#include "Errors.h"
#include "Utils.h"
#include "backend/CodeGenFactory.h"
#include "frontend/ConditionalCompilator.h"
#include "frontend/Lexer.h"
#include "frontend/Parser.h"
#include "frontend/PreScanVisitor.h"
#include "frontend/TargetLoader.h"
#include "ir/IRGenerator.h"
#include "ir/Optimizer.h"
#include "ir/Tacky.h"

namespace fs = std::filesystem;

struct CompilerContext {
  std::map<std::string, std::unique_ptr<Program>> module_cache;
  std::vector<const Program *> linear_imports;
  std::map<std::string, const Program *> named_modules;
  std::set<std::string> loading_modules;
  DeviceConfig config;
  std::map<std::string, std::vector<std::string>> module_source_lines;
};

std::string resolve_module(const std::string &module_name,
                           const std::vector<std::string> &include_paths,
                           const fs::path &current_file_path,
                           const int relative_level) {
  std::string path_rel = module_name;
  std::ranges::replace(path_rel, '.', fs::path::preferred_separator);

  if (relative_level > 0) {
    fs::path search_dir = current_file_path.parent_path();

    for (int i = 1; i < relative_level; ++i) {
      search_dir = search_dir.parent_path();
    }

    fs::path full_path = search_dir / (path_rel + ".py");

    if (fs::exists(full_path)) return full_path.string();

    full_path = search_dir / path_rel / "__init__.py";
    if (fs::exists(full_path)) return full_path.string();

    throw std::runtime_error("Relative import not found: " +
                             full_path.string());
  }

  for (const auto &base : include_paths) {
    fs::path full_path = fs::path(base) / (path_rel + ".py");
    if (fs::exists(full_path)) return full_path.string();

    full_path = fs::path(base) / path_rel / "__init__.py";
    if (fs::exists(full_path)) return full_path.string();
  }

  throw std::runtime_error("Module not found: " + module_name);
}

void load_imports_recursively(const Program *ast, CompilerContext *ctx,
                              const fs::path &current_path,
                              const std::vector<std::string> &includes) {
  for (const auto &imp : ast->imports) {
    std::string path;
    try {
      if (imp->module_name == "pymcu.types" ||
          imp->module_name == "pymcu.time" ||
          imp->module_name == "pymcu.chips") {
        // Intrinsic/compiler-provided modules — not real source files
        // pymcu.chips provides __CHIP__ which is injected by ConditionalCompilator
        continue;
      }

      path = resolve_module(imp->module_name, includes, current_path,
                            imp->relative_level);

      if (ctx->module_cache.contains(path)) continue;
      if (ctx->loading_modules.contains(path)) continue;

      std::cout << "Loading module: " << path << "\n";
      ctx->loading_modules.insert(path);

      std::string src = read_source(path);
      {
        std::istringstream stream(src);
        std::string line;
        std::vector<std::string> lines;
        while (std::getline(stream, line)) {
          lines.push_back(line);
        }
        ctx->module_source_lines[imp->module_name] = std::move(lines);
      }

      Lexer l(src);
      Parser p(l.tokenize());
      auto mod_ast = p.parseProgram();

      auto &inserted_ptr = ctx->module_cache[path] = std::move(mod_ast);
      ctx->named_modules[imp->module_name] = inserted_ptr.get();

      // Recurse
      load_imports_recursively(inserted_ptr.get(), ctx, path, includes);

      ctx->loading_modules.erase(path);
    } catch (const std::exception &e) {
      if (!path.empty()) ctx->loading_modules.erase(path);
      std::cerr << "Error importing '" << imp->module_name << "': " << e.what()
                << "\n";
      throw CompilerError("ImportError", "Failed to import module", imp->line,
                          0);
    }
  }
}

int main(int argc, char *argv[]) {
  argparse::ArgumentParser program("pymcuc");

  program.add_argument("file").help("Input source file");
  program.add_argument("-o", "--output")
      .default_value(std::string(""))
      .help("Output ASM file");
  program.add_argument("--arch").default_value(std::string("pic14"));

  program.add_argument("--chip")
      .help("Target chip (e.g., pic16f18877). Locates pymcu/chips/<chip>.py")
      .default_value(std::string(""));

  program.add_argument("--freq")
      .help("Clock frequency in Hz")
      .default_value(4000000UL)
      .scan<'u', unsigned long>();

  program.add_argument("-C", "--config")
      .help("Configuration bits (KEY=VALUE)")
      .append();

  program.add_argument("-I", "--include")
      .help("Add directory to search path for imports")
      .append();

  program.add_argument("--reset-vector")
      .help("Reset vector address (e.g., 0x2000 for bootloader)")
      .default_value(-1)
      .scan<'i', int>();

  program.add_argument("--interrupt-vector")
      .help("Interrupt vector address (e.g., 0x2008 for bootloader)")
      .default_value(-1)
      .scan<'i', int>();

  try {
    program.parse_args(argc, argv);
  } catch (const std::exception &err) {
    std::cerr << err.what() << std::endl;
    return 1;
  }

  const auto filepath = program.get<std::string>("file");
  const auto arch = program.get<std::string>("--arch");
  const auto chip_name = program.get<std::string>("--chip");
  auto output_path = program.get<std::string>("--output");

  DeviceConfig device_config;
  device_config.frequency = program.get<unsigned long>("--freq");
  device_config.reset_vector = program.get<int>("--reset-vector");
  device_config.interrupt_vector = program.get<int>("--interrupt-vector");

  // CLI --arch is the fallback; --chip + device_info() take precedence
  if (program.is_used("--arch")) {
    device_config.target_chip = arch;
  }

  CompilerContext context;

  if (program.is_used("-C")) {
    for (auto config_list = program.get<std::vector<std::string>>("-C");
         const auto &item : config_list) {
      if (size_t eq_pos = item.find('='); eq_pos != std::string::npos) {
        std::string key = item.substr(0, eq_pos);
        std::string val = item.substr(eq_pos + 1);
        device_config.fuses[key] = val;
      }
    }
  }

  if (output_path.empty()) {
    std::filesystem::path p(filepath);
    p.replace_extension(".asm");
    output_path = p.string();
  }

  std::vector<std::string> source_lines;
  std::string source;
  try {
    source = read_source(filepath);
    std::istringstream stream(source);
    std::string line;
    while (std::getline(stream, line)) {
      source_lines.push_back(line);
    }
  } catch (const std::exception &e) {
    std::cerr << "Fatal Error: " << e.what() << "\n";
    return 1;
  }

  std::vector<std::string> include_paths;
  if (program.is_used("-I")) {
    include_paths = program.get<std::vector<std::string>>("-I");
  }
  include_paths.emplace_back(".");
  if (auto p = fs::path(filepath).parent_path(); !p.empty()) {
    include_paths.push_back(p.string());
  }

  // =====================================================================
  // Phase 0: Target Bootstrap
  // If --chip is provided, locate and parse the chip definition file
  // BEFORE any user code. This front-loads __CHIP__ so DCE works in
  // library modules (gpio.py) from the very first pass.
  // =====================================================================
  if (!chip_name.empty()) {
    try {
      auto target = TargetLoader::bootstrap(chip_name, include_paths);

      // Merge extracted config (device_info() values take precedence)
      device_config.chip = target.config.chip;
      device_config.detected_chip = target.config.detected_chip;
      device_config.arch = target.config.arch;
      if (target.config.ram_size > 0)
        device_config.ram_size = target.config.ram_size;
      if (target.config.flash_size > 0)
        device_config.flash_size = target.config.flash_size;
      if (target.config.eeprom_size > 0)
        device_config.eeprom_size = target.config.eeprom_size;

      // Set target_chip for backend header emission (LIST P=, #include)
      if (device_config.target_chip.empty()) {
        device_config.target_chip = chip_name;
      }

      // Register the chip module in the context so it won't be re-loaded
      // when main.py's imports are resolved in Phase 1.
      context.module_source_lines[target.module_name] =
          std::move(target.source_lines);
      const auto *ast_ptr = target.ast.get();
      context.module_cache[target.file_path] = std::move(target.ast);
      context.named_modules[target.module_name] = ast_ptr;
    } catch (const std::exception &e) {
      std::cerr << "Target Error: " << e.what() << "\n";
      return 1;
    }
  }

  try {
    Lexer lexer(source);
    const auto tokens = lexer.tokenize();

    Parser parser(tokens);
    const auto ast = parser.parseProgram();

    // Pass 1: Recursive Import Loading (Initial dependencies)
    load_imports_recursively(ast.get(), &context, fs::path(filepath),
                             include_paths);

    // Pass 2: Global Configuration Bootstrap (PreScan all loaded modules)
    // Especially important for chip definition modules (pymcu.chips.*)
    PreScanVisitor pre_scanner(device_config);
    pre_scanner.scan(*ast);
    for (auto &[name, mod_ast] : context.named_modules) {
      pre_scanner.scan(*const_cast<Program *>(mod_ast));
    }
    context.config = device_config;

    // Resolve Target Chip for Conditional Compilation
    if (context.config.chip.empty()) context.config.chip = arch;
    if (context.config.arch.empty()) context.config.arch = arch;

    // Pass 3: Global Conditional Compilation (Pre-Process all modules)
    // Unwraps __CHIP__ blocks and moves local imports to top-level.
    ConditionalCompilator conditional(context.config);
    conditional.process(*ast);
    for (auto &[name, mod_ast] : context.named_modules) {
      conditional.process(*const_cast<Program *>(mod_ast));
    }

    // Pass 4: Final Recursive Load (In case Conditional Compilation uncovered
    // new imports)
    load_imports_recursively(ast.get(), &context, fs::path(filepath),
                             include_paths);
    for (auto &[name, mod_ast] : context.named_modules) {
      load_imports_recursively(mod_ast, &context, fs::path(filepath),
                               include_paths);
    }

    // IR Generation
    IRGenerator ir_gen;
    auto ir = ir_gen.generate(*ast, context.named_modules, device_config,
                              source_lines, context.module_source_lines);

    // ir = Optimizer::optimize(ir);

    // Propagate device_info from chip modules into device_config
    // (PreScan on chip modules may have populated config in the context)
    // Source code device_info() takes precedence over CLI --arch.
    if (!context.config.chip.empty()) {
      device_config.chip = context.config.chip;
    }
    if (!context.config.arch.empty()) {
      device_config.arch = context.config.arch;
    }
    if (!context.config.target_chip.empty()) {
      device_config.target_chip = context.config.target_chip;
    }
    if (context.config.ram_size > 0) {
      device_config.ram_size = context.config.ram_size;
    }

    // Determine target architecture priority: device_info(arch=) > chip name >
    // CLI Use the explicit architecture name (e.g., "pic14e") for
    // CodeGenFactory when available, since it's more precise than the chip
    // name.
    std::string target_arch = device_config.arch;
    if (target_arch.empty()) {
      target_arch = device_config.chip;
      if (target_arch.empty()) {
        target_arch = arch;
      }
    }

    // Ensure chip and arch are populated for backend usage
    if (device_config.chip.empty()) {
      device_config.chip = arch;
    }
    if (device_config.arch.empty()) {
      device_config.arch = arch;
    }

    // Ensure target_chip is set for header file emission (LIST P=, #include)
    if (device_config.target_chip.empty()) {
      device_config.target_chip = device_config.chip;
    }

    auto backend = CodeGenFactory::create(target_arch, device_config);

    // Create output directories if they don't exist
    auto output_parent = std::filesystem::path(output_path).parent_path();
    if (!output_parent.empty()) {
      std::filesystem::create_directories(output_parent);
    }

    std::ofstream asm_file(output_path);
    if (!asm_file.is_open()) {
      throw std::runtime_error("Cannot open output file: " + output_path);
    }

    std::cout << "[pymcuc] Compiling " << filepath << " -> " << output_path
              << " (" << target_arch << " @ " << device_config.frequency
              << "Hz)\n";

    backend->compile(ir, asm_file);

    std::cout << "[pymcuc] Success! Output written to " << output_path << "\n";
  } catch (const CompilerError &e) {
    Diagnostic::report(e, source, filepath);
    return 1;
  } catch (const std::exception &e) {
    Diagnostic::report_internal(e.what(), filepath);
    return 1;
  }

  return 0;
}
