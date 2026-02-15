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
#include "ir/IRGenerator.h"
#include "ir/Optimizer.h"
#include "ir/Tacky.h"

namespace fs = std::filesystem;

struct CompilerContext {
  std::map<std::string, std::unique_ptr<Program> > module_cache;
  std::vector<const Program *> linear_imports;
  std::map<std::string, const Program *> named_modules;
  std::set<std::string> loading_modules;
  DeviceConfig config;
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
      if (imp->module_name == "pymcu.types") {
        // Intrinsics
        continue;
      }

      path = resolve_module(imp->module_name, includes, current_path,
                            imp->relative_level);

      if (ctx->module_cache.contains(path)) continue;

      if (ctx->loading_modules.contains(path)) continue;

      std::cout << "Loading module: " << path << "\n";

      ctx->loading_modules.insert(path);

      std::string src = read_source(path);
      Lexer l(src);
      const auto tokens = l.tokenize();
      Parser p(tokens);
      auto mod_ast = p.parseProgram();

      // Run PreScanVisitor on chip definition modules (pymcu.chips.*)
      // to extract device_info() configuration (arch, ram_size, etc.)
      if (imp->module_name.find("pymcu.chips.") == 0) {
        PreScanVisitor chip_scanner(ctx->config);
        chip_scanner.scan(*mod_ast);
      }

      load_imports_recursively(mod_ast.get(), ctx, path, includes);

      auto &inserted_ptr = ctx->module_cache[path] = std::move(mod_ast);
      ctx->linear_imports.push_back(inserted_ptr.get());
      ctx->named_modules[imp->module_name] = inserted_ptr.get();

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

  try {
    program.parse_args(argc, argv);
  } catch (const std::exception &err) {
    std::cerr << err.what() << std::endl;
    return 1;
  }

  const auto filepath = program.get<std::string>("file");
  const auto arch = program.get<std::string>("--arch");
  auto output_path = program.get<std::string>("--output");

  DeviceConfig device_config;
  device_config.frequency = program.get<unsigned long>("--freq");

  // Only pollute config with CLI arch if explicitly provided by user.
  // Otherwise, let PreScan detect from source first.
  if (program.is_used("--arch")) {
    const auto arch = program.get<std::string>("--arch");
    device_config.target_chip = arch;  // Source of Truth
  }

  device_config.chip = "";  // Default empty, populated by PreScan

  CompilerContext context;
  // context.config will be set after PreScan

  if (program.is_used("-C")) {
    for (auto config_list = program.get<std::vector<std::string> >("-C");
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

  std::vector<std::string> source_lines;  // Move to outer scope
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
    include_paths = program.get<std::vector<std::string> >("-I");
  }
  include_paths.emplace_back(".");
  if (auto p = fs::path(filepath).parent_path(); !p.empty()) {
    include_paths.push_back(p.string());
  }

  try {
    Lexer lexer(source);
    const auto tokens = lexer.tokenize();

    Parser parser(tokens);
    const auto ast = parser.parseProgram();

    // Configuration Bootstrap (PreScan)
    PreScanVisitor pre_scanner(device_config);
    pre_scanner.scan(*ast);
    context.config = device_config;

    // Resolve Target Chip for Conditional Compilation
    std::string active_chip = device_config.chip;
    if (active_chip.empty()) active_chip = arch;

    // Conditional Compilation (Pre-Process)
    // Only runs on the main file to unwrap __CHIP__ blocks
    ConditionalCompilator conditional(active_chip);
    conditional.process(*ast);

    load_imports_recursively(ast.get(), &context, fs::path(filepath),
                             include_paths);

    // IR Generation
    IRGenerator ir_gen;
    auto ir = ir_gen.generate(*ast, context.named_modules, source_lines);

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

    std::ofstream asm_file(output_path);
    if (!asm_file.is_open()) {
      throw std::runtime_error("Cannot open output file: " + output_path);
    }

    std::cout << "[pymcuc] Compiling " << filepath << " -> " << output_path
              << " (" << arch << " @ " << device_config.frequency << "Hz)\n";

    backend->compile(ir, asm_file);

    std::cout << "[pymcuc] Success! Output written to " << output_path << "\n";
  } catch (const CompilerError &e) {
    std::cerr << "Error in " << filepath << ":" << e.line << ": " << e.what()
              << "\n";
    // Print the line
    if (e.line > 0 && e.line <= static_cast<int>(source_lines.size())) {
      std::cerr << "    " << source_lines[e.line - 1] << "\n";
      // Simple pointer to column if valid
      if (e.column > 0) {
        std::string pointer(e.column - 1, ' ');
        pointer += "^";
        std::cerr << "    " << pointer << "\n";
      }
    }
    return 1;
  } catch (const std::exception &e) {
    std::cerr << "Internal Compiler Error: " << e.what() << "\n";
    return 1;
  }

  return 0;
}
