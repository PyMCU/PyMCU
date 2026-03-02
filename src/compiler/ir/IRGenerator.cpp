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

#include "IRGenerator.h"

#include <iostream>
#include <stdexcept>
#include <typeinfo>

#include "../common/Utils.h"  // Not available consistently

/// Returns true if the type annotation represents a const type
/// (either bare "const" or parameterized "const[uint8]", etc.)
static bool is_const_type(const std::string &type) {
  return type == "const" || (type.find("const[") == 0 && type.back() == ']');
}

tacky::Temporary IRGenerator::make_temp(DataType type) {
  return tacky::Temporary{"tmp_" + std::to_string(temp_counter++), type};
}

std::string IRGenerator::make_label() {
  return "L_" + std::to_string(label_counter++);
}

void IRGenerator::emit(const tacky::Instruction &inst) {
  current_instructions.push_back(inst);
}

tacky::Program IRGenerator::generate(
    const Program &main_ast,
    const std::map<std::string, const Program *> &imported_modules,
    const DeviceConfig &config,
    const std::vector<std::string> &source_lines,
    const std::map<std::string, std::vector<std::string>>
        &module_source_lines) {
  this->source_lines = source_lines;
  this->module_source_lines = module_source_lines;
  this->last_line = -1;
  this->current_source_file = "";
  tacky::Program ir_program;
  globals.clear();
  mutable_globals.clear();
  function_return_types.clear();
  function_params.clear();
  inline_functions.clear();
  inline_functions.clear();
  modules.clear();
  functions_to_compile.clear();
  intrinsic_names.clear();

  // Always-available backend intrinsics (not tied to any import).
  // uart_send_string / uart_send_string_ln are called by the AVR UART HAL to
  // emit a UARTSendString IR instruction (flash string pool + LPM-Z loop).
  intrinsic_names.insert("uart_send_string");
  intrinsic_names.insert("uart_send_string_ln");

  // Inject compile-time constants from device configuration
  if (config.frequency > 0) {
    constant_variables["__FREQUENCY__"] = static_cast<int>(config.frequency);
  }

  // Register intrinsic names from pymcu.types imports
  for (const auto &imp : main_ast.imports) {
    if (imp->module_name == "pymcu.types") {
      intrinsic_names.insert("ptr");
      intrinsic_names.insert("const");
      intrinsic_names.insert("device_info");
      intrinsic_names.insert("inline");
      intrinsic_names.insert("interrupt");
      intrinsic_names.insert("asm");
    }
    // Record imported symbols for alias resolution
    for (const auto &sym : imp->symbols) {
      imported_aliases[sym] = imp->module_name;
    }
  }
  for (const auto &[mod_name, mod_ast] : imported_modules) {
    for (const auto &imp : mod_ast->imports) {
      if (imp->module_name == "pymcu.types") {
        intrinsic_names.insert("ptr");
        intrinsic_names.insert("const");
        intrinsic_names.insert("device_info");
        intrinsic_names.insert("inline");
        intrinsic_names.insert("interrupt");
        intrinsic_names.insert("asm");
      }
      // Register imported aliases for cross-module resolution
      // (e.g., gpio.py imports pin_set_mode from _gpio.atmega328p)
      for (const auto &sym : imp->symbols) {
        imported_aliases[sym] = imp->module_name;
      }
    }
  }

  // 1. Scan all module symbols first (two-pass approach)
  // This ensures all globals/functions are registered before any function
  // body is visited, preventing ordering issues with cross-module references
  // (e.g., nco.py referencing chip registers from pymcu.chips.pic16f18877).
  for (const auto &[mod_name, mod_ast] : imported_modules) {
    modules[mod_name] = ModuleScope{};
    current_module_prefix = mod_name + "_";
    std::replace(current_module_prefix.begin(), current_module_prefix.end(),
                 '.', '_');
    // Set source file context so FunctionEntry captures it
    auto dot_pos = mod_name.rfind('.');
    current_source_file =
        (dot_pos != std::string::npos ? mod_name.substr(dot_pos + 1)
                                      : mod_name) +
        ".py";
    scan_globals(*mod_ast, &modules[mod_name]);
    scan_functions(*mod_ast, &modules[mod_name]);
  }

  // 1.5. Resolve module-level imports
  // This ensures that 'from X import Y' statements populate the module's scope
  for (const auto &[mod_name, mod_ast] : imported_modules) {
    auto &scope = modules[mod_name];
    for (const auto &imp : mod_ast->imports) {
      if (modules.contains(imp->module_name)) {
        const auto &src_scope = modules.at(imp->module_name);
        for (const auto &sym : imp->symbols) {
          if (src_scope.globals.contains(sym)) {
            scope.globals[sym] = src_scope.globals.at(sym);
          } else if (src_scope.mutable_globals.contains(sym)) {
            scope.mutable_globals[sym] = src_scope.mutable_globals.at(sym);
          }
        }
      }
    }
  }

  // Also scan main script symbols before visiting any function bodies
  current_module_prefix = "";
  current_source_file = "main.py";
  scan_globals(main_ast);
  scan_functions(main_ast);

  // 1.6. Resolve main script imports (from X import Y)
  for (const auto &imp : main_ast.imports) {
    if (modules.contains(imp->module_name)) {
      const auto &src_scope = modules.at(imp->module_name);
      for (const auto &sym : imp->symbols) {
        if (src_scope.globals.contains(sym)) {
          globals[sym] = src_scope.globals.at(sym);
        } else if (src_scope.mutable_globals.contains(sym)) {
          mutable_globals[sym] = src_scope.mutable_globals.at(sym);
        }
      }
    }
  }

  // 1.7. Resolve class re-exports through __init__.py chains.
  // When module A (e.g. pymcu.hal) re-exports class C from module B
  // (e.g. pymcu.hal.gpio), create aliases for all C's methods so that
  // resolve_callee("C") → "A_prefix_C_method" finds the inline functions.
  for (const auto &[mod_name, mod_ast] : imported_modules) {
    std::string dst_prefix = mod_name;
    std::replace(dst_prefix.begin(), dst_prefix.end(), '.', '_');
    dst_prefix += "_";

    for (const auto &imp : mod_ast->imports) {
      if (!modules.contains(imp->module_name)) continue;

      std::string src_prefix = imp->module_name;
      std::replace(src_prefix.begin(), src_prefix.end(), '.', '_');
      src_prefix += "_";

      for (const auto &sym : imp->symbols) {
        std::string src_class_prefix = src_prefix + sym + "_";
        std::string dst_class_prefix = dst_prefix + sym + "_";

        // Collect entries to add (avoid mutating while iterating)
        std::vector<std::pair<std::string, const FunctionDef *>> inline_adds;
        for (const auto &[key, val] : inline_functions) {
          if (key.find(src_class_prefix) == 0) {
            std::string suffix = key.substr(src_class_prefix.size());
            inline_adds.emplace_back(dst_class_prefix + suffix, val);
          }
        }

        for (const auto &[new_key, func_ptr] : inline_adds) {
          std::string src_key =
              src_class_prefix + new_key.substr(dst_class_prefix.size());
          inline_functions[new_key] = func_ptr;
          if (function_params.contains(src_key))
            function_params[new_key] = function_params[src_key];
          if (function_return_types.contains(src_key))
            function_return_types[new_key] = function_return_types[src_key];
          if (function_param_types.contains(src_key))
            function_param_types[new_key] = function_param_types[src_key];
          if (method_instance_types.contains(src_key))
            method_instance_types[new_key] = method_instance_types[src_key];
        }

        // Also propagate class-level globals (e.g. Pin.OUTPUT)
        for (const auto &[key, val] : std::map<std::string, SymbolInfo>(globals)) {
          if (key.find(src_class_prefix) == 0) {
            std::string suffix = key.substr(src_class_prefix.size());
            globals[dst_class_prefix + suffix] = val;
          }
        }
      }
    }
  }

  // 2. Generate IR for all function bodies (modules + main)
  // All functions were registered in functions_to_compile by scan_functions
  for (const auto &entry : functions_to_compile) {
    current_module_prefix = entry.prefix;
    current_source_file = entry.source_file;
    if (!entry.func->is_inline) {
      ir_program.functions.push_back(visitFunction(entry.func));
    }
  }

  // Pass mutable global variable names and types to the backend for RAM
  // allocation
  for (const auto &[name, type] : mutable_globals) {
    ir_program.globals.push_back(tacky::Variable{name, type});
  }

  loop_stack.clear();
  return ir_program;
}

tacky::Val IRGenerator::resolve_binding(const std::string &name) {
  // Check if it's a known compile-time constant or memory-mapped register
  if (const auto it = globals.find(name); it != globals.end()) {
    if (it->second.is_memory_address) {
      return tacky::MemoryAddress{it->second.value, it->second.type};
    } else {
      return tacky::Constant{it->second.value};
    }
  }

  if (mutable_globals.contains(name)) {
    // If we are in a function, check if this variable is marked global
    if (!current_function.empty()) {
      bool is_explicit_global = current_function_globals.count(name);
      if (is_explicit_global) {
        return tacky::Variable{name, mutable_globals.at(name)};
      }

      // Check if shadowed by local
      std::string local_name = current_function + "." + name;
      if (constant_variables.contains(local_name)) {
        return tacky::Constant{constant_variables.at(local_name)};
      }
    }

    // Try current module prefix first — mutable_globals takes priority
    std::string module_global = current_module_prefix + name;
    if (mutable_globals.contains(module_global)) {
      return tacky::Variable{module_global, mutable_globals.at(module_global)};
    }
    if (constant_variables.contains(module_global)) {
      return tacky::Constant{constant_variables.at(module_global)};
    }

    // Fallback to bare name — mutable_globals takes priority
    if (mutable_globals.contains(name)) {
      return tacky::Variable{name, mutable_globals.at(name)};
    }
    if (constant_variables.contains(name)) {
      return tacky::Constant{constant_variables.at(name)};
    }
  }

  // Also check constant_variables directly for names that aren't in
  // mutable_globals (like flattened ones)
  if (!current_function.empty()) {
    std::string local_name = current_function + "." + name;
    if (constant_variables.contains(local_name)) {
      return tacky::Constant{constant_variables.at(local_name)};
    }
  }
  std::string module_global = current_module_prefix + name;
  if (constant_variables.contains(module_global)) {
    return tacky::Constant{constant_variables.at(module_global)};
  }
  if (constant_variables.contains(name)) {
    return tacky::Constant{constant_variables.at(name)};
  }

  // Check imports for global constants/addresses
  for (const auto &[mod_name, _] : modules) {
    std::string mangled_mod = mod_name;
    std::replace(mangled_mod.begin(), mangled_mod.end(), '.', '_');
    std::string mod_key = mangled_mod + "_" + name;
    if (globals.contains(mod_key)) {
      const auto &sym = globals.at(mod_key);
      if (sym.is_memory_address)
        return tacky::MemoryAddress{sym.value, sym.type};
      return tacky::Constant{sym.value};
    }
    if (mutable_globals.contains(mod_key)) {
      return tacky::Variable{mod_key, mutable_globals.at(mod_key)};
    }
  }

  std::string local_name;
  if (!current_inline_prefix.empty()) {
    local_name = current_inline_prefix + name;
  } else {
    local_name = current_function + "." + name;
  }

  // Check constant_variables for the inline-prefixed name.
  // This is critical for zero-cost abstractions: when a string literal
  // or integer constant is propagated through an inlined parameter,
  // it must resolve to Constant here to enable compile-time folding.
  if (constant_variables.contains(local_name)) {
    return tacky::Constant{constant_variables.at(local_name)};
  }

  DataType type = DataType::UINT8;
  if (variable_types.contains(local_name)) {
    type = variable_types.at(local_name);
  }
  return tacky::Variable{local_name, type};
}


// Resolves a variable name (possibly through the variable_aliases chain) to a
// compile-time string constant stored in str_constant_variables.  Returns
// std::nullopt if the name cannot be resolved to a string constant.
std::optional<std::string>
IRGenerator::resolve_str_constant(const std::string &name) const {
  std::string key = name;
  for (int depth = 0; depth < 20; ++depth) {
    if (str_constant_variables.count(key))
      return str_constant_variables.at(key);
    auto alias_it = variable_aliases.find(key);
    if (alias_it != variable_aliases.end()) {
      key = alias_it->second;
    } else {
      break;
    }
  }
  return std::nullopt;
}

std::string IRGenerator::resolve_callee(const std::string &name) {
  // If we have an explicit module dot (mod.func), just replace it
  if (size_t dot_pos = name.find('.'); dot_pos != std::string::npos) {
    std::string mod = name.substr(0, dot_pos);
    std::string func = name.substr(dot_pos + 1);
    // Use underscore for mangled name
    return mod + "_" + func;
  }

  // Intrinsics always resolve to themselves, regardless of import aliases
  if (intrinsic_names.contains(name)) {
    return name;
  }

  // Check if it's an imported alias
  if (imported_aliases.contains(name)) {
    std::string mod_name = imported_aliases.at(name);
    // Replace dots in module name with underscores
    std::string mangled_mod = mod_name;
    std::replace(mangled_mod.begin(), mangled_mod.end(), '.', '_');
    return mangled_mod + "_" + name;
  }

  // Check if it's already a mangled name in current module
  std::string local_mangled = current_module_prefix + name;
  if (inline_functions.contains(local_mangled) ||
      function_params.contains(local_mangled)) {
    return local_mangled;
  }

  // Fallback to bare name
  return name;
}

void IRGenerator::scan_globals(const Program &ast, ModuleScope *scope) {
  for (const auto &stmt : ast.global_statements) {
    std::string name;
    std::string type;
    const Expression *initializer = nullptr;

    if (const auto varDecl = dynamic_cast<const VarDecl *>(stmt.get())) {
      name = varDecl->name;
      type = varDecl->var_type;
      initializer = varDecl->init.get();
    } else if (const auto assign =
                   dynamic_cast<const AssignStmt *>(stmt.get())) {
      if (const auto varExpr =
              dynamic_cast<const VariableExpr *>(assign->target.get())) {
        name = varExpr->name;
        initializer = assign->value.get();
      }
    } else if (const auto annAssign =
                   dynamic_cast<const AnnAssign *>(stmt.get())) {
      name = annAssign->target;
      type = annAssign->annotation;  // Use annotation as type
      initializer = annAssign->value.get();
    } else if (const auto classDef =
                   dynamic_cast<const ClassDef *>(stmt.get())) {
      // Recursive scan for class static fields
      if (classDef->body) {
        std::string old_prefix = current_module_prefix;
        current_module_prefix += classDef->name + "_";

        if (const auto block =
                dynamic_cast<const Block *>(classDef->body.get())) {
          for (const auto &inner_stmt : block->statements) {
            std::string name;
            std::string type;
            const Expression *initializer = nullptr;

            if (const auto varDecl =
                    dynamic_cast<const VarDecl *>(inner_stmt.get())) {
              name = varDecl->name;
              type = varDecl->var_type;
              initializer = varDecl->init.get();
            } else if (const auto assign =
                           dynamic_cast<const AssignStmt *>(inner_stmt.get())) {
              if (const auto varExpr = dynamic_cast<const VariableExpr *>(
                      assign->target.get())) {
                name = varExpr->name;
                initializer = assign->value.get();
              }
            } else if (const auto annAssign =
                           dynamic_cast<const AnnAssign *>(inner_stmt.get())) {
              name = annAssign->target;
              type = annAssign->annotation;
              initializer = annAssign->value.get();
            }

            if (!name.empty() && initializer) {
              try {
                int val = evaluate_constant_expr(initializer);
                bool is_all_upper = true;
                for (char c : name) {
                  if (std::islower(c)) is_all_upper = false;
                }

                if (is_all_upper) {
                  globals[current_module_prefix + name] =
                      SymbolInfo{false, val};
                } else {
                  mutable_globals[current_module_prefix + name] =
                      resolve_type(type);
                }
              } catch (...) {
                mutable_globals[current_module_prefix + name] =
                    resolve_type(type);
              }
            }
          }
        }
        current_module_prefix = old_prefix;
      }
    }

    if (!name.empty() && initializer) {
      try {
        // Alias Resolution (ptr[...] = Identifier)
        if (const auto varExpr =
                dynamic_cast<const VariableExpr *>(initializer)) {
          // Check if we are aliasing an existing memory address constant in the
          // current scope
          const SymbolInfo *source_info = nullptr;
          std::string lookup_local = current_module_prefix + varExpr->name;

          if (globals.contains(lookup_local)) {
            source_info = &globals.at(lookup_local);
          } else {
            // Search imports
            for (const auto &[mod_name, _] : modules) {
              std::string mod_key = mod_name + "_" + varExpr->name;
              if (globals.contains(mod_key)) {
                source_info = &globals.at(mod_key);
                break;
              }
            }
          }

          if (source_info) {
            globals[current_module_prefix + name] = *source_info;
            continue;
          }
        }

        const int val = evaluate_constant_expr(initializer);
        bool is_memory_address = false;

        // If we have an initializer that is a call to ptr or PIORegister, it's
        // a memory address
        if (const auto call = dynamic_cast<const CallExpr *>(initializer)) {
          if (const auto var =
                  dynamic_cast<const VariableExpr *>(call->callee.get())) {
            if ((var->name == "ptr" && intrinsic_names.contains("ptr")) ||
                var->name == "PIORegister") {
              is_memory_address = true;
            }
          }
        }

        // Also check type hint
        if (!type.empty() && (type.find("ptr") != std::string::npos ||
                              type.find("PIORegister") != std::string::npos)) {
          is_memory_address = true;
        }

        if (is_memory_address) {
          SymbolInfo info{true, val, resolve_type(type)};
          globals[current_module_prefix + name] = info;
          if (scope) scope->globals[name] = info;
        } else {
          // Distinguish true constants (ALL_CAPS naming convention, e.g.
          // BUTTON_PIN) from mutable variables (lowercase/mixed case, e.g.
          // error_flags). Constants like BUTTON_PIN = 0 remain compile-time
          // constants. Mutable variables need RAM allocation.
          bool is_all_upper = true;
          for (char c : name) {
            if (std::islower(static_cast<unsigned char>(c))) {
              is_all_upper = false;
              break;
            }
          }
          if (is_all_upper) {
            SymbolInfo info{false, val};
            globals[current_module_prefix + name] = info;
            if (scope) scope->globals[name] = info;
          } else {
            // Mutable global: needs RAM, store initial value for later Copy
            DataType t = resolve_type(type);
            mutable_globals[current_module_prefix + name] = t;
            if (scope) scope->mutable_globals[name] = t;
          }
        }
      } catch (...) {
        // Non-constant initializer: this is a runtime variable, needs RAM
        DataType t = resolve_type(type);
        mutable_globals[current_module_prefix + name] = t;
        if (scope) scope->mutable_globals[name] = t;
      }
    }
  }
}
void IRGenerator::scan_functions(const Program &ast, ModuleScope *scope) {
  // 1. Top-level functions
  for (const auto &func : ast.functions) {
    std::string full_name = current_module_prefix + func->name;
    function_return_types[full_name] = func->return_type;
    std::vector<std::string> params;
    std::vector<DataType> param_types;
    for (const auto &p : func->params) {
      params.push_back(p.name);
      param_types.push_back(resolve_type(p.type));
    }
    function_params[full_name] = params;
    function_param_types[full_name] = param_types;

    if (scope) {
      scope->function_return_types[func->name] = func->return_type;
      scope->function_params[func->name] = params;
    }

    if (func->is_inline) {
      inline_functions[full_name] = func.get();
      if (scope) scope->inline_functions[func->name] = func.get();
    } else {
      functions_to_compile.push_back({current_module_prefix, func.get(), current_source_file});
    }
  }

  // 2. Class methods
  for (const auto &stmt : ast.global_statements) {
    if (const auto classDef = dynamic_cast<const ClassDef *>(stmt.get())) {
      if (classDef->body) {
        class_names.insert(classDef->name);
        std::string old_prefix = current_module_prefix;
        std::string class_prefix = current_module_prefix + classDef->name + "_";
        current_module_prefix = class_prefix;

        if (const auto block =
                dynamic_cast<const Block *>(classDef->body.get())) {
          for (const auto &inner : block->statements) {
            if (const auto func =
                    dynamic_cast<const FunctionDef *>(inner.get())) {
              std::string full_name = current_module_prefix + func->name;
              function_return_types[full_name] = func->return_type;
              std::vector<std::string> params;
              std::vector<DataType> param_types;
              for (const auto &p : func->params) {
                params.push_back(p.name);
                param_types.push_back(resolve_type(p.type));
              }
              function_params[full_name] = params;
              function_param_types[full_name] = param_types;

              if (func->is_inline) {
                inline_functions[full_name] = func;
              } else {
                functions_to_compile.push_back({current_module_prefix, func, current_source_file});
              }
              method_instance_types[full_name] = current_module_prefix.substr(
                  0, current_module_prefix.size() - 1);
            }
          }
        }
        current_module_prefix = old_prefix;
      }
    }
  }
}

int IRGenerator::evaluate_constant_expr(const Expression *expr) {
  if (const auto num = dynamic_cast<const IntegerLiteral *>(expr)) {
    return num->value;
  }

  if (const auto call = dynamic_cast<const CallExpr *>(expr)) {
    if (const auto var =
            dynamic_cast<const VariableExpr *>(call->callee.get())) {
      if (((var->name == "ptr" && intrinsic_names.contains("ptr")) ||
           var->name == "PIORegister") &&
          call->args.size() == 1) {
        return evaluate_constant_expr(call->args[0].get());
      }
      // const(value) — compile-time constant wrapper
      if (var->name == "const" && intrinsic_names.contains("const") &&
          call->args.size() == 1) {
        return evaluate_constant_expr(call->args[0].get());
      }
    }
  }

  if (const auto var = dynamic_cast<const VariableExpr *>(expr)) {
    // Try to resolve constant from globals
    std::string lookup = current_module_prefix + var->name;
    if (globals.contains(lookup)) {
      if (!globals.at(lookup).is_memory_address) {
        return globals.at(lookup).value;
      }
    }
    // Check Imports
    for (const auto &[mod_name, _] : modules) {
      std::string mod_key = mod_name + "_" + var->name;
      if (globals.contains(mod_key)) {
        if (!globals.at(mod_key).is_memory_address) {
          return globals.at(mod_key).value;
        }
      }
    }
  }

  throw std::runtime_error("Not a constant expression");
}

tacky::Function IRGenerator::visitFunction(const FunctionDef *funcNode) {
  tacky::Function ir_func;
  std::string full_name = current_module_prefix + funcNode->name;
  ir_func.name = full_name;
  current_function = full_name;

  // Copy inline flag from AST
  ir_func.is_inline = funcNode->is_inline;
  ir_func.is_interrupt = funcNode->is_interrupt;
  ir_func.interrupt_vector = funcNode->interrupt_vector;

  ir_func.interrupt_vector = funcNode->interrupt_vector;
  current_function_globals.clear();

  current_instructions.clear();
  loop_stack.clear();
  last_line = -1;  // Reset so first statement in function gets a debug line

  for (const auto &param : funcNode->params) {
    ir_func.params.push_back(current_function + "." + param.name);
  }

  visitBlock(funcNode->body.get());

  if (current_instructions.empty() ||
      !std::holds_alternative<tacky::Return>(current_instructions.back())) {
    emit(tacky::Return{std::monostate{}});
  }

  ir_func.body = current_instructions;
  return ir_func;
}

void IRGenerator::visitBlock(const Block *block) {
  for (const auto &stmt : block->statements) {
    visitStatement(stmt.get());
  }
}

void IRGenerator::visitStatement(const Statement *stmt) {
  // Track the current statement's source line for error reporting
  if (stmt->line > 0 && inline_depth == 0) {
    current_stmt_line = stmt->line;
  }
  if (stmt->line > 0 && stmt->line != last_line) {
    // Determine which source lines to use based on current module
    const std::vector<std::string> *lines_ptr = &source_lines;
    if (!current_module_prefix.empty()) {
      // Find module source lines by matching prefix to module name
      // current_module_prefix is "modname_", strip trailing "_"
      std::string mod_key =
          current_module_prefix.substr(0, current_module_prefix.size() - 1);
      if (auto it = module_source_lines.find(mod_key);
          it != module_source_lines.end()) {
        lines_ptr = &it->second;
      }
    }

    if (stmt->line <= static_cast<int>(lines_ptr->size())) {
      emit(tacky::DebugLine{stmt->line, (*lines_ptr)[stmt->line - 1],
                            current_source_file});
      last_line = stmt->line;
    }
  }

  if (auto *imp = dynamic_cast<const ImportStmt *>(stmt)) {
    // Inside inline bodies, register import aliases so resolve_callee
    // can find module-prefixed functions (e.g., pin_set_mode →
    // pymcu_hal__gpio_atmega328p_pin_set_mode)
    if (inline_depth > 0) {
      for (const auto &sym : imp->symbols) {
        imported_aliases[sym] = imp->module_name;
      }
    }
    return;
  }
  if (auto *block = dynamic_cast<const Block *>(stmt)) return visitBlock(block);
  if (auto *ret = dynamic_cast<const ReturnStmt *>(stmt))
    return visitReturn(ret);
  if (auto ifStmt = dynamic_cast<const IfStmt *>(stmt)) return visitIf(ifStmt);
  if (auto matchStmt = dynamic_cast<const MatchStmt *>(stmt))
    return visitMatch(matchStmt);
  if (auto whileStmt = dynamic_cast<const WhileStmt *>(stmt))
    return visitWhile(whileStmt);
  if (auto forStmt = dynamic_cast<const ForStmt *>(stmt))
    return visitFor(forStmt);
  if (auto *breakStmt = dynamic_cast<const BreakStmt *>(stmt))
    return visitBreak(breakStmt);
  if (auto *continueStmt = dynamic_cast<const ContinueStmt *>(stmt))
    return visitContinue(continueStmt);
  if (auto *assign = dynamic_cast<const AssignStmt *>(stmt))
    return visitAssign(assign);
  if (auto *augAssign = dynamic_cast<const AugAssignStmt *>(stmt))
    return visitAugAssign(augAssign);
  if (auto *decl = dynamic_cast<const VarDecl *>(stmt))
    return visitVarDecl(decl);
  if (auto *annAssign = dynamic_cast<const AnnAssign *>(stmt))
    return visitAnnAssign(annAssign);
  if (auto *exprStmt = dynamic_cast<const ExprStmt *>(stmt))
    return visitExprStmt(exprStmt);
  else if (auto global = dynamic_cast<const GlobalStmt *>(stmt)) {
    visitGlobal(global);
  } else if (auto cls = dynamic_cast<const ClassDef *>(stmt)) {
    visitClassDef(cls);
  } else if (dynamic_cast<const PassStmt *>(stmt)) {
    return;
  } else if (auto *raiseStmt = dynamic_cast<const RaiseStmt *>(stmt)) {
    throw std::runtime_error(
        raiseStmt->error_type + ": " + raiseStmt->message);
  } else if (!stmt) {
    throw std::runtime_error("IR Generation: Statement pointer is null");
  } else {
    throw std::runtime_error(
        std::string("IR Generation: Unknown Statement type: ") +
        typeid(*stmt).name());
  }
}

void IRGenerator::visitReturn(const ReturnStmt *stmt) {
  tacky::Val val = std::monostate{};
  if (stmt->value) {
    val = visitExpression(stmt->value.get());
  }

  if (!inline_stack.empty()) {
    const auto &ctx = inline_stack.back();
    if (ctx.result_temp.has_value()) {
      emit(tacky::Copy{val, ctx.result_temp.value()});

      // Track alias for return value if possible
      if (const auto v = std::get_if<tacky::Variable>(&val)) {
        variable_aliases[ctx.result_temp->name] = v->name;
      } else if (const auto t = std::get_if<tacky::Temporary>(&val)) {
        variable_aliases[ctx.result_temp->name] = t->name;
      }
      if (const auto c = std::get_if<tacky::Constant>(&val)) {
        constant_variables[ctx.result_temp->name] = c->value;
      }
    }
    emit(tacky::Jump{ctx.exit_label});
  } else {
    emit(tacky::Return{val});
  }
}

// Helper: Try to detect and emit optimized bit test jumps
int IRGenerator::emit_optimized_conditional_jump(
    const Expression *cond, const std::string &target_label,
    bool jump_if_true) {
  // Helper to resolve constant integers (literals or global constants)
  auto resolve_int = [&](const Expression *expr) -> std::optional<int> {
    if (const auto num = dynamic_cast<const IntegerLiteral *>(expr)) {
      return num->value;
    }
    if (const auto var = dynamic_cast<const VariableExpr *>(expr)) {
      if (globals.contains(var->name) &&
          !globals.at(var->name).is_memory_address) {
        return globals.at(var->name).value;
      }
    }
    return std::nullopt;
  };

  // Pattern D: Relational Operations ( > < >= <= == != )
  // We want to emit JumpIfGreater, JumpIfEqual, etc. directly.
  if (auto binExpr = dynamic_cast<const BinaryExpr *>(cond)) {
    tacky::Val v1 = visitExpression(binExpr->left.get());
    tacky::Val v2 = visitExpression(binExpr->right.get());

    // Compile-time constant evaluation: if both operands are known constants,
    // evaluate the condition statically and emit an unconditional jump or nothing.
    // This is critical for zero-cost abstractions where inlined string/int
    // comparisons (e.g., name == "RA0") must be resolved at compile time.
    auto c1 = std::get_if<tacky::Constant>(&v1);
    auto c2 = std::get_if<tacky::Constant>(&v2);
    if (c1 && c2) {
      bool cond_result = false;
      switch (binExpr->op) {
        case BinaryOp::Equal:
          cond_result = (c1->value == c2->value);
          break;
        case BinaryOp::NotEqual:
          cond_result = (c1->value != c2->value);
          break;
        case BinaryOp::Less:
          cond_result = (c1->value < c2->value);
          break;
        case BinaryOp::LessEq:
          cond_result = (c1->value <= c2->value);
          break;
        case BinaryOp::Greater:
          cond_result = (c1->value > c2->value);
          break;
        case BinaryOp::GreaterEq:
          cond_result = (c1->value >= c2->value);
          break;
        default:
          break;
      }
      // jump_if_true=false → "jump to target if condition is FALSE"
      if (jump_if_true) {
        if (cond_result) emit(tacky::Jump{target_label});
      } else {
        if (!cond_result) emit(tacky::Jump{target_label});
      }
      // Return statically resolved: 1 = condition true, -1 = condition false
      return cond_result ? 1 : -1;
    }

    switch (binExpr->op) {
      case BinaryOp::Equal:
        if (jump_if_true)
          emit(tacky::JumpIfEqual{v1, v2, target_label});
        else
          emit(tacky::JumpIfNotEqual{v1, v2, target_label});
        return 1;
      case BinaryOp::NotEqual:
        if (jump_if_true)
          emit(tacky::JumpIfNotEqual{v1, v2, target_label});
        else
          emit(tacky::JumpIfEqual{v1, v2, target_label});
        return 1;
      case BinaryOp::Less:
        if (jump_if_true)
          emit(tacky::JumpIfLessThan{v1, v2, target_label});
        else
          emit(tacky::JumpIfGreaterOrEqual{v1, v2, target_label});
        return 1;
      case BinaryOp::LessEq:
        if (jump_if_true)
          emit(tacky::JumpIfLessOrEqual{v1, v2, target_label});
        else
          emit(tacky::JumpIfGreaterThan{v1, v2, target_label});
        return 1;
      case BinaryOp::Greater:
        if (jump_if_true)
          emit(tacky::JumpIfGreaterThan{v1, v2, target_label});
        else
          emit(tacky::JumpIfLessOrEqual{v1, v2, target_label});
        return 1;
      case BinaryOp::GreaterEq:
        if (jump_if_true)
          emit(tacky::JumpIfGreaterOrEqual{v1, v2, target_label});
        else
          emit(tacky::JumpIfLessThan{v1, v2, target_label});
        return 1;
      default:
        break;
    }
  }

  // Pattern A: BitAccess(addr, bit) == 0/1
  // Case 1: Explicit Comparison
  if (auto binExpr = dynamic_cast<const BinaryExpr *>(cond)) {
    if (binExpr->op == BinaryOp::Equal || binExpr->op == BinaryOp::NotEqual) {
      const IndexExpr *indexExpr =
          dynamic_cast<const IndexExpr *>(binExpr->left.get());
      const Expression *rhsExpr = binExpr->right.get();

      // Swap if needed
      if (!indexExpr) {
        indexExpr = dynamic_cast<const IndexExpr *>(binExpr->right.get());
        rhsExpr = binExpr->left.get();
      }

      if (indexExpr) {
        // Skip bit-jump optimisation if the indexed variable is a declared array.
        bool target_is_array = false;
        if (auto *ve = dynamic_cast<const VariableExpr *>(indexExpr->target.get())) {
          std::string q = current_function.empty() ? ve->name : current_function + "." + ve->name;
          target_is_array = array_sizes.contains(q);
        }
        if (!target_is_array) {
          auto bitVal = resolve_int(indexExpr->index.get());
          auto targetVal = resolve_int(rhsExpr);

          if (bitVal.has_value() && targetVal.has_value()) {
            tacky::Val addr = visitExpression(indexExpr->target.get());
            int bit = bitVal.value();
            int target = targetVal.value();

            bool invert = (binExpr->op == BinaryOp::NotEqual);
            if (invert) target = !target;

            if (target == 0) {
              // == 0: Jump if Bit is 1 (SET)
              // != 0: Jump if Bit is 0 (CLEAR) (Not handled by user explicit
              // request but logical) User Request: == 0 -> BTFSC (Jump if Set).
              // My backend: JumpIfBitSet -> BTFSC.
              if (jump_if_true) {
                // Jump to TARGET if condition True (Bit is 0). Jump if Clear.
                emit(tacky::JumpIfBitClear{addr, bit, target_label});
              } else {
                // Jump to ELSE (Target) if condition False (Bit is 1). Jump if
                // Set.
                emit(tacky::JumpIfBitSet{addr, bit, target_label});
              }
              return 1;
            } else if (target == 1) {
              // == 1: Jump if Bit is 0 (CLEAR)
              if (jump_if_true) {
                // Jump to TARGET if condition True (Bit is 1). Jump if Set.
                emit(tacky::JumpIfBitSet{addr, bit, target_label});
              } else {
                // Jump to ELSE (Target) if condition False (Bit is 0). Jump if
                // Clear.
                emit(tacky::JumpIfBitClear{addr, bit, target_label});
              }
              return 1;
            }
          }
        }
      }
    }
  }

  // Pattern C: Unary Not (if not bit)
  // Case 2: Pythonic NOT
  if (auto *unaryExpr = dynamic_cast<const UnaryExpr *>(cond)) {
    if (unaryExpr->op == UnaryOp::Not) {
      if (auto *indexExpr =
              dynamic_cast<const IndexExpr *>(unaryExpr->operand.get())) {
        bool target_is_array = false;
        if (auto *ve = dynamic_cast<const VariableExpr *>(indexExpr->target.get())) {
          std::string q = current_function.empty() ? ve->name : current_function + "." + ve->name;
          target_is_array = array_sizes.contains(q);
        }
        if (!target_is_array) {
          auto bitVal = resolve_int(indexExpr->index.get());
          if (bitVal.has_value()) {
            tacky::Val addr = visitExpression(indexExpr->target.get());
            int bit = bitVal.value();

            // Condition: Bit is 0.
            if (jump_if_true) {
              // Jump to Target if True (Bit is 0). Jump if Clear.
              emit(tacky::JumpIfBitClear{addr, bit, target_label});
            } else {
              // Jump to Else (Target) if False (Bit is 1). Jump if Set.
              // ASM: BTFSC (Jump if Set) -> GOTO Else.
              emit(tacky::JumpIfBitSet{addr, bit, target_label});
            }
            return 1;
          }
        }
      }
    }
  }

  // Pattern B: Single BitAccess (implicit true test)
  // Case 3: Implicit Truthiness
  if (auto *indexExpr = dynamic_cast<const IndexExpr *>(cond)) {
    bool target_is_array = false;
    if (auto *ve = dynamic_cast<const VariableExpr *>(indexExpr->target.get())) {
      std::string q = current_function.empty() ? ve->name : current_function + "." + ve->name;
      target_is_array = array_sizes.contains(q);
    }
    if (!target_is_array) {
      auto bitVal = resolve_int(indexExpr->index.get());
      if (bitVal.has_value()) {
        tacky::Val addr = visitExpression(indexExpr->target.get());
        int bit = bitVal.value();

        // Condition: Bit is 1.
        if (jump_if_true) {
          // Jump to Target if True (Bit is 1). Jump if Set.
          emit(tacky::JumpIfBitSet{addr, bit, target_label});
        } else {
          // Jump to Else (Target) if False (Bit is 0). Jump if Clear.
          // ASM: BTFSS (Jump if Clear) -> GOTO Else.
          emit(tacky::JumpIfBitClear{addr, bit, target_label});
        }
        return 1;
      }
    }
  }

  return 0;
}

void IRGenerator::visitIf(const IfStmt *stmt) {
  std::string end_label = make_label();

  // 1. Main If Condition
  std::string next_label = (stmt->elif_branches.empty() && !stmt->else_branch)
                               ? end_label
                               : make_label();

  // "If boolean optimization": Jump to next_label if condition is FALSE
  // Returns: 0 = not optimized, 1 = optimized (runtime or static true),
  //         -1 = statically false
  int opt_result = emit_optimized_conditional_jump(stmt->condition.get(),
                                                    next_label, false);
  bool skip_then = false;

  if (opt_result == -1) {
    // Condition is statically FALSE — skip then_branch entirely (dead code)
    skip_then = true;
  } else if (opt_result == 0) {
    // Fall back to standard evaluation
    tacky::Val cond_val = visitExpression(stmt->condition.get());
    if (auto c = std::get_if<tacky::Constant>(&cond_val)) {
      if (c->value == 0) {
        // Condition is always FALSE, skip then_branch entirely
        skip_then = true;
        if (stmt->elif_branches.empty() && !stmt->else_branch) {
          emit(tacky::Label{end_label});
          return;
        }
        emit(tacky::Jump{next_label});
      } else {
        // Condition is always TRUE, skip jump and elif/else branches
        visitStatement(stmt->then_branch.get());
        emit(tacky::Label{end_label});
        return;
      }
    } else {
      emit(tacky::JumpIfZero{cond_val, next_label});
    }
  }
  // opt_result >= 1: Optimized (runtime jump emitted or statically true)
  // — fall through to visit then_branch normally

  if (!skip_then) {
    visitStatement(stmt->then_branch.get());

    // Only emit jump to end if we have other branches
    if (!stmt->elif_branches.empty() || stmt->else_branch) {
      emit(tacky::Jump{end_label});
    }
  }

  // 2. Elif Branches
  for (size_t i = 0; i < stmt->elif_branches.size(); ++i) {
    emit(tacky::Label{next_label});

    bool is_last_elif = (i == stmt->elif_branches.size() - 1);
    next_label =
        (is_last_elif && !stmt->else_branch) ? end_label : make_label();

    const auto &[elif_cond, elif_block] = stmt->elif_branches[i];

    // "Elif boolean optimization": Jump to next_label if condition is FALSE
    int elif_opt =
        emit_optimized_conditional_jump(elif_cond.get(), next_label, false);
    bool skip_elif = false;

    if (elif_opt == -1) {
      // Condition is statically FALSE — skip elif body (dead code)
      skip_elif = true;
    } else if (elif_opt == 0) {
      tacky::Val elif_val = visitExpression(elif_cond.get());
      if (auto c = std::get_if<tacky::Constant>(&elif_val)) {
        if (c->value == 0) {
          skip_elif = true;
          emit(tacky::Jump{next_label});
        }
      } else {
        emit(tacky::JumpIfZero{elif_val, next_label});
      }
    }

    if (!skip_elif) {
      visitStatement(elif_block.get());

      if (!is_last_elif || stmt->else_branch) {
        emit(tacky::Jump{end_label});
      }
    }
  }

  // 3. Else Branch
  if (stmt->else_branch) {
    emit(tacky::Label{next_label});
    visitStatement(stmt->else_branch.get());
  }

  emit(tacky::Label{end_label});
}

void IRGenerator::visitMatch(const MatchStmt *stmt) {
  // Generate Match/Case Logic
  // 1. Evaluate Target -> Temp
  // 2. Case 1:
  //    Temp == Pattern ?
  //    JumpIfZero -> Next Case
  //    Body
  //    Jump -> End
  // ...
  // N. Default Case (_)
  //    Body
  //    Jump -> End

  tacky::Val target_val = visitExpression(stmt->target.get());

  // Optimization: If target is complex, store it in a temp to avoid
  // re-evaluation? visitExpression usually returns a Variable, Constant, or
  // Temporary. So it is safe to reuse `target_val` without re-evaluation side
  // effects UNLESS visitExpression emitted instructions that we shouldn't
  // repeat. Since `visitExpression` emits instructions to calculate the value,
  // `target_val` holds the *result*. So we are good.
  // HOWEVER, if the result is a temporary that might be clobbered, we should be
  // careful. In TACKY, temporaries are unique variables, so it's fine.

  std::string end_label = make_label();

  for (const auto &branch : stmt->branches) {
    std::string next_case_label = make_label();

    if (branch.pattern) {
      // Specific Pattern
      tacky::Val pattern_val = visitExpression(branch.pattern.get());

      // Generate equality check: target == pattern
      tacky::Temporary cmp_res = make_temp();
      emit(tacky::Binary{tacky::BinaryOp::Equal, target_val, pattern_val,
                         cmp_res});

      // If false (0), jump to next case
      emit(tacky::JumpIfZero{cmp_res, next_case_label});

      // Execute body
      visitBlock(dynamic_cast<const Block *>(branch.body.get()));
      emit(tacky::Jump{end_label});
    } else {
      // Wildcard Case (_)
      // Always execute if we reached here
      visitBlock(dynamic_cast<const Block *>(branch.body.get()));
      emit(tacky::Jump{end_label});
    }

    emit(tacky::Label{next_case_label});
  }

  emit(tacky::Label{end_label});
}

void IRGenerator::visitWhile(const WhileStmt *stmt) {
  const std::string start_label = make_label();
  const std::string end_label = make_label();

  loop_stack.push_back({start_label, end_label});

  emit(tacky::Label{start_label});

  // "While boolean optimization": Jump to end_label if condition is FALSE
  int while_opt = emit_optimized_conditional_jump(stmt->condition.get(),
                                                   end_label, false);

  if (while_opt == -1) {
    // Condition is statically FALSE — loop body is dead code
    emit(tacky::Label{end_label});
    loop_stack.pop_back();
    return;
  }

  if (while_opt == 0) {
    // Fall back to standard evaluation
    const tacky::Val cond_val = visitExpression(stmt->condition.get());
    if (auto c = std::get_if<tacky::Constant>(&cond_val)) {
      if (c->value == 0) {
        emit(tacky::Jump{end_label});
      }
    } else {
      emit(tacky::JumpIfZero{cond_val, end_label});
    }
  }

  visitStatement(stmt->body.get());

  emit(tacky::Jump{start_label});
  emit(tacky::Label{end_label});
  loop_stack.pop_back();
}

void IRGenerator::visitFor(const ForStmt *stmt) {
  // General for-in: compile-time unrolling over a string constant
  if (stmt->iterable) {
    // Resolve the iterable to a string constant
    auto get_str = [&](const Expression *e) -> std::optional<std::string> {
      if (const auto *lit = dynamic_cast<const StringLiteral *>(e))
        return lit->value;
      if (const auto *var = dynamic_cast<const VariableExpr *>(e)) {
        std::string key = current_inline_prefix + var->name;
        for (int depth = 0; depth < 20; ++depth) {
          if (str_constant_variables.contains(key))
            return str_constant_variables.at(key);
          if (variable_aliases.contains(key)) {
            key = variable_aliases.at(key);
          } else {
            break;
          }
        }
      }
      return std::nullopt;
    };

    auto str_opt = get_str(stmt->iterable.get());
    if (!str_opt.has_value()) {
      throw std::runtime_error(
          "for-in loop iterable must be a compile-time string constant. "
          "Use 'const[str]' type annotation on the parameter.");
    }

    std::string var_key = current_inline_prefix + stmt->var_name;
    for (unsigned char c : *str_opt) {
      constant_variables[var_key] = static_cast<int>(c);
      visitStatement(stmt->body.get());
    }
    constant_variables.erase(var_key);
    return;
  }

  // Desugar: for VAR in range(start, stop, step) → while loop
  tacky::Val start_val = stmt->range_start
      ? visitExpression(stmt->range_start.get())
      : tacky::Constant{0};
  tacky::Val stop_val = visitExpression(stmt->range_stop.get());
  tacky::Val step_val = stmt->range_step
      ? visitExpression(stmt->range_step.get())
      : tacky::Constant{1};

  // Allocate loop variable
  std::string var_name = current_inline_prefix.empty()
      ? stmt->var_name
      : current_inline_prefix + stmt->var_name;
  tacky::Variable loop_var{var_name, DataType::UINT8};
  emit(tacky::Copy{start_val, loop_var});

  std::string start_label = make_label();
  std::string end_label = make_label();
  loop_stack.push_back({start_label, end_label});

  emit(tacky::Label{start_label});

  // Condition: i < stop → exit when i >= stop
  emit(tacky::JumpIfGreaterOrEqual{loop_var, stop_val, end_label});

  visitStatement(stmt->body.get());

  // Increment: i += step
  emit(tacky::AugAssign{tacky::BinaryOp::Add, loop_var, step_val});
  emit(tacky::Jump{start_label});
  emit(tacky::Label{end_label});
  loop_stack.pop_back();
}

void IRGenerator::visitBreak(const BreakStmt *stmt) {
  if (loop_stack.empty()) {
    throw std::runtime_error("Break statement outside of loop");
  }
  emit(tacky::Jump{loop_stack.back().break_label});
}

void IRGenerator::visitContinue(const ContinueStmt *stmt) {
  if (loop_stack.empty()) {
    throw std::runtime_error("Continue statement outside of loop");
  }
  emit(tacky::Jump{loop_stack.back().continue_label});
}

void IRGenerator::visitAssign(const AssignStmt *stmt) {
  if (auto indexExpr = dynamic_cast<const IndexExpr *>(stmt->target.get())) {
    // Disambiguation: if the target is a declared array, treat as element store.
    if (auto *ve = dynamic_cast<const VariableExpr *>(indexExpr->target.get())) {
      std::string qualified = current_function.empty()
                                  ? ve->name
                                  : current_function + "." + ve->name;
      if (array_sizes.contains(qualified)) {
        tacky::Val idx_val = visitExpression(indexExpr->index.get());
        if (auto *c = std::get_if<tacky::Constant>(&idx_val)) {
          std::string elem_name = qualified + "__" + std::to_string(c->value);
          tacky::Variable elem_var{elem_name, array_elem_types.at(qualified)};
          tacky::Val val = visitExpression(stmt->value.get());
          emit(tacky::Copy{val, elem_var});
          return;
        }
        throw std::runtime_error("Array subscript must be a compile-time constant");
      }
    }

    // Bit-slice path (existing behaviour for register access like PORTB[0] = 1)
    tacky::Val target = visitExpression(indexExpr->target.get());
    tacky::Val indexVal = visitExpression(indexExpr->index.get());
    int bit = 0;

    if (auto c = std::get_if<tacky::Constant>(&indexVal)) {
      bit = c->value;
    } else {
      throw std::runtime_error("Bit index must be constant");
    }

    tacky::Val val = visitExpression(stmt->value.get());

    if (auto c = std::get_if<tacky::Constant>(&val)) {
      if (c->value != 0) {
        emit(tacky::BitSet{target, bit});
      } else {
        emit(tacky::BitClear{target, bit});
      }
    } else {
      emit(tacky::BitWrite{target, bit, val});
    }
    return;
  }

  // Pre-register constructor target BEFORE evaluating RHS.
  // This allows the inlined constructor to bind 'self' to the target variable,
  // enabling zero-cost member propagation (e.g., self.name = "RA0" → led_name).
  if (auto varExpr = dynamic_cast<const VariableExpr *>(stmt->target.get())) {
    if (auto call = dynamic_cast<const CallExpr *>(stmt->value.get())) {
      if (auto calleeVar =
              dynamic_cast<const VariableExpr *>(call->callee.get())) {
        std::string resolvedClass = resolve_callee(calleeVar->name);
        if (inline_functions.contains(resolvedClass + "___init__")) {
          std::string qualified_name = current_function.empty()
                                           ? varExpr->name
                                           : current_function + "." + varExpr->name;
          instance_classes[qualified_name] = resolvedClass;
          pending_constructor_target = qualified_name;
          virtual_instances.insert(qualified_name);
        }
      }
    }
  }

  tacky::Val value = visitExpression(stmt->value.get());

  if (auto varExpr = dynamic_cast<const VariableExpr *>(stmt->target.get())) {
    tacky::Val target;
    if (!current_function.empty()) {
      if (current_function_globals.count(varExpr->name)) {
        // Explicit global assignment
        target = resolve_binding(varExpr->name);
      } else {
        // Local assignment: when inside an inline expansion use the inline prefix
        // so that `i = i + 1` inside _delay_ms_avr resolves to
        // inline2._delay_ms_avr.i, not main.i.
        if (!current_inline_prefix.empty()) {
          target = resolve_binding(varExpr->name);
        } else {
          std::string qualified_name = current_function + "." + varExpr->name;
          DataType type = DataType::UINT8;
          if (variable_types.contains(qualified_name)) {
            type = variable_types.at(qualified_name);
          }
          target = tacky::Variable{qualified_name, type};
        }
      }
    } else {
      // Top-level assignment
      target = resolve_binding(varExpr->name);
    }

    // Skip dead copy for void constructor results (zero-cost: no RAM for instance)
    if (std::holds_alternative<std::monostate>(value)) {
      // Constructor returned void — instance is virtual, no copy needed
    } else {
      emit(tacky::Copy{value, target});
    }

    // Track alias if it's a variable or temporary assignment
    if (const auto v = std::get_if<tacky::Variable>(&value)) {
      if (const auto t = std::get_if<tacky::Variable>(&target)) {
        variable_aliases[t->name] = v->name;
      }
    } else if (const auto t_src = std::get_if<tacky::Temporary>(&value)) {
      if (const auto t_dst = std::get_if<tacky::Variable>(&target)) {
        variable_aliases[t_dst->name] = t_src->name;
      }
    }
    // Track constant if it's a constant assignment (only at module level;
    // inside functions, variables may be reassigned in loops/branches).
    // Skip mutable globals — they may be reassigned at runtime via `global`.
    if (current_function.empty()) {
      if (const auto c = std::get_if<tacky::Constant>(&value)) {
        if (const auto t = std::get_if<tacky::Variable>(&target)) {
          if (!mutable_globals.contains(t->name)) {
            constant_variables[t->name] = c->value;
          }
        }
      }
    } else {
      // Inside a function: erase any stale constant entry for this variable
      if (const auto t = std::get_if<tacky::Variable>(&target)) {
        constant_variables.erase(t->name);
      }
    }
  } else if (auto memExpr =
                 dynamic_cast<const MemberAccessExpr *>(stmt->target.get())) {
    if (memExpr->member == "value") {
      tacky::Val target = visitExpression(memExpr->object.get());

      // Look up the variable type to determine if multi-byte operation
      DataType var_type = DataType::UINT8;  // Default
      if (auto var = std::get_if<tacky::Variable>(&target)) {
        if (variable_types.contains(var->name)) {
          var_type = variable_types.at(var->name);
        }
      } else if (auto mem = std::get_if<tacky::MemoryAddress>(&target)) {
        // For memory-mapped registers (ptr types), the type is carried
        // on the MemoryAddress itself from scan_globals/resolve_binding
        var_type = mem->type;
      }

      // Check if this is a multi-byte type
      int byte_count = size_of(var_type);

      if (byte_count == 1) {
        // 8-bit: Single byte write
        if (std::holds_alternative<tacky::MemoryAddress>(target) ||
            std::holds_alternative<tacky::Variable>(target)) {
          emit(tacky::Copy{value, target});
        } else {
          throw std::runtime_error(
              "Cannot assign to .value of this expression type");
        }
      } else if (byte_count == 2) {
        // 16-bit: Two byte writes (low, high)
        if (auto addr = std::get_if<tacky::MemoryAddress>(&target)) {
          // If value is a constant, extract bytes directly
          if (auto const_val = std::get_if<tacky::Constant>(&value)) {
            int full_value = const_val->value;
            int low_byte = full_value & 0xFF;
            int high_byte = (full_value >> 8) & 0xFF;

            emit(tacky::Copy{tacky::Constant{low_byte},
                             tacky::MemoryAddress{addr->address}});
            emit(tacky::Copy{tacky::Constant{high_byte},
                             tacky::MemoryAddress{addr->address + 1}});
          } else {
            // Direct 2-byte copy: the backend natively handles multi-byte
            // Copy from variable/temp to MemoryAddress, decomposing into
            // individual byte moves without redundant mask/shift temporaries.
            emit(tacky::Copy{value,
                             tacky::MemoryAddress{addr->address,
                                                  DataType::UINT16}});
          }
        } else {
          throw std::runtime_error(
              "16-bit .value assignment requires constant address");
        }
      } else {
        throw std::runtime_error("Unsupported type size for .value assignment");
      }
    } else {
      // General member access: flattened to obj_member
      tacky::Val objVal = visitExpression(memExpr->object.get());
      std::string base_name;
      if (auto *v = std::get_if<tacky::Variable>(&objVal)) {
        base_name = v->name;
      } else if (auto *t = std::get_if<tacky::Temporary>(&objVal)) {
        base_name = t->name;
      }

      if (!base_name.empty()) {
        while (variable_aliases.contains(base_name)) {
          base_name = variable_aliases.at(base_name);
        }
        std::string flattened_name = base_name + "_" + memExpr->member;

        // Zero-Cost Abstraction: suppress Copy for virtual instance members
        // when value is a compile-time constant. The constant is tracked for
        // folding at use sites, but no RAM or instructions are emitted.
        if (auto c = std::get_if<tacky::Constant>(&value)) {
          constant_variables[flattened_name] = c->value;
          if (virtual_instances.contains(base_name)) {
            return;  // Virtual instance — no RAM needed
          }
        }

        emit(tacky::Copy{value,
                         tacky::Variable{flattened_name, DataType::UINT8}});
        return;
      }
      throw std::runtime_error("Unknown member access in assignment: " +
                               memExpr->member);
    }
  } else if (auto unaryExpr = dynamic_cast<const UnaryExpr *>(stmt->target.get())) {
    // Indirect assignment: *ptr = value
    if (unaryExpr->op == UnaryOp::Deref) {
      tacky::Val ptr = visitExpression(unaryExpr->operand.get());
      emit(tacky::StoreIndirect{value, ptr});
      return;
    }
    throw std::runtime_error("Invalid assignment target");
  } else {
    throw std::runtime_error("Invalid assignment target");
  }
}

void IRGenerator::visitVarDecl(const VarDecl *stmt) {
  // Track variable type
  DataType dt = resolve_type(stmt->var_type);
  // MUST mirror make_variable(): inside inline expansion use current_inline_prefix
  // so that lookup keys match (e.g. "delay_ms__i" vs "delay_ms.i").
  std::string qualified;
  if (!current_inline_prefix.empty()) {
    qualified = current_inline_prefix + stmt->name;
  } else if (!current_function.empty()) {
    qualified = current_function + "." + stmt->name;
  } else {
    qualified = stmt->name;
  }
  variable_types[qualified] = dt;

  // Create the Variable
  if (stmt->init) {
    tacky::Val val = visitExpression(stmt->init.get());
    tacky::Val target = resolve_binding(stmt->name);
    // Set type on the target variable
    if (auto v = std::get_if<tacky::Variable>(&target)) {
      v->type = dt;
    }
    emit(tacky::Copy{val, target});

    // Track constant for folding (only at module level, not inside functions
    // where variables may be reassigned in loops/branches).
    // Skip mutable globals — they may be reassigned at runtime via `global`.
    if (current_function.empty()) {
      if (auto c = std::get_if<tacky::Constant>(&val)) {
        if (auto v = std::get_if<tacky::Variable>(&target)) {
          if (!mutable_globals.contains(v->name)) {
            constant_variables[v->name] = c->value;
          }
        }
      }
    }
  }
}

void IRGenerator::visitAnnAssign(const AnnAssign *stmt) {
  // Check for array annotation: TYPE[N] where N is a pure integer, e.g. "uint8[4]".
  // Distinguished from ptr[TYPE] / const[TYPE] by the bracket content being all digits.
  {
    auto bracket = stmt->annotation.find('[');
    auto close   = stmt->annotation.rfind(']');
    if (bracket != std::string::npos && close != std::string::npos &&
        close == stmt->annotation.size() - 1 && close > bracket + 1) {
      std::string inner = stmt->annotation.substr(bracket + 1, close - bracket - 1);
      bool is_number = !inner.empty() &&
                       std::all_of(inner.begin(), inner.end(), ::isdigit);
      if (is_number) {
        int count = std::stoi(inner);
        DataType elem_dt = resolve_type(stmt->annotation.substr(0, bracket));
        std::string qualified = current_function.empty()
                                    ? stmt->target
                                    : current_function + "." + stmt->target;
        array_sizes[qualified]      = count;
        array_elem_types[qualified] = elem_dt;
        variable_types[qualified]   = elem_dt;

        // Determine initializer values (default: zero-init)
        std::vector<int> init_vals(count, 0);
        if (stmt->value) {
          if (auto *le = dynamic_cast<const ListExpr *>(stmt->value.get())) {
            for (int k = 0; k < std::min(count, (int)le->elements.size()); ++k) {
              if (auto *il = dynamic_cast<const IntegerLiteral *>(le->elements[k].get()))
                init_vals[k] = il->value;
            }
          }
        }

        // Emit Copy for each element to a synthetic scalar variable: qualified__k
        for (int k = 0; k < count; ++k) {
          std::string elem_name = qualified + "__" + std::to_string(k);
          tacky::Variable elem_var{elem_name, elem_dt};
          variable_types[elem_name] = elem_dt;
          emit(tacky::Copy{tacky::Constant{init_vals[k]}, elem_var});
        }
        return;
      }
    }
  }

  // Extract type from annotation string like "ptr[uint16]"
  DataType type = DataType::UINT8;  // default

  if (stmt->annotation.find("ptr[uint16]") != std::string::npos) {
    type = DataType::UINT16;
  } else if (stmt->annotation.find("ptr[uint32]") != std::string::npos) {
    type = DataType::UINT32;
  } else if (stmt->annotation.find("uint16") != std::string::npos) {
    type = DataType::UINT16;
  } else if (stmt->annotation.find("uint32") != std::string::npos) {
    type = DataType::UINT32;
  }
  // ptr[uint8] or plain "ptr" → UINT8 (default)

  // Store in variable_types map with proper qualification.
  // MUST mirror make_variable(): when inside an inline expansion,
  // use current_inline_prefix so the lookup keys match.
  std::string qualified;
  if (!current_inline_prefix.empty()) {
    qualified = current_inline_prefix + stmt->target;
  } else if (!current_function.empty()) {
    qualified = current_function + "." + stmt->target;
  } else {
    qualified = stmt->target;
  }
  variable_types[qualified] = type;

  // Generate assignment IR if initializer present
  if (stmt->value) {
    tacky::Val rhs = visitExpression(stmt->value.get());

    // If RHS is a MemoryAddress, propagate the type
    if (auto *addr = std::get_if<tacky::MemoryAddress>(&rhs)) {
      addr->type = type;
    }

    // Create variable with type (use qualified name when inside a function)
    tacky::Variable var{qualified, type};
    emit(tacky::Copy{rhs, var});
  }
}

DataType IRGenerator::resolve_type(const std::string &type_str) {
  // Check if this is a ptr[TYPE] annotation
  if (type_str.find("ptr[") == 0 && type_str.back() == ']') {
    // Extract the element type from ptr[uint16] -> uint16
    size_t start = type_str.find('[') + 1;
    size_t end = type_str.find(']');
    std::string element_type = type_str.substr(start, end - start);
    return string_to_datatype(element_type);
  }
  // const[TYPE] → resolve the inner type (const is compile-time only)
  if (type_str.find("const[") == 0 && type_str.back() == ']') {
    std::string inner = type_str.substr(6, type_str.size() - 7);
    return string_to_datatype(inner);
  }
  return string_to_datatype(type_str);
}

void IRGenerator::visitAugAssign(const AugAssignStmt *stmt) {
  // Map AugOp to tacky::BinaryOp
  auto map_augop = [](AugOp op) -> tacky::BinaryOp {
    switch (op) {
      case AugOp::Add:
        return tacky::BinaryOp::Add;
      case AugOp::Sub:
        return tacky::BinaryOp::Sub;
      case AugOp::Mul:
        return tacky::BinaryOp::Mul;
      case AugOp::Div:
        return tacky::BinaryOp::Div;
      case AugOp::FloorDiv:
        return tacky::BinaryOp::FloorDiv;
      case AugOp::Mod:
        return tacky::BinaryOp::Mod;
      case AugOp::BitAnd:
        return tacky::BinaryOp::BitAnd;
      case AugOp::BitOr:
        return tacky::BinaryOp::BitOr;
      case AugOp::BitXor:
        return tacky::BinaryOp::BitXor;
      case AugOp::LShift:
        return tacky::BinaryOp::LShift;
      case AugOp::RShift:
        return tacky::BinaryOp::RShift;
    }
    return tacky::BinaryOp::Add;  // Unreachable
  };

  tacky::Val operand = visitExpression(stmt->value.get());

  if (auto varExpr = dynamic_cast<const VariableExpr *>(stmt->target.get())) {
    tacky::Val target = resolve_binding(varExpr->name);
    emit(tacky::AugAssign{map_augop(stmt->op), target, operand});
  } else {
    throw std::runtime_error("Augmented assignment target must be a variable");
  }
}

void IRGenerator::visitExprStmt(const ExprStmt *stmt) {
  visitExpression(stmt->expr.get());
}

void IRGenerator::visitGlobal(const GlobalStmt *stmt) {
  for (const auto &name : stmt->names) {
    current_function_globals.insert(name);
  }
}

tacky::Val IRGenerator::visitExpression(const Expression *expr) {
  if (auto *bin = dynamic_cast<const BinaryExpr *>(expr))
    return visitBinary(bin);
  if (auto *un = dynamic_cast<const UnaryExpr *>(expr)) return visitUnary(un);
  if (auto *num = dynamic_cast<const IntegerLiteral *>(expr))
    return visitLiteral(num);
  if (auto *var = dynamic_cast<const VariableExpr *>(expr))
    return visitVariable(var);
  if (auto call = dynamic_cast<const CallExpr *>(expr)) {
    return visitCall(call);
  } else if (auto yield = dynamic_cast<const YieldExpr *>(expr)) {
    return visitYield(yield);
  }
  if (auto *idx = dynamic_cast<const IndexExpr *>(expr)) return visitIndex(idx);
  if (auto *mem = dynamic_cast<const MemberAccessExpr *>(expr))
    return visitMemberAccess(mem);

  if (auto *boolean = dynamic_cast<const BooleanLiteral *>(expr)) {
    return tacky::Constant{boolean->value ? 1 : 0};
  }

  if (auto *str = dynamic_cast<const StringLiteral *>(expr)) {
    // Single-char string literals are treated as their ASCII value (e.g. 'H' → 72)
    if (str->value.size() == 1) {
      return tacky::Constant{(int)(unsigned char)str->value[0]};
    }
    // Multi-char strings: assign an integer ID (used only by asm() which extracts
    // the value directly from the AST, not through this path)
    if (string_literal_ids.find(str->value) == string_literal_ids.end()) {
      string_literal_ids[str->value] = next_string_id++;
    }
    return tacky::Constant{string_literal_ids[str->value]};
  }

  throw std::runtime_error("IR Generation: Unknown Expression type");
}

tacky::Val IRGenerator::visitLiteral(const IntegerLiteral *expr) {
  return tacky::Constant{expr->value};
}

tacky::Val IRGenerator::visitVariable(const VariableExpr *expr) {
  return resolve_binding(expr->name);
}

tacky::Val IRGenerator::visitBinary(const BinaryExpr *expr) {
  tacky::Val v1 = visitExpression(expr->left.get());
  tacky::Val v2 = visitExpression(expr->right.get());

  auto get_val_type = [](const tacky::Val &v) {
    if (auto var = std::get_if<tacky::Variable>(&v)) return var->type;
    if (auto tmp = std::get_if<tacky::Temporary>(&v)) return tmp->type;
    if (auto c = std::get_if<tacky::Constant>(&v)) {
      if (c->value > 255 || c->value < -128) return DataType::UINT16;
      return DataType::UINT8;
    }
    return DataType::UINT8;
  };

  DataType t1 = get_val_type(v1);
  DataType t2 = get_val_type(v2);
  DataType resType = (size_of(t1) >= size_of(t2)) ? t1 : t2;

  tacky::Temporary dst = make_temp(resType);
  tacky::BinaryOp op;
  switch (expr->op) {
    case BinaryOp::Add:
      op = tacky::BinaryOp::Add;
      break;
    case BinaryOp::Sub:
      op = tacky::BinaryOp::Sub;
      break;
    case BinaryOp::Mul:
      op = tacky::BinaryOp::Mul;
      break;
    case BinaryOp::Div:
      op = tacky::BinaryOp::Div;
      break;
    case BinaryOp::FloorDiv:
      op = tacky::BinaryOp::FloorDiv;
      break;
    case BinaryOp::Mod:
      op = tacky::BinaryOp::Mod;
      break;
    case BinaryOp::Equal:
      op = tacky::BinaryOp::Equal;
      break;
    case BinaryOp::NotEqual:
      op = tacky::BinaryOp::NotEqual;
      break;
    case BinaryOp::Less:
      op = tacky::BinaryOp::LessThan;
      break;
    case BinaryOp::LessEq:
      op = tacky::BinaryOp::LessEqual;
      break;
    case BinaryOp::Greater:
      op = tacky::BinaryOp::GreaterThan;
      break;
    case BinaryOp::GreaterEq:
      op = tacky::BinaryOp::GreaterEqual;
      break;

    case BinaryOp::BitAnd:
      op = tacky::BinaryOp::BitAnd;
      break;
    case BinaryOp::BitOr:
      op = tacky::BinaryOp::BitOr;
      break;
    case BinaryOp::BitXor:
      op = tacky::BinaryOp::BitXor;
      break;
    case BinaryOp::LShift:
      op = tacky::BinaryOp::LShift;
      break;
    case BinaryOp::RShift:
      op = tacky::BinaryOp::RShift;
      break;
    case BinaryOp::And:
      op = tacky::BinaryOp::BitAnd;
      break;
    case BinaryOp::Or:
      op = tacky::BinaryOp::BitOr;
      break;
    default:
      throw std::runtime_error("Unsupported Binary Op");
  }

  if (auto c1 = std::get_if<tacky::Constant>(&v1)) {
    if (auto c2 = std::get_if<tacky::Constant>(&v2)) {
      switch (expr->op) {
        case BinaryOp::Add:
          return tacky::Constant{c1->value + c2->value};
        case BinaryOp::Sub:
          return tacky::Constant{c1->value - c2->value};
        case BinaryOp::Equal:
          return tacky::Constant{c1->value == c2->value ? 1 : 0};
        case BinaryOp::NotEqual:
          return tacky::Constant{c1->value != c2->value ? 1 : 0};
        case BinaryOp::And:
          return tacky::Constant{(c1->value && c2->value) ? 1 : 0};
        case BinaryOp::Or:
          return tacky::Constant{(c1->value || c2->value) ? 1 : 0};
        default:
          break;
      }
    }
  }

  emit(tacky::Binary{op, v1, v2, dst});
  return dst;
}

tacky::Val IRGenerator::visitUnary(const UnaryExpr *expr) {
  tacky::Val operand = visitExpression(expr->operand.get());

  // Constant folding: resolve at compile time if operand is constant
  if (auto c = std::get_if<tacky::Constant>(&operand)) {
    switch (expr->op) {
      case UnaryOp::Negate:
        return tacky::Constant{-c->value};
      case UnaryOp::Not:
        return tacky::Constant{!c->value ? 1 : 0};
      case UnaryOp::BitNot:
        return tacky::Constant{~c->value};
      default:
        break;
    }
  }

  // Indirect access: *ptr
  if (expr->op == UnaryOp::Deref) {
    tacky::Temporary result = make_temp(DataType::UINT8);
    emit(tacky::LoadIndirect{operand, result});
    return result;
  }

  tacky::Temporary result = make_temp(DataType::UINT8);  // Default to UINT8

  switch (expr->op) {
    case UnaryOp::Negate:
      emit(tacky::Unary{tacky::UnaryOp::Neg, operand, result});
      break;
    case UnaryOp::Not:
      emit(tacky::Unary{tacky::UnaryOp::Not, operand, result});
      break;
    case UnaryOp::BitNot:
      emit(tacky::Unary{tacky::UnaryOp::BitNot, operand, result});
      break;
    default:
      throw std::runtime_error("Unknown unary operator");
  }

  return result;
}

tacky::Val IRGenerator::visitYield(const YieldExpr *expr) {
  // TODO: Implement yield (Phase 3)
  throw std::runtime_error("Yield not yet implemented");
}

tacky::Val IRGenerator::visitIndex(const IndexExpr *expr) {
  // Disambiguation: if the target is a declared array, treat as element access.
  if (auto *ve = dynamic_cast<const VariableExpr *>(expr->target.get())) {
    std::string qualified = current_function.empty()
                                ? ve->name
                                : current_function + "." + ve->name;
    if (array_sizes.contains(qualified)) {
      tacky::Val idx_val = visitExpression(expr->index.get());
      if (auto *c = std::get_if<tacky::Constant>(&idx_val)) {
        std::string elem_name = qualified + "__" + std::to_string(c->value);
        return tacky::Variable{elem_name, array_elem_types.at(qualified)};
      }
      throw std::runtime_error("Array subscript must be a compile-time constant");
    }
  }

  // Bit-slice path (existing behaviour for register access like PORTB[0])
  tacky::Val target = visitExpression(expr->target.get());
  tacky::Val indexVal = visitExpression(expr->index.get());

  int bit = 0;
  if (auto c = std::get_if<tacky::Constant>(&indexVal)) {
    bit = c->value;
  } else {
    throw std::runtime_error("Bit index must be constant for reading");
  }

  tacky::Temporary dst = make_temp();
  emit(tacky::BitCheck{target, bit, dst});

  return dst;
}

tacky::Val IRGenerator::visitMemberAccess(const MemberAccessExpr *expr) {
  // Check for module access (e.g. adc.init) OR static class member
  // (MyClass.CONST)
  if (auto *varExpr = dynamic_cast<const VariableExpr *>(expr->object.get())) {
    // Try to resolve as mangled global
    std::string mangled_name = varExpr->name + "_" + expr->member;

    // Check resolved globals/constants
    if (globals.contains(mangled_name)) {
      const auto &sym = globals.at(mangled_name);
      if (sym.is_memory_address)
        return tacky::MemoryAddress{sym.value, sym.type};
      return tacky::Constant{sym.value};
    }
    if (mutable_globals.contains(mangled_name)) {
      return tacky::Variable{mangled_name, mutable_globals.at(mangled_name)};
    }

    if (modules.contains(varExpr->name)) {
      // It was a module check, and we failed to find it in globals.
      // But maybe it's a function reference?

      if (function_params.contains(mangled_name) ||
          function_return_types.contains(mangled_name)) {
        return tacky::Variable{mangled_name, DataType::UINT8};
      }
      throw std::runtime_error("Unknown module member: " + mangled_name);
    }

    // Also check for functions (static methods) of classes
    if (function_params.contains(mangled_name) ||
        function_return_types.contains(mangled_name)) {
      return tacky::Variable{mangled_name, DataType::UINT8};
    }

    // Fallback: if object is an imported class name, try resolving the member
    // as a module-level constant from the class's source module.
    // This handles Pin.OUTPUT where OUTPUT is defined at module scope.
    if (imported_aliases.contains(varExpr->name)) {
      std::string mod_name = imported_aliases.at(varExpr->name);
      std::string mod_prefix = mod_name;
      std::replace(mod_prefix.begin(), mod_prefix.end(), '.', '_');
      std::string mod_mangled = mod_prefix + "_" + expr->member;
      if (globals.contains(mod_mangled)) {
        const auto &sym = globals.at(mod_mangled);
        if (sym.is_memory_address)
          return tacky::MemoryAddress{sym.value, sym.type};
        return tacky::Constant{sym.value};
      }
      // Also check class-level constant with full prefix
      std::string class_mangled =
          mod_prefix + "_" + varExpr->name + "_" + expr->member;
      if (globals.contains(class_mangled)) {
        const auto &sym = globals.at(class_mangled);
        if (sym.is_memory_address)
          return tacky::MemoryAddress{sym.value, sym.type};
        return tacky::Constant{sym.value};
      }
    }
  }

  if (expr->member == "value") {
    // Get the underlying pointer variable
    tacky::Val obj = visitExpression(expr->object.get());

    // Look up the variable's type to propagate to MemoryAddress
    DataType var_type = DataType::UINT8;  // default

    if (auto *var = std::get_if<tacky::Variable>(&obj)) {
      // Check if we have type information for this variable
      if (variable_types.contains(var->name)) {
        var_type = variable_types[var->name];
      }

      // Update the variable's type field
      var->type = var_type;
    }

    return obj;
  }

  // Fallback for general member access: flattened to obj_member
  tacky::Val objVal = visitExpression(expr->object.get());
  std::string base_name;
  if (auto *v = std::get_if<tacky::Variable>(&objVal)) {
    base_name = v->name;
  } else if (auto *t = std::get_if<tacky::Temporary>(&objVal)) {
    base_name = t->name;
  }

  if (!base_name.empty()) {
    while (variable_aliases.contains(base_name)) {
      base_name = variable_aliases.at(base_name);
    }
    std::string flattened_name = base_name + "_" + expr->member;
    // Check if it's a known global/constant (e.g. self.CONST)
    if (constant_variables.contains(flattened_name)) {
      return tacky::Constant{constant_variables.at(flattened_name)};
    }
    if (globals.contains(flattened_name)) {
      const auto &sym = globals.at(flattened_name);
      if (sym.is_memory_address)
        return tacky::MemoryAddress{sym.value, sym.type};
      return tacky::Constant{sym.value};
    }
    return tacky::Variable{flattened_name, DataType::UINT8};
  }

  throw std::runtime_error("Unknown member access: " + expr->member);
}

tacky::Val IRGenerator::visitCall(const CallExpr *expr) {
  // Resolve callee name
  std::string callee;
  if (const auto var = dynamic_cast<const VariableExpr *>(expr->callee.get())) {
    callee = resolve_callee(var->name);
  } else if (const auto mem =
                 dynamic_cast<const MemberAccessExpr *>(expr->callee.get())) {
    // Check if object is a module or class name (e.g., uart.init() -> uart_init,
    // MathUtils.add() -> MathUtils_add)
    bool resolved_as_module = false;
    if (const auto varExpr =
            dynamic_cast<const VariableExpr *>(mem->object.get())) {
      if (modules.contains(varExpr->name)) {
        // Module call: flatten to module_method
        std::string mangled_mod = varExpr->name;
        std::replace(mangled_mod.begin(), mangled_mod.end(), '.', '_');
        callee = mangled_mod + "_" + mem->member;
        resolved_as_module = true;
      } else if (class_names.contains(varExpr->name)) {
        // Static class method call: flatten to ClassName_method
        callee = current_module_prefix + varExpr->name + "_" + mem->member;
        resolved_as_module = true;
      }
    }
    if (!resolved_as_module) {
      tacky::Val objVal = visitExpression(mem->object.get());
      if (const auto var = std::get_if<tacky::Variable>(&objVal)) {
        // For now, flatten to object_method.
        if (instance_classes.contains(var->name)) {
          callee = instance_classes[var->name] + "_" + mem->member;
        } else {
          callee = var->name + "_" + mem->member;
        }
      } else if (const auto addr =
                     std::get_if<tacky::MemoryAddress>(&objVal)) {
        // Accessing member of a register? (e.g. PORT.bit)
        callee = "MemoryAddress_" + std::to_string(addr->address) + "_" +
                 mem->member;
      } else {
        throw std::runtime_error(
            "Complex member access in call not yet supported");
      }
    }
  } else {
    throw std::runtime_error("Indirect calls not yet supported");
  }

  // Constructor support: Class() -> Class___init__()
  if (inline_functions.contains(callee + "___init__")) {
    callee += "___init__";
  }

  // Handle intrinsics first
  if (callee == "asm") {
    if (expr->args.size() != 1) {
      throw std::runtime_error("asm() expects exactly one string argument");
    }
    // Get the raw string value directly from the AST (not the string ID)
    if (auto *str = dynamic_cast<const StringLiteral *>(expr->args[0].get())) {
      emit(tacky::InlineAsm{str->value});
      return std::monostate{};
    }
    // Check if it's a constant variable holding a string
    if (auto *var = dynamic_cast<const VariableExpr *>(expr->args[0].get())) {
      std::string resolved = current_inline_prefix + var->name;
      // Look up in string_literal_ids reverse mapping is not available,
      // so we require a literal string argument
      throw std::runtime_error(
          "asm() argument must be a string literal, got variable '" +
          var->name + "'");
    }
    throw std::runtime_error(
        "asm() argument must be a compile-time string literal");
  }

  // Handle uart_send_string(s) / uart_send_string_ln(s) intrinsics.
  // Emits UARTSendString{text, add_newline} — handled by AVR backend as a flash
  // string pool entry sent via LPM+Z loop.  Other backends ignore this instruction.
  if (callee == "uart_send_string" || callee == "uart_send_string_ln") {
    bool add_nl = (callee == "uart_send_string_ln");
    if (expr->args.size() != 1) {
      throw std::runtime_error(callee + "() expects exactly one string argument");
    }
    // Resolve the argument: accept a string literal or a const[str] variable.
    auto resolve_str_arg = [&](const Expression *arg_expr) -> std::string {
      if (const auto *lit = dynamic_cast<const StringLiteral *>(arg_expr)) {
        return lit->value;
      }
      if (const auto *var = dynamic_cast<const VariableExpr *>(arg_expr)) {
        std::string key = current_inline_prefix + var->name;
        for (int depth = 0; depth < 20; ++depth) {
          if (str_constant_variables.contains(key))
            return str_constant_variables.at(key);
          if (variable_aliases.contains(key))
            key = variable_aliases.at(key);
          else
            break;
        }
      }
      throw std::runtime_error(
          callee + "() argument must be a compile-time string constant");
    };
    std::string text = resolve_str_arg(expr->args[0].get());
    emit(tacky::UARTSendString{text, add_nl});
    return std::monostate{};
  }

  // Handle ptr(addr) intrinsic
  if (callee == "ptr" && intrinsic_names.contains("ptr")) {
    if (expr->args.size() != 1) {
      throw std::runtime_error("ptr() expects exactly one argument");
    }

    int address = 0;
    tacky::Val arg_val = visitExpression(expr->args[0].get());
    if (auto c = std::get_if<tacky::Constant>(&arg_val)) {
      address = c->value;
    } else {
      throw std::runtime_error("ptr() argument must be a constant expression");
    }

    return tacky::MemoryAddress{address, DataType::UINT8};
  }

  // ptr used without importing from pymcu.types
  if (callee == "ptr" && !intrinsic_names.contains("ptr")) {
    std::cerr << "[Warning] 'ptr' is not recognized as an intrinsic. "
              << "Did you forget to import from pymcu.types?\n";
    return tacky::Constant{0};
  }

  // Handle const(value) intrinsic — compile-time constant enforcement
  if (callee == "const" && intrinsic_names.contains("const")) {
    if (expr->args.size() != 1) {
      throw std::runtime_error("const() expects exactly one argument");
    }
    tacky::Val arg_val = visitExpression(expr->args[0].get());
    if (auto c = std::get_if<tacky::Constant>(&arg_val)) {
      return *c;
    }
    throw std::runtime_error(
        "const() argument must be a compile-time constant expression");
  }

  // Inlining Support
  if (inline_functions.contains(callee)) {
    const FunctionDef *func = inline_functions.at(callee);
    std::string exit_label = make_label();

    // Calculate new prefix but don't set it yet
    int new_depth = inline_depth + 1;
    std::string new_prefix =
        "inline" + std::to_string(new_depth) + "." + func->name + ".";

    std::optional<tacky::Temporary> result;

    if (func->return_type != "void" && func->return_type != "None") {
      result = make_temp(resolve_type(func->return_type));
    }

    // Evaluate args in CURRENT context
    std::vector<tacky::Val> argValues;

    // Detect constructor calls (___init__) for zero-cost self binding
    bool is_constructor =
        callee.size() > 9 &&
        callee.substr(callee.size() - 9) == "___init__";
    size_t param_offset = 0;

    // If it was an instance method call, bind 'self' via alias (zero-cost)
    if (!is_constructor) {
      if (const auto mem =
              dynamic_cast<const MemberAccessExpr *>(expr->callee.get())) {
        tacky::Val objVal = visitExpression(mem->object.get());
        if (const auto var = std::get_if<tacky::Variable>(&objVal)) {
          if (instance_classes.contains(var->name)) {
            // Zero-cost: bind self via alias instead of passing as arg.
            // The self parameter will be resolved through variable_aliases
            // in member access expressions (self.x → instance_x).
            std::string self_name = new_prefix + "self";
            variable_aliases[self_name] = var->name;
            instance_classes[self_name] = instance_classes[var->name];
            param_offset = 1;  // Skip self in param binding
          }
        }
      }
    } else {
      // Constructor: skip 'self' param — it's bound via alias, not passed
      param_offset = 1;
    }

    // Separate positional and keyword arguments
    // Also track raw string literal args (parallel to argValues) for const[str] binding
    std::map<std::string, tacky::Val> kwArgValues;
    std::map<std::string, std::string> raw_kw_str_args;
    std::vector<const StringLiteral *> raw_str_args;
    for (const auto &arg : expr->args) {
      if (auto *kw = dynamic_cast<const KeywordArgExpr *>(arg.get())) {
        kwArgValues[kw->key] = visitExpression(kw->value.get());
        if (auto *s = dynamic_cast<const StringLiteral *>(kw->value.get()))
          raw_kw_str_args[kw->key] = s->value;
      } else {
        raw_str_args.push_back(
            dynamic_cast<const StringLiteral *>(arg.get()));
        argValues.push_back(visitExpression(arg.get()));
      }
    }

    // Switch context
    inline_depth++;
    std::string saved_prefix = current_inline_prefix;
    current_inline_prefix = new_prefix;

    // Deduce and restore the module prefix of the callee so that
    // sibling functions in the same module resolve correctly during inlining.
    std::string saved_module_prefix = current_module_prefix;
    if (func->name.size() < callee.size()) {
      current_module_prefix = callee.substr(0, callee.size() - func->name.size());
    }

    // Track instance type for self if it's a method
    if (method_instance_types.contains(callee)) {
      instance_classes[new_prefix + "self"] = method_instance_types.at(callee);
    }

    // Zero-cost constructor: bind 'self' to the target instance variable
    if (is_constructor && !pending_constructor_target.empty()) {
      std::string self_name = new_prefix + "self";
      variable_aliases[self_name] = pending_constructor_target;
      // Track instance type so nested method calls on self resolve correctly
      std::string class_prefix =
          callee.substr(0, callee.size() - 9);  // Strip "___init__"
      instance_classes[self_name] = class_prefix;
      pending_constructor_target.clear();
    }

    inline_stack.push_back({exit_label, result});

    // Assign args to params in NEW context (offset by 1 for constructors)
    // Track which params were bound (by index) so defaults fill the rest
    std::set<size_t> bound_params;

    // 1. Bind positional args
    for (size_t i = 0; i < argValues.size(); ++i) {
      size_t param_idx = i + param_offset;
      if (param_idx >= func->params.size()) break;
      std::string paramName =
          current_inline_prefix + func->params[param_idx].name;
      bound_params.insert(param_idx);

      // Track aliases for properties
      if (const auto v = std::get_if<tacky::Variable>(&argValues[i])) {
        variable_aliases[paramName] = v->name;
        // Erase stale constant entries from previous inline expansions
        constant_variables.erase(paramName);
        str_constant_variables.erase(paramName);
      }
      // Enforce const parameters — must receive compile-time constants
      if (is_const_type(func->params[param_idx].type)) {
        // Special case: const[str] params may be passed as a variable that
        // was itself bound from a const[str] parameter in a parent inline
        // context.  Resolve through the alias / str_constant_variables chain.
        if (func->params[param_idx].type == "const[str]") {
          // 1. Direct string literal argument
          if (i < raw_str_args.size() && raw_str_args[i] != nullptr) {
            str_constant_variables[paramName] = raw_str_args[i]->value;
            continue;
          }
          // 2. Variable that aliases a const[str] from a parent context
          if (const auto *v = std::get_if<tacky::Variable>(&argValues[i])) {
            if (auto str_val = resolve_str_constant(v->name)) {
              str_constant_variables[paramName] = *str_val;
              continue;
            }
          }
          throw std::runtime_error(
              "Parameter '" + func->params[param_idx].name +
              "' is declared as const[str] and requires a compile-time string "
              "constant value");
        }
        if (!std::get_if<tacky::Constant>(&argValues[i])) {
          throw std::runtime_error(
              "Parameter '" + func->params[param_idx].name +
              "' is declared as const and requires a compile-time constant "
              "value");
        }
        constant_variables[paramName] =
            std::get_if<tacky::Constant>(&argValues[i])->value;
        continue;
      }

      // Track constants for folding — skip emitting the Copy when the arg is
      // a compile-time constant, since the value will be propagated directly
      // through constant_variables (zero-cost: no RAM needed for this param).
      if (const auto c = std::get_if<tacky::Constant>(&argValues[i])) {
        constant_variables[paramName] = c->value;
        continue;  // No Copy needed — constant is folded at use sites
      }

      // Copy arg to param with proper type from function signature.
      // Erase any stale constant_variables entry (e.g., from a previous
      // call where this same param slot held a compile-time constant).
      // Without this, body expressions that read the parameter would see
      // the old constant instead of the runtime value just stored.
      constant_variables.erase(paramName);
      str_constant_variables.erase(paramName);
      DataType param_type = resolve_type(func->params[param_idx].type);
      emit(tacky::Copy{argValues[i],
                        tacky::Variable{paramName, param_type}});
    }

    // 2. Bind keyword args by matching param names
    for (const auto &[kwName, kwVal] : kwArgValues) {
      bool found = false;
      for (size_t pi = param_offset; pi < func->params.size(); ++pi) {
        if (func->params[pi].name == kwName) {
          std::string paramName =
              current_inline_prefix + func->params[pi].name;
          bound_params.insert(pi);
          found = true;

          if (const auto v = std::get_if<tacky::Variable>(&kwVal)) {
            variable_aliases[paramName] = v->name;
          }
          if (is_const_type(func->params[pi].type)) {
            // Special case: const[str] — may come from a literal or a
            // variable aliasing a parent const[str] parameter.
            if (func->params[pi].type == "const[str]") {
              if (raw_kw_str_args.contains(kwName)) {
                str_constant_variables[paramName] = raw_kw_str_args.at(kwName);
              } else if (const auto *v = std::get_if<tacky::Variable>(&kwVal)) {
                if (auto str_val = resolve_str_constant(v->name)) {
                  str_constant_variables[paramName] = *str_val;
                } else {
                  throw std::runtime_error(
                      "Parameter '" + func->params[pi].name +
                      "' is declared as const[str] and requires a compile-time "
                      "string constant value");
                }
              }
            } else {
              if (!std::get_if<tacky::Constant>(&kwVal)) {
                throw std::runtime_error(
                    "Parameter '" + func->params[pi].name +
                    "' is declared as const and requires a compile-time "
                    "constant value");
              }
              constant_variables[paramName] =
                  std::get_if<tacky::Constant>(&kwVal)->value;
            }
          } else if (const auto c = std::get_if<tacky::Constant>(&kwVal)) {
            constant_variables[paramName] = c->value;
          } else {
            DataType param_type = resolve_type(func->params[pi].type);
            emit(tacky::Copy{kwVal,
                              tacky::Variable{paramName, param_type}});
          }
          break;
        }
      }
      if (!found) {
        throw std::runtime_error("Unknown keyword argument '" + kwName +
                                  "' in call to " + callee);
      }
    }

    // 3. Fill in defaults for optional params not provided
    for (size_t i = param_offset; i < func->params.size();
         ++i) {
      if (bound_params.contains(i)) continue;
      if (func->params[i].default_value) {
        std::string paramName =
            current_inline_prefix + func->params[i].name;
        tacky::Val defaultVal =
            visitExpression(func->params[i].default_value.get());

        // Enforce const parameters — defaults must also be constants
        if (is_const_type(func->params[i].type)) {
          // const[str] default: the default expression may be a string literal
          if (func->params[i].type == "const[str]") {
            if (const auto *v = std::get_if<tacky::Variable>(&defaultVal)) {
              if (auto str_val = resolve_str_constant(v->name)) {
                str_constant_variables[paramName] = *str_val;
                continue;
              }
            }
            // If it's already stored (e.g., from a StringLiteral that
            // evaluated to a variable key), look it up by paramName.
            // For now, fall through so default integer const checking handles
            // non-string const defaults correctly.
          }
          if (!std::get_if<tacky::Constant>(&defaultVal)) {
            throw std::runtime_error(
                "Default value for const parameter '" +
                func->params[i].name +
                "' must be a compile-time constant");
          }
          constant_variables[paramName] =
              std::get_if<tacky::Constant>(&defaultVal)->value;
          continue;
        }

        if (const auto c = std::get_if<tacky::Constant>(&defaultVal)) {
          constant_variables[paramName] = c->value;
        } else {
          DataType param_type = resolve_type(func->params[i].type);
          emit(tacky::Copy{defaultVal,
                            tacky::Variable{paramName, param_type}});
        }
      }
    }

    // Body — save/restore debug line state so inlined code doesn't
    // suppress debug lines in the calling context
    int saved_last_line = last_line;
    last_line = -1;
    try {
      visitBlock(func->body.get());
    } catch (const CompilerError &) {
      // Already has source location — re-throw as-is
      throw;
    } catch (const std::runtime_error &e) {
      // Propagate error to user's call site (not stdlib internal line)
      int call_line = current_stmt_line > 0 ? current_stmt_line : 1;
      throw CompilerError("CompileError", e.what(), call_line, 1);
    }
    last_line = saved_last_line;

    emit(tacky::Label{exit_label});
    inline_stack.pop_back();

    current_inline_prefix = saved_prefix;
    current_module_prefix = saved_module_prefix;
    inline_depth--;

    if (result) return *result;
    return std::monostate{};
  }

  // Eval args once
  std::vector<tacky::Val> arg_values;
  for (const auto &arg : expr->args) {
    arg_values.push_back(visitExpression(arg.get()));
  }

  // Module function call support (e.g., adc.init -> adc_init)
  // callee string is already resolved at start of function
  if (size_t dot_pos = callee.find('.'); dot_pos != std::string::npos) {
    std::string mod = callee.substr(0, dot_pos);
    if (modules.contains(mod)) {
      // It's a module call, replace dot with underscore
      callee[dot_pos] = '_';
    }
  }

  // PIO Intrinsics Mapping
  if (function_params.contains(callee)) {
    // Validate args count
    const auto &param_names = function_params[callee];
    if (expr->args.size() != param_names.size()) {
      throw std::runtime_error(
          "Function '" + callee + "' expects " +
          std::to_string(param_names.size()) + " arguments, but " +
          std::to_string(expr->args.size()) + " were provided");
    }

    // Emit copies for arguments (Pass-by-value / Static Allocation)
    const auto &param_types = function_param_types.contains(callee)
                                  ? function_param_types.at(callee)
                                  : std::vector<DataType>{};
    for (size_t i = 0; i < expr->args.size(); ++i) {
      std::string param_var_name = callee + "." + param_names[i];
      DataType ptype = (i < param_types.size()) ? param_types[i] : DataType::UINT8;
      emit(tacky::Copy{arg_values[i], tacky::Variable{param_var_name, ptype}});
    }
  }

  if (callee == "pull")
    callee = "__pio_pull";
  else if (callee == "push")
    callee = "__pio_push";
  else if (callee == "out")
    callee = "__pio_out";
  else if (callee == "in_")
    callee = "__pio_in";
  else if (callee == "wait")
    callee = "__pio_wait";

  tacky::Call callInstr;
  callInstr.function_name = callee;
  // callInstr.args = arg_values; // Don't attach args if we copied them?
  // ArgumentsTest expects copies. Backend probably ignores args if copies are
  // done. But keeping them might be safer for backends that support it.
  // However, tests showed generated assembly was missing moves.
  // If I assume backend ignores 'args' field for PIC14, then copies are
  // required. I will leave args empty to be safe and match "Void Call"
  // pattern unless backend uses them. Actually, let's keep args in IR for
  // completeness but ensure copies are emitted.
  callInstr.args = arg_values;

  bool is_pio_intrinsic = callee.starts_with("__pio_") || callee == "delay";

  if (is_pio_intrinsic || (function_return_types.contains(callee) &&
                           (function_return_types[callee] == "void" ||
                            function_return_types[callee] == "None"))) {
    callInstr.dst = std::monostate{};
    emit(callInstr);
    return std::monostate{};
  }

  tacky::Temporary dst = make_temp();
  callInstr.dst = dst;

  emit(callInstr);
  return dst;
}

void IRGenerator::visitClassDef(const ClassDef *classNode) {
  // Phase 2: Static Class Implementation
  if (classNode->body) {
    std::string old_prefix = current_module_prefix;
    current_module_prefix += classNode->name + "_";
    visitStatement(classNode->body.get());
    current_module_prefix = old_prefix;
  }
}