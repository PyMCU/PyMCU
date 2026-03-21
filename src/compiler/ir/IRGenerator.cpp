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
#include <algorithm>
#include <functional>
#include <iostream>
#include <set>
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

// ── Overload helpers ─────────────────────────────────────────────────────────

static std::string datatype_to_suffix_str(DataType dt) {
  switch (dt) {
    case DataType::UINT8:  return "uint8";
    case DataType::UINT16: return "uint16";
    case DataType::UINT32: return "uint32";
    case DataType::INT8:   return "int8";
    case DataType::INT16:  return "int16";
    case DataType::INT32:  return "int32";
    default:               return "uint8";
  }
}

std::string IRGenerator::build_overload_suffix(const std::vector<Param> &params) {
  std::string suffix;
  bool first = true;
  for (const auto &p : params) {
    if (p.name == "self") continue;
    if (!first) suffix += "_";
    first = false;
    suffix += p.type.empty() ? "uint8" : p.type;
  }
  return suffix.empty() ? "void" : suffix;
}

DataType IRGenerator::infer_expr_type(const Expression *expr) const {
  if (dynamic_cast<const BooleanLiteral *>(expr)) return DataType::UINT8;
  if (dynamic_cast<const IntegerLiteral *>(expr)) return DataType::UINT8;
  if (const auto *var = dynamic_cast<const VariableExpr *>(expr)) {
    std::string key = current_inline_prefix + var->name;
    for (int i = 0; i < 20; ++i) {
      if (variable_types.contains(key)) return variable_types.at(key);
      if (variable_aliases.contains(key))
        key = variable_aliases.at(key);
      else
        break;
    }
  }
  if (const auto *bin = dynamic_cast<const BinaryExpr *>(expr)) {
    DataType lt = infer_expr_type(bin->left.get());
    DataType rt = infer_expr_type(bin->right.get());
    return static_cast<DataType>(
        std::max(static_cast<int>(lt), static_cast<int>(rt)));
  }
  return DataType::UINT8;
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
  pending_isr_registrations.clear();
  extern_function_map.clear();

  // Always-available backend intrinsics (not tied to any import).
  // uart_send_string / uart_send_string_ln are called by the AVR UART HAL to
  // emit a UARTSendString IR instruction (flash string pool + LPM-Z loop).
  intrinsic_names.insert("uart_send_string");
  intrinsic_names.insert("uart_send_string_ln");
  // T2.4: Type cast intrinsics.
  for (const char *t : {"uint8", "uint16", "uint32", "int8", "int16", "int32"})
    intrinsic_names.insert(t);
  intrinsic_names.insert("print");     // Python-style print → UART output
  intrinsic_names.insert("sleep_ms");  // time.sleep_ms → delay_ms intrinsic
  intrinsic_names.insert("sleep_us");  // time.sleep_us → delay_us intrinsic
  intrinsic_names.insert("len");       // compile-time array/list length
  intrinsic_names.insert("sum");       // sum(list) — compile-time or unrolled addition
  intrinsic_names.insert("any");       // any(list) — OR chain
  intrinsic_names.insert("all");       // all(list) — AND chain
  intrinsic_names.insert("hex");       // hex(n) — compile-time integer to hex string
  intrinsic_names.insert("bin");       // bin(n) — compile-time integer to binary string
  intrinsic_names.insert("str");       // str(n) — compile-time integer to decimal string
  intrinsic_names.insert("pow");       // pow(x,n) — compile-time constant fold x**n
  intrinsic_names.insert("zip");       // zip(a,b) — compile-time unrolled paired iteration
  intrinsic_names.insert("reversed");  // reversed(iter) — compile-time reversed iteration
  intrinsic_names.insert("divmod");    // divmod(a,b) — quotient + remainder

  // Inject compile-time constants from device configuration.
  // __FREQ__ is the canonical public name; __FREQUENCY__ is kept for
  // backwards-compatibility with any existing internal HAL code.
  if (config.frequency > 0) {
    constant_variables["__FREQ__"] = static_cast<int>(config.frequency);
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
      intrinsic_names.insert("compile_isr");
    }
    // `import time` or `import time as t` — register the module alias so that
    // `time.sleep_ms(...)` / `t.sleep_ms(...)` resolves via the module-call path.
    if (imp->symbols.empty()) {
      const std::string &mod_key =
          imp->module_alias.empty() ? imp->module_name : imp->module_alias;
      modules.emplace(mod_key, ModuleScope{});
    }
    // Record imported symbols (using alias if provided)
    for (const auto &sym : imp->symbols) {
      const std::string &key =
          imp->aliases.count(sym) ? imp->aliases.at(sym) : sym;
      imported_aliases[key] = imp->module_name;
      if (imp->aliases.count(sym))
        alias_to_original[key] = sym;
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
        intrinsic_names.insert("compile_isr");
      }
      // Register imported aliases for cross-module resolution
      // (e.g., gpio.py imports pin_set_mode from _gpio.atmega328p)
      for (const auto &sym : imp->symbols) {
        const std::string &key =
            imp->aliases.count(sym) ? imp->aliases.at(sym) : sym;
        imported_aliases[key] = imp->module_name;
        if (imp->aliases.count(sym))
          alias_to_original[key] = sym;
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
        // Skip aliased imports (e.g. "from X import UART as _UART") — the
        // alias means the symbol is used internally, not re-exported under
        // the same name.  Re-exporting it would overwrite any class with the
        // same original name that the destination module defines itself.
        if (imp->aliases.count(sym)) continue;

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

  // Apply compile_isr() registrations: mark handler functions as ISRs.
  // This runs after all functions are compiled so ir_program.functions is full.
  for (auto &[bare_name, vec] : pending_isr_registrations) {
    bool found = false;
    for (auto &fn : ir_program.functions) {
      // Match exact name OR module-prefixed name ending with "_<bare_name>".
      if (fn.name == bare_name ||
          (fn.name.size() > bare_name.size() &&
           fn.name[fn.name.size() - bare_name.size() - 1] == '_' &&
           fn.name.substr(fn.name.size() - bare_name.size()) == bare_name)) {
        fn.is_interrupt = true;
        fn.interrupt_vector = vec;
        found = true;
        break;
      }
    }
    if (!found) {
      throw std::runtime_error(
          "compile_isr(): function '" + bare_name +
          "' not found. Ensure the handler is a top-level function defined in "
          "the same translation unit.");
    }
  }
  pending_isr_registrations.clear();

  // Pass mutable global variable names and types to the backend for RAM
  // allocation
  for (const auto &[name, type] : mutable_globals) {
    ir_program.globals.push_back(tacky::Variable{name, type});
  }

  // Collect unique C symbol names from @extern-decorated functions.
  // The AVR backend emits ".extern symbol" for each entry.
  std::set<std::string> seen_extern;
  for (const auto &[_fname, sym] : extern_function_map) {
    if (seen_extern.insert(sym).second)
      ir_program.extern_symbols.push_back(sym);
  }

  loop_stack.clear();
  extern_function_map.clear();
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

  // When inside an inline expansion, check the inline-prefix scope FIRST so
  // that inline params (e.g. "inline1.divmod8.b" = 3) take priority over any
  // same-named variable in the outer function scope (e.g. "main.b" = 7).
  if (!current_inline_prefix.empty()) {
    std::string inline_name = current_inline_prefix + name;
    if (constant_variables.contains(inline_name)) {
      return tacky::Constant{constant_variables.at(inline_name)};
    }
    if (constant_address_variables.contains(inline_name)) {
      return tacky::MemoryAddress{constant_address_variables.at(inline_name)};
    }
  }

  // Also check constant_variables directly for names that aren't in
  // mutable_globals (like flattened ones)
  if (!current_function.empty() && current_inline_prefix.empty()) {
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

  // Check str_constant_variables: a string constant propagated through inline
  // parameter chains (e.g. const[str] or str params carrying "PB0") must also
  // resolve to Constant so that match/case arms can be constant-folded.
  if (auto str_val = resolve_str_constant(local_name)) {
    auto it = string_literal_ids.find(*str_val);
    if (it != string_literal_ids.end()) {
      return tacky::Constant{it->second};
    }
    // String not yet registered — assign a new ID and return it.
    string_literal_ids[*str_val] = next_string_id;
    string_id_to_str[next_string_id] = *str_val;
    return tacky::Constant{next_string_id++};
  }

  DataType type = DataType::UINT8;
  if (variable_types.contains(local_name)) {
    type = variable_types.at(local_name);
  }

  // Follow variable_aliases chain before returning, but ONLY for inline
  // parameter transfer slots (names with 2+ dots, e.g.
  // "inline1.print_byte.value") which are intentionally excluded from register
  // allocation.  Stop if the chain passes through a Temporary name (tmp_N) —
  // those arise from copy-value tracking (x = x+1 → alias[x]=tmp_N) and
  // should not be followed when the variable is read, only the Copy instruction
  // provides the write.
  {
    auto dot_count = [](const std::string &s) {
      return static_cast<int>(std::count(s.begin(), s.end(), '.'));
    };
    auto is_temp = [](const std::string &s) {
      return s.size() > 4 && s.substr(0, 4) == "tmp_";
    };
    if (dot_count(local_name) >= 2) {
      std::string resolved = local_name;
      std::string last_non_temp = local_name;
      for (int depth = 0; depth < 20; ++depth) {
        auto it = variable_aliases.find(resolved);
        if (it == variable_aliases.end()) break;
        const std::string &next = it->second;
        if (is_temp(next)) {
          // If the temp has a known constant (e.g. from an inline function
          // that returns a constant like select_bit), propagate it directly.
          if (constant_variables.contains(next))
            return tacky::Constant{constant_variables.at(next)};
          if (constant_address_variables.contains(next))
            return tacky::MemoryAddress{constant_address_variables.at(next)};
          break;  // temp from copy-value tracking, not a constant — stop
        }
        resolved = next;
        last_non_temp = resolved;
      }
      if (last_non_temp != local_name) {
        if (constant_variables.contains(last_non_temp)) {
          return tacky::Constant{constant_variables.at(last_non_temp)};
        }
        if (constant_address_variables.contains(last_non_temp)) {
          return tacky::MemoryAddress{constant_address_variables.at(last_non_temp)};
        }
        DataType resolved_type = DataType::UINT8;
        if (variable_types.contains(last_non_temp)) {
          resolved_type = variable_types.at(last_non_temp);
        }
        return tacky::Variable{last_non_temp, resolved_type};
      }
    }
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
    // Use the original symbol name if this is an alias (e.g. _Pin -> Pin)
    const std::string &original =
        alias_to_original.count(name) ? alias_to_original.at(name) : name;
    return mangled_mod + "_" + original;
  }

  // Check if it's already a mangled name in current module (or ancestor prefix).
  // When called from inside a class method inline expansion, current_module_prefix
  // includes the class component (e.g. "module_ClassName_"). We progressively
  // strip trailing _Component_ segments to also find module-level sibling functions.
  {
    std::string prefix_try = current_module_prefix;
    while (!prefix_try.empty()) {
      std::string candidate = prefix_try + name;
      if (inline_functions.contains(candidate) ||
          function_params.contains(candidate)) {
        return candidate;
      }
      // Strip the last underscore-delimited component (e.g. "ClassName_")
      if (prefix_try.size() < 2) break;
      size_t last_sep = prefix_try.rfind('_', prefix_try.size() - 2);
      if (last_sep == std::string::npos) break;
      prefix_try = prefix_try.substr(0, last_sep + 1);
    }
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

      // Module-level bytearray: register in array_sizes and module_sram_arrays now
      // (visitVarDecl is only called inside function bodies, so we must handle this
      // here to allow non-inline functions to access the array with variable indices).
      if (type == "bytearray" && initializer) {
        if (const auto *call = dynamic_cast<const CallExpr *>(initializer)) {
          const auto *callee = dynamic_cast<const VariableExpr *>(call->callee.get());
          if (callee && callee->name == "bytearray" && !call->args.empty()) {
            int count = 0;
            const Expression *arg0 = call->args[0].get();
            if (const auto *il = dynamic_cast<const IntegerLiteral *>(arg0))
              count = il->value;
            if (count > 0) {
              // Use bare name (no module prefix) to match visitVarDecl's qualified key.
              array_sizes[name]      = count;
              array_elem_types[name] = DataType::UINT8;
              module_sram_arrays.insert(name);
            }
          }
        }
      }
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

      // Module-level TYPE[N] array: register in array_sizes and module_sram_arrays now.
      // This handles e.g. "_rx_buf: uint8[16] = bytearray(16)" at module scope.
      // visitAnnAssign is only called inside function bodies, so we must pre-register
      // here to allow non-inline functions to access the array with variable indices.
      {
        auto bracket = type.find('[');
        auto close   = type.rfind(']');
        if (bracket != std::string::npos && close != std::string::npos &&
            close == type.size() - 1 && close > bracket + 1) {
          std::string inner = type.substr(bracket + 1, close - bracket - 1);
          bool is_number = !inner.empty() &&
                           std::all_of(inner.begin(), inner.end(), ::isdigit);
          if (is_number) {
            int count = std::stoi(inner);
            DataType elem_dt = resolve_type(type.substr(0, bracket));
            // Use bare name (no module prefix) to match visitAnnAssign's qualified key.
            array_sizes[name]      = count;
            array_elem_types[name] = elem_dt;
            module_sram_arrays.insert(name);
          }
        }
      }
    } else if (const auto classDef =
                   dynamic_cast<const ClassDef *>(stmt.get())) {
      // Recursive scan for class static fields
      if (classDef->body) {
        std::string old_prefix = current_module_prefix;
        current_module_prefix += classDef->name + "_";

        // T2.1: Enum classes fold ALL fields to integer constants.
        bool is_enum = false;
        for (const auto &base : classDef->bases)
          if (base == "Enum" || base == "IntEnum") is_enum = true;

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

                if (is_all_upper || is_enum) {
                  globals[current_module_prefix + name] =
                      SymbolInfo{false, val};
                } else {
                  mutable_globals[current_module_prefix + name] =
                      resolve_type(type);
                }
              } catch (...) {
                if (!is_enum)
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

    if (func->is_extern) {
      // @extern: no IR body; call sites emit CALL to the C symbol directly.
      extern_function_map[full_name] = func->extern_symbol;
    } else if (func->is_inline) {
      if (inline_functions.contains(full_name)) {
        // Overload collision: mangle the existing entry on first collision.
        if (!overloaded_functions.contains(full_name)) {
          const FunctionDef *existing = inline_functions.at(full_name);
          std::string existing_sfx = build_overload_suffix(existing->params);
          inline_functions[full_name + "___" + existing_sfx] = existing;
          inline_functions.erase(full_name);
          overloaded_functions.insert(full_name);
        }
        // Register new overload under its mangled key.
        std::string new_sfx = build_overload_suffix(func->params);
        inline_functions[full_name + "___" + new_sfx] = func.get();
      } else {
        inline_functions[full_name] = func.get();
        if (scope) scope->inline_functions[func->name] = func.get();
      }
    } else {
      functions_to_compile.push_back({current_module_prefix, func.get(), current_source_file});
    }
  }

  // 2. Class methods
  for (const auto &stmt : ast.global_statements) {
    if (const auto classDef = dynamic_cast<const ClassDef *>(stmt.get())) {
      // T2.1: Enum classes have no methods — skip entirely.
      bool is_enum = false;
      for (const auto &base : classDef->bases)
        if (base == "Enum" || base == "IntEnum") is_enum = true;
      if (is_enum) continue;

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

              if (func->is_property_setter) {
                // Register setter under a mangled key to avoid colliding with
                // the getter that has the same Python function name.
                std::string setter_key = full_name + "___setter";
                inline_functions[setter_key] = func;
                // Map "ClassName.property_name" -> setter key for visitAssign
                std::string class_name =
                    class_prefix.substr(0, class_prefix.size() - 1);
                property_setters[class_name + "." + func->property_name] =
                    setter_key;
              } else if (func->is_inline) {
                if (inline_functions.contains(full_name)) {
                  // Method overload collision within the same class.
                  if (!overloaded_functions.contains(full_name)) {
                    const FunctionDef *existing = inline_functions.at(full_name);
                    std::string existing_sfx = build_overload_suffix(existing->params);
                    inline_functions[full_name + "___" + existing_sfx] = existing;
                    inline_functions.erase(full_name);
                    overloaded_functions.insert(full_name);
                  }
                  std::string new_sfx = build_overload_suffix(func->params);
                  inline_functions[full_name + "___" + new_sfx] = func;
                } else {
                  inline_functions[full_name] = func;
                }
              } else {
                functions_to_compile.push_back({current_module_prefix, func, current_source_file});
              }
              if (!func->is_property_setter) {
                method_instance_types[full_name] = current_module_prefix.substr(
                    0, current_module_prefix.size() - 1);
              }
            }
          }
        }

        // ── Single-level class inheritance ───────────────────────────────────
        // After registering the child's own methods, inherit any @inline methods
        // from base classes that the child does NOT already override.
        for (const auto &base_name : classDef->bases) {
          // Resolve the base's full prefix: try the current module scope first,
          // then the bare name (for same-file or already-imported bases).
          std::string base_prefix = old_prefix + base_name + "_";
          // Check alias_to_original in case base was imported as an alias.
          auto resolve_base = [&]() -> std::string {
            // Direct: old_prefix + base_name + "_"
            // e.g. class LED(GPIODevice): base_prefix = "GPIODevice_" (no mod prefix in main)
            if (!old_prefix.empty()) {
              // Try with and without module prefix.
              for (const auto &[key, _] : inline_functions) {
                if (key.find(base_prefix) == 0) return base_prefix;
              }
            }
            // Try bare name (e.g. imported class whose module prefix was stripped).
            std::string bare = base_name + "_";
            for (const auto &[key, _] : inline_functions) {
              if (key.find(bare) == 0) return bare;
            }
            return base_prefix; // fallback
          };
          std::string resolved_base_prefix = resolve_base();

          // Record base prefix for super() resolution.
          std::string child_class_name = class_prefix.substr(
              0, class_prefix.size() - 1); // strip trailing "_"
          class_base_prefixes[child_class_name] = resolved_base_prefix;

          // Collect methods to inherit (snapshot to avoid mutation during iteration).
          std::vector<std::pair<std::string, const FunctionDef *>> to_inherit;
          for (const auto &[key, val] : inline_functions) {
            if (key.find(resolved_base_prefix) == 0) {
              std::string method_suffix = key.substr(resolved_base_prefix.size());
              std::string child_key = class_prefix + method_suffix;
              // Inherit only if child has not already defined/overridden this method.
              if (!inline_functions.contains(child_key)) {
                to_inherit.emplace_back(child_key, val);
              }
            }
          }
          for (auto &[child_key, val] : to_inherit) {
            inline_functions[child_key] = val;
            // Copy metadata.
            std::string src_key = resolved_base_prefix +
                                  child_key.substr(class_prefix.size());
            if (function_params.contains(src_key))
              function_params[child_key] = function_params[src_key];
            if (function_param_types.contains(src_key))
              function_param_types[child_key] = function_param_types[src_key];
            if (function_return_types.contains(src_key))
              function_return_types[child_key] = function_return_types[src_key];
            // Register method_instance_types so instance dispatch works.
            method_instance_types[child_key] =
                class_prefix.substr(0, class_prefix.size() - 1);
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

  if (const auto str = dynamic_cast<const StringLiteral *>(expr)) {
    // Assign or retrieve a string ID so module-level string constants
    // (e.g. D2 = "PD2") can be constant-folded through match/case.
    if (string_literal_ids.find(str->value) == string_literal_ids.end()) {
      string_literal_ids[str->value] = next_string_id;
      string_id_to_str[next_string_id] = str->value;
      next_string_id++;
    }
    return string_literal_ids[str->value];
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

  // Pre-scan the body to detect which arrays are accessed with variable indices.
  // This must run before visitBlock so that visitAnnAssign chooses the right strategy.
  arrays_with_variable_index.clear();
  scanForVariableIndexedArrays(funcNode->body->statements, full_name + ".");

  visitBlock(funcNode->body.get());

  if (current_instructions.empty() ||
      !std::holds_alternative<tacky::Return>(current_instructions.back())) {
    emit(tacky::Return{std::monostate{}});
  }

  ir_func.body = current_instructions;
  arrays_with_variable_index.clear();  // reset for next function
  return ir_func;
}

// ---------------------------------------------------------------------------
// Pre-scan: walk the AST to find arrays that are subscripted with a non-
// constant (runtime) index anywhere in the function body.
// ---------------------------------------------------------------------------
void IRGenerator::scanForVariableIndexedArrays(
    const std::vector<std::unique_ptr<Statement>> &stmts,
    const std::string &prefix) {

  // Recursive helpers using lambdas
  std::function<void(const Expression *)> scanExpr;
  std::function<void(const Statement *)>  scanStmt;

  // First mini-pass: collect array names from AnnAssign declarations in this function.
  // We need this because array_sizes is populated during visitAnnAssign (IR generation),
  // which hasn't run yet when the pre-scan executes.
  std::set<std::string> local_arrays;
  std::function<void(const Statement *)> collectArrayDecls;
  collectArrayDecls = [&](const Statement *stmt) {
    if (!stmt) return;
    if (auto *ann = dynamic_cast<const AnnAssign *>(stmt)) {
      // TYPE[N] arrays
      auto br = ann->annotation.find('[');
      auto cl = ann->annotation.rfind(']');
      if (br != std::string::npos && cl != std::string::npos) {
        std::string inner = ann->annotation.substr(br + 1, cl - br - 1);
        if (!inner.empty() && std::all_of(inner.begin(), inner.end(), ::isdigit))
          local_arrays.insert(prefix + ann->target);
      }
      // bytearray annotation (AnnAssign path is not reachable since parser produces
      // VarDecl, but handle defensively)
      if (ann->annotation == "bytearray")
        local_arrays.insert(prefix + ann->target);
    } else if (auto *vd = dynamic_cast<const VarDecl *>(stmt)) {
      // bytearray VarDecl: buf: bytearray = bytearray(N)
      if (vd->var_type == "bytearray")
        local_arrays.insert(prefix + vd->name);
    } else if (auto *block = dynamic_cast<const Block *>(stmt)) {
      for (const auto &s : block->statements) collectArrayDecls(s.get());
    } else if (auto *if_stmt = dynamic_cast<const IfStmt *>(stmt)) {
      if (if_stmt->then_branch) collectArrayDecls(if_stmt->then_branch.get());
      for (const auto &[c, b] : if_stmt->elif_branches) collectArrayDecls(b.get());
      if (if_stmt->else_branch) collectArrayDecls(if_stmt->else_branch.get());
    } else if (auto *wh = dynamic_cast<const WhileStmt *>(stmt)) {
      if (wh->body) collectArrayDecls(wh->body.get());
    }
  };
  for (const auto &s : stmts) collectArrayDecls(s.get());

  // Second pass: look for non-constant subscripts on the collected array names.
  scanExpr = [&](const Expression *expr) {
    if (!expr) return;
    if (auto *idx = dynamic_cast<const IndexExpr *>(expr)) {
      if (auto *ve = dynamic_cast<const VariableExpr *>(idx->target.get())) {
        std::string q = prefix + ve->name;
        // If this is a local array AND the index is not an integer literal → needs runtime access
        if (local_arrays.contains(q) && !dynamic_cast<const IntegerLiteral *>(idx->index.get())) {
          arrays_with_variable_index.insert(q);
        }
      }
      scanExpr(idx->target.get());
      scanExpr(idx->index.get());
    } else if (auto *call = dynamic_cast<const CallExpr *>(expr)) {
      scanExpr(call->callee.get());
      for (const auto &arg : call->args) scanExpr(arg.get());
    } else if (auto *bin = dynamic_cast<const BinaryExpr *>(expr)) {
      scanExpr(bin->left.get());
      scanExpr(bin->right.get());
    } else if (auto *un = dynamic_cast<const UnaryExpr *>(expr)) {
      scanExpr(un->operand.get());
    }
    // IntegerLiteral, VariableExpr, etc. have no sub-expressions to scan
  };

  scanStmt = [&](const Statement *stmt) {
    if (!stmt) return;
    if (auto *assign = dynamic_cast<const AssignStmt *>(stmt)) {
      scanExpr(assign->target.get());
      scanExpr(assign->value.get());
    } else if (auto *ann = dynamic_cast<const AnnAssign *>(stmt)) {
      if (ann->value) scanExpr(ann->value.get());
    } else if (auto *ret = dynamic_cast<const ReturnStmt *>(stmt)) {
      if (ret->value) scanExpr(ret->value.get());
    } else if (auto *expr_stmt = dynamic_cast<const ExprStmt *>(stmt)) {
      scanExpr(expr_stmt->expr.get());
    } else if (auto *if_stmt = dynamic_cast<const IfStmt *>(stmt)) {
      scanExpr(if_stmt->condition.get());
      if (if_stmt->then_branch) scanStmt(if_stmt->then_branch.get());
      for (const auto &[cond, body] : if_stmt->elif_branches) {
        scanExpr(cond.get());
        scanStmt(body.get());
      }
      if (if_stmt->else_branch) scanStmt(if_stmt->else_branch.get());
    } else if (auto *while_stmt = dynamic_cast<const WhileStmt *>(stmt)) {
      scanExpr(while_stmt->condition.get());
      if (while_stmt->body) scanStmt(while_stmt->body.get());
    } else if (auto *block = dynamic_cast<const Block *>(stmt)) {
      for (const auto &s : block->statements) scanStmt(s.get());
    } else if (auto *aug = dynamic_cast<const AugAssignStmt *>(stmt)) {
      scanExpr(aug->target.get());
      scanExpr(aug->value.get());
    }
  };

  for (const auto &s : stmts) scanStmt(s.get());
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
        const std::string &key =
            imp->aliases.count(sym) ? imp->aliases.at(sym) : sym;
        imported_aliases[key] = imp->module_name;
        if (imp->aliases.count(sym))
          alias_to_original[key] = sym;
      }
      // `import time` or `import time as t` inside inline body — register module.
      if (imp->symbols.empty()) {
        const std::string &mod_key =
            imp->module_alias.empty() ? imp->module_name : imp->module_alias;
        modules.emplace(mod_key, ModuleScope{});
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
  if (auto *withStmt = dynamic_cast<const WithStmt *>(stmt))
    return visitWith(withStmt);
  if (auto *assertStmt = dynamic_cast<const AssertStmt *>(stmt))
    return visitAssert(assertStmt);
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
  if (auto *tupleUnpack = dynamic_cast<const TupleUnpackStmt *>(stmt))
    return visitTupleUnpack(tupleUnpack);
  if (auto global = dynamic_cast<const GlobalStmt *>(stmt)) {
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
  // --- Inline multi-return: return (expr0, expr1, ...) ---
  if (stmt->value && !inline_stack.empty() &&
      !inline_stack.back().result_vars.empty()) {
    if (const auto *tup = dynamic_cast<const TupleExpr *>(stmt->value.get())) {
      const auto &ctx = inline_stack.back();
      if (tup->elements.size() != ctx.result_vars.size()) {
        throw std::runtime_error(
            "Tuple return size mismatch: expected " +
            std::to_string(ctx.result_vars.size()) + " elements");
      }
      for (size_t _k = 0; _k < tup->elements.size(); ++_k) {
        tacky::Val elem_val = visitExpression(tup->elements[_k].get());
        DataType dt = DataType::UINT8;
        emit(tacky::Copy{elem_val, tacky::Variable{ctx.result_vars[_k], dt}});
        if (const auto *c = std::get_if<tacky::Constant>(&elem_val))
          constant_variables[ctx.result_vars[_k]] = c->value;
      }
      emit(tacky::Jump{ctx.exit_label});
      return;
    }
  }

  tacky::Val val = std::monostate{};
  if (stmt->value) {
    val = visitExpression(stmt->value.get());
  }

  if (!inline_stack.empty()) {
    auto &ctx = inline_stack.back();
    if (ctx.result_temp.has_value()) {
      // For MemoryAddress returns: distinguish ptr-returning functions from
      // value-returning functions that happen to read a memory-mapped register.
      //   ptr return (e.g. select_port -> ptr[uint8]): propagate address via
      //     constant_address_variables; no Copy emitted (address IS the value).
      //   value return (e.g. eeprom_read -> uint8): emit Copy so the codegen
      //     generates IN/LDS to read the actual byte into a register.
      if (const auto m = std::get_if<tacky::MemoryAddress>(&val)) {
        // Check the declared return type of the inline function being expanded.
        // ptr-returning functions (e.g. select_port -> ptr[uint8]): propagate address.
        // value-returning functions (e.g. eeprom_read -> uint8): emit Copy to read byte.
        bool returns_ptr = false;
        auto rt_it = function_return_types.find(ctx.callee_name);
        if (rt_it != function_return_types.end()) {
          const auto &rt = rt_it->second;
          returns_ptr = (rt.rfind("ptr", 0) == 0) ||
                        (rt.find("ptr") != std::string::npos && rt.find('[') != std::string::npos);
        }
        if (returns_ptr) {
          // ptr-returning inline: propagate address for bit-slice folding, no Copy.
          // Only set if not already assigned (ptr address must not be overwritten by
          // a dead-code fallback return that follows an exhaustive match).
          if (!ctx.result_assigned) {
            constant_address_variables[ctx.result_temp->name] = m->address;
            ctx.result_assigned = true;
          }
          emit(tacky::Jump{ctx.exit_label});
          return;
        }
        // Non-ptr return: the Copy below generates IN/LDS to read the actual byte.
      }

      emit(tacky::Copy{val, ctx.result_temp.value()});
      ctx.result_assigned = true;

      // Track constant returns so that compile-time folding of member assignments
      // works correctly (e.g. select_bit("PB5") → 5 tracked so self._bit = result
      // folds into constant_variables["led._bit"] = 5 for bit-slice resolution).
      // Multi-path inline functions that return different constants on different paths
      // (e.g. i2c_ping returning 0 or 1) are handled correctly by the Optimizer's
      // propagate_copies: it erases multi-definition temps rather than propagating
      // the wrong value, so runtime-conditional returns stay correct end-to-end.
      if (const auto *c = std::get_if<tacky::Constant>(&val)) {
        constant_variables[ctx.result_temp->name] = c->value;
      } else if (const auto v = std::get_if<tacky::Variable>(&val)) {
        variable_aliases[ctx.result_temp->name] = v->name;
      } else if (const auto t = std::get_if<tacky::Temporary>(&val)) {
        variable_aliases[ctx.result_temp->name] = t->name;
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
  // T1.2: And/Or in condition position use sequential conditional jumps (no temp).
  if (auto binExpr = dynamic_cast<const BinaryExpr *>(cond)) {
    if (binExpr->op == BinaryOp::And || binExpr->op == BinaryOp::Or) {
      bool is_and = (binExpr->op == BinaryOp::And);

      // Helper: emit a sub-condition jump, falling back to visitExpression if needed.
      auto emit_sub = [&](const Expression *sub, const std::string &label,
                          bool if_true) {
        int r = emit_optimized_conditional_jump(sub, label, if_true);
        if (r != 0) return;
        tacky::Val v = visitExpression(sub);
        if (auto c = std::get_if<tacky::Constant>(&v)) {
          bool cval = (c->value != 0);
          if (cval == if_true) emit(tacky::Jump{label});
          return;
        }
        if (if_true)
          emit(tacky::JumpIfNotZero{v, label});
        else
          emit(tacky::JumpIfZero{v, label});
      };

      if ((!jump_if_true && is_and) || (jump_if_true && !is_and)) {
        // "jump if And-false" or "jump if Or-true":
        // Jump to target if EITHER operand satisfies the individual condition.
        emit_sub(binExpr->left.get(), target_label, jump_if_true);
        emit_sub(binExpr->right.get(), target_label, jump_if_true);
      } else {
        // "jump if And-true" or "jump if Or-false":
        // Jump to target only when BOTH operands satisfy the individual condition.
        // If left does NOT satisfy it, skip the whole thing.
        std::string skip_label = make_label();
        emit_sub(binExpr->left.get(), skip_label, !jump_if_true);
        emit_sub(binExpr->right.get(), target_label, jump_if_true);
        emit(tacky::Label{skip_label});
      }
      return 1;
    }

    // For in/not in/is/is not: fall back to visitBinary (handled there).
    if (binExpr->op == BinaryOp::In || binExpr->op == BinaryOp::NotIn ||
        binExpr->op == BinaryOp::Is || binExpr->op == BinaryOp::IsNot) {
      return 0;
    }

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
      // PEP 634: Sequence pattern — case [a, b, c]: or case [0xFF, cmd, data]:
      // Destructures a fixed-size array element-by-element.
      if (const auto *seq = dynamic_cast<const ListExpr *>(branch.pattern.get())) {
        // Resolve subject array name.
        std::string arr_name;
        if (const auto *av = std::get_if<tacky::Variable>(&target_val))
          arr_name = av->name;
        else
          throw std::runtime_error("match/case sequence pattern: subject must be an array variable");

        int pat_size = static_cast<int>(seq->elements.size());

        // Compile-time size check.
        if (array_sizes.contains(arr_name) && array_sizes.at(arr_name) != pat_size) {
          // Statically impossible: skip this case entirely.
          emit(tacky::Jump{next_case_label});
          emit(tacky::Label{next_case_label});
          continue;
        }

        bool use_sram = arrays_with_variable_index.contains(arr_name) ||
                        module_sram_arrays.contains(arr_name);
        DataType elem_dt = array_elem_types.contains(arr_name)
                               ? array_elem_types.at(arr_name)
                               : DataType::UINT8;

        // For each element: emit comparison (literal) or deferred binding (name).
        // Collect captures to emit after all checks pass.
        struct CapturePending { int idx; std::string name; };
        std::vector<CapturePending> captures;

        for (int i = 0; i < pat_size; ++i) {
          const Expression *elem = seq->elements[i].get();

          // Load array element at position i.
          tacky::Val elem_val;
          if (use_sram) {
            tacky::Temporary tmp = make_temp(elem_dt);
            emit(tacky::ArrayLoad{arr_name, tacky::Constant{i}, tmp,
                                  elem_dt, pat_size});
            elem_val = tmp;
          } else {
            // Constant-index path: synthetic scalar arr__i.
            elem_val = tacky::Variable{arr_name + "__" + std::to_string(i), elem_dt};
          }

          // Identifier → capture (always matches); literal → equality check.
          if (const auto *ve = dynamic_cast<const VariableExpr *>(elem)) {
            // Identifier element = capture; bind after all checks.
            std::string qname = current_function.empty()
                                    ? ve->name
                                    : current_function + "." + ve->name;
            captures.push_back({i, qname});
          } else {
            // Literal element: must match exactly.
            tacky::Val pat_val = visitExpression(elem);
            tacky::Temporary cmp = make_temp();
            emit(tacky::Binary{tacky::BinaryOp::Equal, elem_val, pat_val, cmp});
            emit(tacky::JumpIfZero{cmp, next_case_label});
          }
        }

        // All element checks passed — emit guard (if any).
        if (branch.guard) {
          tacky::Val g = visitExpression(branch.guard.get());
          emit(tacky::JumpIfZero{g, next_case_label});
        }

        // Emit capture bindings now that we know we matched.
        for (const auto &cap : captures) {
          tacky::Val src;
          if (use_sram) {
            tacky::Temporary tmp = make_temp(elem_dt);
            emit(tacky::ArrayLoad{arr_name, tacky::Constant{cap.idx}, tmp,
                                  elem_dt, pat_size});
            src = tmp;
          } else {
            src = tacky::Variable{arr_name + "__" + std::to_string(cap.idx), elem_dt};
          }
          emit(tacky::Copy{src, tacky::Variable{cap.name, elem_dt}});
          variable_types[cap.name] = elem_dt;
        }

        // Emit as-capture (if any).
        if (!branch.capture_name.empty()) {
          std::string qname = current_function.empty()
                                  ? branch.capture_name
                                  : current_function + "." + branch.capture_name;
          emit(tacky::Copy{target_val, tacky::Variable{qname, elem_dt}});
          variable_types[qname] = elem_dt;
        }

        visitBlock(dynamic_cast<const Block *>(branch.body.get()));
        emit(tacky::Jump{end_label});
        emit(tacky::Label{next_case_label});
        continue;
      }

      // Flatten OR patterns: `case a | b | c:` → alternatives [a, b, c]
      // `a | b` is parsed as BinaryExpr(a, BitOr, b) by the expression parser.
      std::vector<const Expression *> alts;
      std::function<void(const Expression *)> flatten = [&](const Expression *e) {
        if (const auto *bin = dynamic_cast<const BinaryExpr *>(e)) {
          if (bin->op == BinaryOp::BitOr) {
            flatten(bin->left.get());
            flatten(bin->right.get());
            return;
          }
        }
        alts.push_back(e);
      };
      flatten(branch.pattern.get());

      // Evaluate all alt values (string literal visitExpression emits no instructions)
      std::vector<tacky::Val> alt_vals;
      for (const auto *alt : alts) {
        alt_vals.push_back(visitExpression(alt));
      }

      // Try compile-time constant folding: if target and all alts are constants
      const auto *ct = std::get_if<tacky::Constant>(&target_val);
      bool all_alts_const = ct != nullptr;
      for (auto &v : alt_vals) {
        if (!std::get_if<tacky::Constant>(&v)) { all_alts_const = false; break; }
      }

      bool skip_body = false;
      if (all_alts_const) {
        // Pure compile-time: check if target matches any alt
        bool any_match = false;
        for (auto &v : alt_vals) {
          if (std::get<tacky::Constant>(v).value == ct->value) { any_match = true; break; }
        }
        if (!any_match) {
          // Statically false: skip body
          emit(tacky::Jump{next_case_label});
          skip_body = true;
        }
        // else: statically true → fall through to body, no condition jump needed
      } else if (alts.size() == 1) {
        // Single pattern, not compile-time constant: use existing JumpIfZero path
        tacky::Temporary cmp_res = make_temp();
        emit(tacky::Binary{tacky::BinaryOp::Equal, target_val, alt_vals[0], cmp_res});
        emit(tacky::JumpIfZero{cmp_res, next_case_label});
      } else {
        // OR pattern, runtime: jump to body if ANY alt matches
        std::string match_label = make_label();
        for (auto &alt_val : alt_vals) {
          tacky::Temporary cmp = make_temp();
          emit(tacky::Binary{tacky::BinaryOp::Equal, target_val, alt_val, cmp});
          emit(tacky::JumpIfNotZero{cmp, match_label});
        }
        emit(tacky::Jump{next_case_label});
        emit(tacky::Label{match_label});
      }

      if (!skip_body) {
        // PEP 634: guard — emitted after pattern match, before body.
        // If the guard is false, fall through to the next case.
        if (branch.guard) {
          tacky::Val g = visitExpression(branch.guard.get());
          emit(tacky::JumpIfZero{g, next_case_label});
        }
        // PEP 634: capture binding — bind matched subject to name.
        if (!branch.capture_name.empty()) {
          std::string qname = current_function.empty()
                                  ? branch.capture_name
                                  : current_function + "." + branch.capture_name;
          DataType dt = DataType::UINT8;
          if (const auto *v = std::get_if<tacky::Variable>(&target_val)) dt = v->type;
          else if (const auto *t = std::get_if<tacky::Temporary>(&target_val)) dt = t->type;
          emit(tacky::Copy{target_val, tacky::Variable{qname, dt}});
        }
        visitBlock(dynamic_cast<const Block *>(branch.body.get()));
        emit(tacky::Jump{end_label});
      }
    } else {
      // Wildcard Case (_) or bare-name capture (always matches)
      // PEP 634: guard on wildcard/capture.
      if (branch.guard) {
        tacky::Val g = visitExpression(branch.guard.get());
        emit(tacky::JumpIfZero{g, next_case_label});
      }
      // Capture binding for bare-name capture (case x:)
      if (!branch.capture_name.empty()) {
        std::string qname = current_function.empty()
                                ? branch.capture_name
                                : current_function + "." + branch.capture_name;
        DataType dt = DataType::UINT8;
        if (const auto *v = std::get_if<tacky::Variable>(&target_val)) dt = v->type;
        else if (const auto *t = std::get_if<tacky::Temporary>(&target_val)) dt = t->type;
        emit(tacky::Copy{target_val, tacky::Variable{qname, dt}});
        variable_types[qname] = dt;
      }
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
  // General for-in: compile-time unrolling over a string or list constant
  if (stmt->iterable) {
    const Expression *iter = stmt->iterable.get();
    std::string var_key = current_inline_prefix + stmt->var_name;

    // --- String constant iteration ---
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

    if (auto str_opt = get_str(iter)) {
      for (unsigned char c : *str_opt) {
        constant_variables[var_key] = static_cast<int>(c);
        visitStatement(stmt->body.get());
      }
      constant_variables.erase(var_key);
      return;
    }

    // --- Constant list literal: for x in [1, 2, 3] ---
    if (const auto *le = dynamic_cast<const ListExpr *>(iter)) {
      for (const auto &elem : le->elements) {
        int val = 0;
        if (const auto *il = dynamic_cast<const IntegerLiteral *>(elem.get())) {
          val = il->value;
        } else {
          throw std::runtime_error(
              "for-in list iterable elements must be compile-time integer constants.");
        }
        constant_variables[var_key] = val;
        visitStatement(stmt->body.get());
      }
      constant_variables.erase(var_key);
      return;
    }

    // --- range() call in iterable position: for x in range(N) ---
    if (const auto *call = dynamic_cast<const CallExpr *>(iter)) {
      const auto *callee_var = dynamic_cast<const VariableExpr *>(call->callee.get());
      if (callee_var && callee_var->name == "range") {
        auto eval_const = [&](const Expression *e) -> std::optional<int> {
          if (const auto *il = dynamic_cast<const IntegerLiteral *>(e)) return il->value;
          if (const auto *v = dynamic_cast<const VariableExpr *>(e)) {
            std::string k = current_inline_prefix + v->name;
            if (constant_variables.contains(k)) return constant_variables.at(k);
          }
          return std::nullopt;
        };
        int start = 0, stop = 0, step = 1;
        if (call->args.size() == 1) {
          auto sv = eval_const(call->args[0].get());
          if (!sv) throw std::runtime_error(
              "for-in range() argument must be a compile-time constant.");
          stop = *sv;
        } else if (call->args.size() >= 2) {
          auto sv = eval_const(call->args[0].get()), ev = eval_const(call->args[1].get());
          if (!sv || !ev) throw std::runtime_error(
              "for-in range() arguments must be compile-time constants.");
          start = *sv; stop = *ev;
          if (call->args.size() >= 3) {
            auto stv = eval_const(call->args[2].get());
            if (!stv) throw std::runtime_error(
                "for-in range() step must be a compile-time constant.");
            step = *stv;
          }
        } else {
          throw std::runtime_error("for-in range() requires at least one argument.");
        }
        if (step == 0) throw std::runtime_error("for-in range() step cannot be zero.");
        for (int i = start; (step > 0 ? i < stop : i > stop); i += step) {
          constant_variables[var_key] = i;
          visitStatement(stmt->body.get());
        }
        constant_variables.erase(var_key);
        return;
      }
    }

    // --- enumerate() over a const iterable: for i, x in enumerate(list/range) ---
    if (const auto *call = dynamic_cast<const CallExpr *>(iter)) {
      const auto *callee_var = dynamic_cast<const VariableExpr *>(call->callee.get());
      if (callee_var && callee_var->name == "enumerate" &&
          !stmt->var2_name.empty() && call->args.size() == 1) {
        std::string idx_key = current_inline_prefix + stmt->var_name;
        std::string val_key = current_inline_prefix + stmt->var2_name;
        const Expression *inner = call->args[0].get();
        int idx = 0;

        // enumerate([v0, v1, ...])
        if (const auto *le = dynamic_cast<const ListExpr *>(inner)) {
          for (const auto &elem : le->elements) {
            int val = 0;
            if (const auto *il = dynamic_cast<const IntegerLiteral *>(elem.get())) {
              val = il->value;
            } else {
              throw std::runtime_error(
                  "enumerate() list elements must be compile-time integer constants.");
            }
            constant_variables[idx_key] = idx++;
            constant_variables[val_key] = val;
            visitStatement(stmt->body.get());
          }
          constant_variables.erase(idx_key);
          constant_variables.erase(val_key);
          return;
        }

        // enumerate(range(N)) or enumerate(range(start, stop, step))
        if (const auto *rcall = dynamic_cast<const CallExpr *>(inner)) {
          const auto *rv = dynamic_cast<const VariableExpr *>(rcall->callee.get());
          if (rv && rv->name == "range") {
            auto eval_c = [&](const Expression *e) -> std::optional<int> {
              if (const auto *il = dynamic_cast<const IntegerLiteral *>(e)) return il->value;
              if (const auto *v = dynamic_cast<const VariableExpr *>(e)) {
                std::string k = current_inline_prefix + v->name;
                if (constant_variables.contains(k)) return constant_variables.at(k);
              }
              return std::nullopt;
            };
            int rstart = 0, rstop = 0, rstep = 1;
            if (rcall->args.size() == 1) {
              auto sv = eval_c(rcall->args[0].get());
              if (!sv) throw std::runtime_error(
                  "enumerate(range()) argument must be compile-time constant.");
              rstop = *sv;
            } else if (rcall->args.size() >= 2) {
              auto sv = eval_c(rcall->args[0].get()), ev = eval_c(rcall->args[1].get());
              if (!sv || !ev) throw std::runtime_error(
                  "enumerate(range()) arguments must be compile-time constants.");
              rstart = *sv; rstop = *ev;
              if (rcall->args.size() >= 3) {
                auto stv = eval_c(rcall->args[2].get());
                if (!stv) throw std::runtime_error(
                    "enumerate(range()) step must be compile-time constant.");
                rstep = *stv;
              }
            }
            for (int rv = rstart; (rstep > 0 ? rv < rstop : rv > rstop); rv += rstep) {
              constant_variables[idx_key] = idx++;
              constant_variables[val_key] = rv;
              visitStatement(stmt->body.get());
            }
            constant_variables.erase(idx_key);
            constant_variables.erase(val_key);
            return;
          }
        }

        // enumerate(arr) where arr is a fixed-size array — compile-time unroll.
        // For constant-only arrays: use synthetic scalars (arr__k).
        // For variable-index SRAM arrays: emit ArrayLoad per element via visitIndex.
        if (const auto *v = dynamic_cast<const VariableExpr *>(inner)) {
          std::string base;
          int arr_size = -1;
          if (!current_inline_prefix.empty()) {
            std::string k = current_inline_prefix + v->name;
            if (array_sizes.contains(k)) { arr_size = array_sizes.at(k); base = k; }
          }
          if (arr_size < 0 && !current_function.empty()) {
            std::string k = current_function + "." + v->name;
            if (array_sizes.contains(k)) { arr_size = array_sizes.at(k); base = k; }
          }
          if (arr_size < 0 && array_sizes.contains(v->name)) {
            arr_size = array_sizes.at(v->name); base = v->name;
          }
          if (arr_size > 0) {
            DataType elem_dt = array_elem_types.contains(base)
                ? array_elem_types.at(base) : DataType::UINT8;
            bool use_sram = arrays_with_variable_index.contains(base) || module_sram_arrays.contains(base);
            // Compute the qualified loop-variable name that resolve_binding returns.
            // resolve_binding("x") computes local_name = current_function + "." + "x"
            // so we must emit Copies targeting that same qualified name.
            std::string qualified_val;
            if (!current_inline_prefix.empty()) {
              qualified_val = current_inline_prefix + stmt->var2_name;
            } else if (!current_function.empty()) {
              qualified_val = current_function + "." + stmt->var2_name;
            } else {
              qualified_val = stmt->var2_name;
            }
            variable_types[qualified_val] = elem_dt;
            for (int k = 0; k < arr_size; ++k) {
              constant_variables[idx_key] = k;
              if (use_sram) {
                // Variable-index SRAM array: emit ArrayLoad via a synthetic IndexExpr
                // to reuse the existing visitIndex path, then Copy result to the
                // function-qualified loop variable.
                auto syn_target = std::make_unique<VariableExpr>(v->name);
                auto syn_index  = std::make_unique<IntegerLiteral>(k);
                auto syn_idx_expr = std::make_unique<IndexExpr>(
                    std::move(syn_target), std::move(syn_index));
                tacky::Val elem_val = visitIndex(syn_idx_expr.get());
                tacky::Variable val_var{qualified_val, elem_dt};
                emit(tacky::Copy{elem_val, val_var});
              } else {
                // Constant-index scalar array: synthetic scalars arr__k
                // For constant values, use constant_variables so resolve_binding finds them.
                // For runtime scalars, alias qualified_val to the scalar variable.
                std::string elem_key = base + "__" + std::to_string(k);
                if (constant_variables.contains(elem_key)) {
                  constant_variables[val_key] = constant_variables.at(elem_key);
                } else {
                  // Emit a Copy from the scalar variable to the qualified loop var.
                  tacky::Variable src_var{elem_key, elem_dt};
                  tacky::Variable val_var{qualified_val, elem_dt};
                  emit(tacky::Copy{src_var, val_var});
                }
              }
              visitStatement(stmt->body.get());
              constant_variables.erase(val_key);
            }
            constant_variables.erase(idx_key);
            return;
          }
        }

        throw std::runtime_error(
            "enumerate() argument must be a constant list literal, range(N), or a fixed-size array.");
      }
    }

    // --- zip(a, b) over two const iterables: for x, y in zip(list_a, list_b) ---
    if (const auto *call = dynamic_cast<const CallExpr *>(iter)) {
      const auto *callee_var = dynamic_cast<const VariableExpr *>(call->callee.get());
      if (callee_var && callee_var->name == "zip" &&
          !stmt->var2_name.empty() && call->args.size() == 2) {
        std::string key1 = current_inline_prefix + stmt->var_name;
        std::string key2 = current_inline_prefix + stmt->var2_name;
        const Expression *arg0 = call->args[0].get();
        const Expression *arg1 = call->args[1].get();

        // Helper to collect integer values from a list literal or array variable
        auto collect_ints = [&](const Expression *e) -> std::vector<int> {
          if (const auto *le = dynamic_cast<const ListExpr *>(e)) {
            std::vector<int> vals;
            for (const auto &elem : le->elements) {
              if (const auto *il = dynamic_cast<const IntegerLiteral *>(elem.get())) {
                vals.push_back(il->value);
              } else {
                throw std::runtime_error(
                    "zip() list elements must be compile-time integer constants.");
              }
            }
            return vals;
          }
          if (const auto *v = dynamic_cast<const VariableExpr *>(e)) {
            // Try to resolve as a constant-only array (synthetic scalars)
            std::string base;
            int arr_size = -1;
            if (!current_inline_prefix.empty()) {
              std::string k = current_inline_prefix + v->name;
              if (array_sizes.contains(k)) { arr_size = array_sizes.at(k); base = k; }
            }
            if (arr_size < 0 && !current_function.empty()) {
              std::string k = current_function + "." + v->name;
              if (array_sizes.contains(k)) { arr_size = array_sizes.at(k); base = k; }
            }
            if (arr_size < 0 && array_sizes.contains(v->name)) {
              arr_size = array_sizes.at(v->name); base = v->name;
            }
            if (arr_size > 0) {
              std::vector<int> vals;
              for (int k = 0; k < arr_size; ++k) {
                std::string elem_key = base + "__" + std::to_string(k);
                if (constant_variables.contains(elem_key)) {
                  vals.push_back(constant_variables.at(elem_key));
                } else {
                  throw std::runtime_error(
                      "zip() array elements must be compile-time integer constants.");
                }
              }
              return vals;
            }
          }
          throw std::runtime_error(
              "zip() arguments must be constant list literals or constant arrays.");
        };

        std::vector<int> vals0 = collect_ints(arg0);
        std::vector<int> vals1 = collect_ints(arg1);
        size_t len = std::min(vals0.size(), vals1.size());
        for (size_t k = 0; k < len; ++k) {
          constant_variables[key1] = vals0[k];
          constant_variables[key2] = vals1[k];
          visitStatement(stmt->body.get());
        }
        constant_variables.erase(key1);
        constant_variables.erase(key2);
        return;
      }
    }

    // --- reversed(iterable): for x in reversed([a,b,c]) or reversed(arr) ---
    if (const auto *call = dynamic_cast<const CallExpr *>(iter)) {
      const auto *callee_var = dynamic_cast<const VariableExpr *>(call->callee.get());
      if (callee_var && callee_var->name == "reversed" && call->args.size() == 1) {
        std::string val_key = current_inline_prefix + stmt->var_name;
        const Expression *inner = call->args[0].get();

        // reversed([v0, v1, ...])
        if (const auto *le = dynamic_cast<const ListExpr *>(inner)) {
          for (int k = (int)le->elements.size() - 1; k >= 0; --k) {
            if (const auto *il = dynamic_cast<const IntegerLiteral *>(le->elements[k].get())) {
              constant_variables[val_key] = il->value;
            } else {
              throw std::runtime_error(
                  "reversed() list elements must be compile-time integer constants.");
            }
            visitStatement(stmt->body.get());
          }
          constant_variables.erase(val_key);
          return;
        }

        // reversed(arr) where arr is a constant array variable
        if (const auto *v = dynamic_cast<const VariableExpr *>(inner)) {
          std::string base;
          int arr_size = -1;
          if (!current_inline_prefix.empty()) {
            std::string k = current_inline_prefix + v->name;
            if (array_sizes.contains(k)) { arr_size = array_sizes.at(k); base = k; }
          }
          if (arr_size < 0 && !current_function.empty()) {
            std::string k = current_function + "." + v->name;
            if (array_sizes.contains(k)) { arr_size = array_sizes.at(k); base = k; }
          }
          if (arr_size < 0 && array_sizes.contains(v->name)) {
            arr_size = array_sizes.at(v->name); base = v->name;
          }
          if (arr_size > 0) {
            for (int k = arr_size - 1; k >= 0; --k) {
              std::string elem_key = base + "__" + std::to_string(k);
              if (constant_variables.contains(elem_key)) {
                constant_variables[val_key] = constant_variables.at(elem_key);
              } else {
                throw std::runtime_error(
                    "reversed() array elements must be compile-time integer constants.");
              }
              visitStatement(stmt->body.get());
            }
            constant_variables.erase(val_key);
            return;
          }
        }

        throw std::runtime_error(
            "reversed() argument must be a constant list literal or a constant array.");
      }
    }

    throw std::runtime_error(
        "for-in loop iterable must be a compile-time string constant, "
        "a constant list literal [v0, v1, ...], range(N), enumerate(list/range), "
        "zip(a, b), or reversed(iterable). "
        "Use 'const[str]' type annotation for string parameters.");
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

// T2.2: with ctx [as name]: body
// Desugars to: ctx.__enter__(); body; ctx.__exit__()
// For ZCA types with @inline __enter__/__exit__, this is fully zero-cost.
void IRGenerator::visitWith(const WithStmt *stmt) {
  // Emit __enter__ call on the context expression
  if (auto *varExpr = dynamic_cast<const VariableExpr *>(stmt->context_expr.get())) {
    std::string obj_name = varExpr->name;

    // Bind "as name" to the context object if requested
    if (!stmt->as_name.empty()) {
      std::string qualified = current_function.empty()
                                  ? stmt->as_name
                                  : current_function + "." + stmt->as_name;
      // Alias: as_name points to same object
      variable_aliases[qualified] = obj_name;
    }

    // Build a synthetic CallExpr for obj.__enter__() and emit it
    auto enter_callee = std::make_unique<MemberAccessExpr>(
        std::make_unique<VariableExpr>(obj_name), "__enter__");
    auto enter_call = std::make_unique<CallExpr>(
        std::move(enter_callee), std::vector<std::unique_ptr<Expression>>{});
    visitExpression(enter_call.get());

    // Visit the body
    visitStatement(stmt->body.get());

    // Emit __exit__ call
    auto exit_callee = std::make_unique<MemberAccessExpr>(
        std::make_unique<VariableExpr>(obj_name), "__exit__");
    auto exit_call = std::make_unique<CallExpr>(
        std::move(exit_callee), std::vector<std::unique_ptr<Expression>>{});
    visitExpression(exit_call.get());
  } else {
    // General expression: evaluate once, call __enter__/__exit__ on result
    visitStatement(stmt->body.get());
  }
}

// T2.3: assert condition [, message]
// Compile-time: throws error if condition is statically false.
// Runtime asserts are stripped (no exception mechanism on bare metal).
void IRGenerator::visitAssert(const AssertStmt *stmt) {
  // Try to evaluate statically without emitting any IR.
  // Only throws if the condition is definitively false at compile time.
  try {
    int val = evaluate_constant_expr(stmt->condition.get());
    if (val == 0) {
      throw std::runtime_error(
          "AssertionError" +
          (stmt->message.empty() ? "" : ": " + stmt->message));
    }
    // Statically true: emit nothing.
  } catch (const std::runtime_error &e) {
    // Re-throw AssertionErrors; swallow "cannot evaluate" exceptions.
    std::string what = e.what();
    if (what.find("AssertionError") == 0) throw;
    // Runtime condition — strip the assert on bare-metal targets.
  }
}

void IRGenerator::visitAssign(const AssignStmt *stmt) {
  if (auto indexExpr = dynamic_cast<const IndexExpr *>(stmt->target.get())) {
    // Disambiguation: if the target is a declared array, treat as element store.
    if (auto *ve = dynamic_cast<const VariableExpr *>(indexExpr->target.get())) {
      std::string qualified = current_function.empty()
                                  ? ve->name
                                  : current_function + "." + ve->name;
      // Fallback: also check module-level (bare) name when local lookup fails.
      if (!array_sizes.contains(qualified) && array_sizes.contains(ve->name))
        qualified = ve->name;
      if (array_sizes.contains(qualified)) {
        if (arrays_with_variable_index.contains(qualified) || module_sram_arrays.contains(qualified)) {
          // Variable-index path: emit ArrayStore IR
          tacky::Val idx_val = visitExpression(indexExpr->index.get());
          tacky::Val src_val = visitExpression(stmt->value.get());
          emit(tacky::ArrayStore{qualified, idx_val, src_val,
                                 array_elem_types.at(qualified),
                                 array_sizes.at(qualified)});
        } else {
          // Constant-index only path: write to synthetic scalar variable
          auto *c = dynamic_cast<const IntegerLiteral *>(indexExpr->index.get());
          if (!c) throw std::runtime_error("Array subscript must be a compile-time constant");
          std::string elem_name = qualified + "__" + std::to_string(c->value);
          tacky::Val src_val = visitExpression(stmt->value.get());
          emit(tacky::Copy{src_val, tacky::Variable{elem_name, array_elem_types.at(qualified)}});
        }
        return;
      }
    }

    // Bit-slice path (existing behaviour for register access like PORTB[0] = 1)
    tacky::Val target = visitExpression(indexExpr->target.get());
    tacky::Val indexVal = visitExpression(indexExpr->index.get());

    // Resolve target: inline ptr-returning helpers return a Temporary whose
    // address is stored in constant_address_variables.
    auto resolve_target_addr = [&](tacky::Val &v) {
      auto try_name = [&](const std::string &name) -> bool {
        if (constant_address_variables.contains(name)) {
          v = tacky::MemoryAddress{constant_address_variables.at(name)};
          return true;
        }
        return false;
      };
      if (const auto t = std::get_if<tacky::Temporary>(&v)) return try_name(t->name);
      if (const auto var = std::get_if<tacky::Variable>(&v)) return try_name(var->name);
      return false;
    };
    resolve_target_addr(target);

    // Resolve index: inline uint8-returning helpers return a Temporary whose
    // value is stored in constant_variables.
    int bit = 0;
    if (auto c = std::get_if<tacky::Constant>(&indexVal)) {
      bit = c->value;
    } else {
      auto try_const = [&](const std::string &name) -> bool {
        if (constant_variables.contains(name)) { bit = constant_variables.at(name); return true; }
        return false;
      };
      bool resolved = false;
      if (const auto t = std::get_if<tacky::Temporary>(&indexVal)) resolved = try_const(t->name);
      else if (const auto v = std::get_if<tacky::Variable>(&indexVal)) resolved = try_const(v->name);
      if (!resolved) {
        std::string debug_name;
        if (const auto t = std::get_if<tacky::Temporary>(&indexVal)) debug_name = "Temp:" + t->name;
        else if (const auto v = std::get_if<tacky::Variable>(&indexVal)) debug_name = "Var:" + v->name;
        else debug_name = "unknown_type";
        throw std::runtime_error("Bit index must be constant [index=" + debug_name + " inline_prefix=" + current_inline_prefix + "]");
      }
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
      // Resolve callee to class name, handling both bare names (Pin(...)) and
      // module member access (busio.UART(...) where busio is a loaded module).
      std::string resolvedClass;
      if (auto calleeVar =
              dynamic_cast<const VariableExpr *>(call->callee.get())) {
        resolvedClass = resolve_callee(calleeVar->name);
      } else if (auto calleeMem =
                     dynamic_cast<const MemberAccessExpr *>(call->callee.get())) {
        if (auto objVar =
                dynamic_cast<const VariableExpr *>(calleeMem->object.get())) {
          if (modules.contains(objVar->name)) {
            std::string mangled = objVar->name;
            std::replace(mangled.begin(), mangled.end(), '.', '_');
            resolvedClass = mangled + "_" + calleeMem->member;
          }
        }
      }
      if (!resolvedClass.empty() &&
          inline_functions.contains(resolvedClass + "___init__")) {
        std::string qualified_name;
        if (!current_inline_prefix.empty()) {
          qualified_name = current_inline_prefix + varExpr->name;
        } else if (!current_function.empty()) {
          qualified_name = current_function + "." + varExpr->name;
        } else {
          qualified_name = varExpr->name;
        }
        instance_classes[qualified_name] = resolvedClass;
        pending_constructor_target = qualified_name;
        virtual_instances.insert(qualified_name);
      }
    }
  }
  // Same pre-registration for MemberAccessExpr targets (e.g. self._pin = _Pin(...)).
  // Without this, the inlined constructor has no pending target and self is not bound
  // to the flattened member name, so instance_classes["main.led__pin"] is never set
  // and later calls like led._pin.mode() fall back to dotted CALL labels.
  if (auto memExpr =
          dynamic_cast<const MemberAccessExpr *>(stmt->target.get())) {
    if (auto call = dynamic_cast<const CallExpr *>(stmt->value.get())) {
      if (auto calleeVar =
              dynamic_cast<const VariableExpr *>(call->callee.get())) {
        std::string resolvedClass = resolve_callee(calleeVar->name);
        if (inline_functions.contains(resolvedClass + "___init__")) {
          // Resolve the object to compute the flattened member name.
          // Visiting a plain VariableExpr (e.g. "self") is side-effect-free so
          // it is safe to evaluate it here before the RHS.
          tacky::Val objVal = visitExpression(memExpr->object.get());
          std::string base_name;
          if (auto *v = std::get_if<tacky::Variable>(&objVal))
            base_name = v->name;
          else if (auto *t = std::get_if<tacky::Temporary>(&objVal))
            base_name = t->name;
          if (!base_name.empty()) {
            while (variable_aliases.contains(base_name))
              base_name = variable_aliases.at(base_name);
            std::string flattened_name = base_name + "_" + memExpr->member;
            instance_classes[flattened_name] = resolvedClass;
            pending_constructor_target = flattened_name;
            virtual_instances.insert(flattened_name);
          }
        }
      }
    }
  }

  // Property setter desugaring: obj.attr = val -> inline setter body.
  // Checked before the general value evaluation so we can route the RHS
  // value directly into the setter's parameter binding.
  // Constructor calls (target is MemberAccessExpr + callee is __init__) are
  // excluded — they are already handled by the pre-registration above.
  {
    auto *memTarget =
        dynamic_cast<const MemberAccessExpr *>(stmt->target.get());
    if (memTarget && !property_setters.empty()) {
      bool is_ctor = false;
      if (auto *call = dynamic_cast<const CallExpr *>(stmt->value.get())) {
        std::string rc;
        if (auto *cv = dynamic_cast<const VariableExpr *>(call->callee.get()))
          rc = resolve_callee(cv->name);
        if (!rc.empty() && inline_functions.contains(rc + "___init__"))
          is_ctor = true;
      }
      if (!is_ctor) {
        // Resolve the object to its canonical qualified name.
        tacky::Val objVal = visitExpression(memTarget->object.get());
        std::string base;
        if (auto *v = std::get_if<tacky::Variable>(&objVal)) base = v->name;
        else if (auto *t = std::get_if<tacky::Temporary>(&objVal)) base = t->name;
        while (!base.empty() && variable_aliases.contains(base))
          base = variable_aliases.at(base);
        if (!base.empty() && instance_classes.contains(base)) {
          std::string cls = instance_classes.at(base);
          std::string setter_key = cls + "." + memTarget->member;
          if (property_setters.contains(setter_key)) {
            // Evaluate the RHS in the caller's context.
            tacky::Val arg_val = visitExpression(stmt->value.get());

            const FunctionDef *setter =
                inline_functions.at(property_setters.at(setter_key));
            std::string exit_label = make_label();
            int new_depth = inline_depth + 1;
            // Use a unique prefix that won't clash with regular method names.
            std::string new_prefix = "inline" + std::to_string(new_depth) +
                                     "." + setter->name + "__setter.";

            // Bind self via alias (zero-cost, same as regular method dispatch).
            variable_aliases[new_prefix + "self"] = base;
            instance_classes[new_prefix + "self"] = cls;

            // Bind the value parameter (setter params: [self, value_param]).
            if (setter->params.size() >= 2) {
              std::string param_name = new_prefix + setter->params[1].name;
              if (auto *c = std::get_if<tacky::Constant>(&arg_val))
                constant_variables[param_name] = c->value;
              else if (auto *v = std::get_if<tacky::Variable>(&arg_val))
                variable_aliases[param_name] = v->name;
              else if (auto *t = std::get_if<tacky::Temporary>(&arg_val))
                variable_aliases[param_name] = t->name;
            }

            // Switch to the setter's inline context.
            inline_depth++;
            std::string saved_prefix = current_inline_prefix;
            std::string saved_module_prefix = current_module_prefix;
            current_inline_prefix = new_prefix;
            // Class methods live under the class prefix (e.g. "ClassName_").
            current_module_prefix = cls + "_";

            inline_stack.push_back({exit_label, std::nullopt, {}, ""});
            visitBlock(setter->body.get());
            emit(tacky::Label{exit_label});
            inline_stack.pop_back();

            inline_depth--;
            current_inline_prefix = saved_prefix;
            current_module_prefix = saved_module_prefix;
            return;
          }
        }
      }
    }
  }

  // F9: Lambda assignment — record binding without emitting any runtime copy.
  if (const auto *lam_rhs = dynamic_cast<const LambdaExpr *>(stmt->value.get())) {
    if (const auto *varExpr = dynamic_cast<const VariableExpr *>(stmt->target.get())) {
      pending_lambda_key.clear();
      visitLambdaExpr(lam_rhs);  // registers lambda; sets pending_lambda_key
      std::string qname;
      if (!current_inline_prefix.empty())
        qname = current_inline_prefix + varExpr->name;
      else if (!current_function.empty())
        qname = current_function + "." + varExpr->name;
      else
        qname = varExpr->name;
      if (!pending_lambda_key.empty())
        lambda_variable_names[qname] = pending_lambda_key;
      pending_lambda_key.clear();
      return;
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
          } else {
            // Return-type inference: propagate the type carried by the RHS
            // value (inline functions return typed Temporaries; Variables also
            // carry their declared type).  This lets users write
            //   x = some_fn()  instead of  x: uint16 = some_fn()
            // and still get the correct multi-byte codegen for 16-bit returns.
            if (auto *tmp = std::get_if<tacky::Temporary>(&value))
              type = tmp->type;
            else if (auto *v = std::get_if<tacky::Variable>(&value))
              type = v->type;
            // Record so that subsequent reads of this variable use the same type.
            variable_types[qualified_name] = type;
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

        // Zero-Cost Abstraction for virtual instance members:
        //
        // String-typed members (e.g. self.name = "PD5") arrive here as Constant(N)
        // where N is a string-literal ID (>= 256, above uint8 range).  They must be
        // tracked in constant_variables so that string-dispatch comparisons
        // (if name == "PB5":) can be constant-folded at use sites.  No SRAM Copy is
        // needed — the value never changes at runtime.
        //
        // Numeric mutable members (self.failed = False, self.humidity = 0) must NOT
        // be constant-folded because they are reassigned in multiple branches at
        // runtime.  They always get a SRAM slot via the Copy that follows.
        if (auto c = std::get_if<tacky::Constant>(&value)) {
          if (!virtual_instances.contains(base_name)) {
            // Non-virtual base: always fold as compile-time constant.
            constant_variables[flattened_name] = c->value;
            // Fall through to emit Copy.
          } else if (string_id_to_str.contains(c->value)) {
            // Virtual instance string member: fold for dispatch, no SRAM needed.
            constant_variables[flattened_name] = c->value;
            str_constant_variables[flattened_name] = string_id_to_str.at(c->value);
            return;
          }
          // Virtual instance numeric (mutable) member: no constant fold, fall through
          // to emit Copy so the value is stored in SRAM.
        }

        // Propagate compile-time ptr/uint values from inline function returns.
        // Inline calls return Temporaries (never plain Constants), so the check above
        // never fires for them. Instead we check the two constant-folding side maps:
        //   constant_address_variables: holds MemoryAddress values (e.g. PORTB = 0x25)
        //   constant_variables:         holds uint8 values (e.g. bit number = 5)
        // These are always compile-time-derived (unlike plain Constant(0) which may be
        // a mutable initial value).  Safe to fold without SRAM.
        // This applies to both virtual and non-virtual instances (e.g. Pin._bit = select_bit(name)).
        {
          auto try_temp_name = [&](const std::string &tname) -> bool {
            if (constant_address_variables.contains(tname)) {
              constant_address_variables[flattened_name] = constant_address_variables.at(tname);
              return true;
            }
            if (constant_variables.contains(tname)) {
              constant_variables[flattened_name] = constant_variables.at(tname);
              return true;
            }
            return false;
          };
          bool folded = false;
          if (const auto *t = std::get_if<tacky::Temporary>(&value)) folded = try_temp_name(t->name);
          else if (const auto *v = std::get_if<tacky::Variable>(&value)) folded = try_temp_name(v->name);
          if (folded) return;  // compile-time constant member — no SRAM needed
        }

        // Propagate instance class when assigning a ZCA instance to a member
        // (e.g. self.pin = pin). Without this, self.pin.low() can't find Pin's
        // inline methods and falls back to a dotted RCALL with an invalid label.
        // Follow the full alias chain because the param variable
        // (e.g. inline1.DHT11___init__.pin) may itself alias the real instance
        // (main.data_pin) which is where instance_classes is actually set.
        if (const auto *v_val = std::get_if<tacky::Variable>(&value)) {
          // Walk alias chain to find the instance_classes entry.
          // Only set a variable alias (and skip Copy) when the assigned value
          // is itself a ZCA instance (e.g. self.pin = pin).  Plain scalar
          // variables like hum_int must fall through to emit a Copy so that
          // the member register (e.g. sensor.humidity) is actually updated.
          std::string cls_key = v_val->name;
          bool is_zca_instance = false;
          for (int depth = 0; depth < 20; ++depth) {
            if (instance_classes.contains(cls_key)) {
              is_zca_instance = true;
              instance_classes[flattened_name] = instance_classes.at(cls_key);
              virtual_instances.insert(flattened_name);
              break;
            }
            auto it = variable_aliases.find(cls_key);
            if (it != variable_aliases.end())
              cls_key = it->second;
            else
              break;
          }
          if (is_zca_instance) {
            variable_aliases[flattened_name] = v_val->name;
            return;  // Alias established — no Copy instruction needed
          }
          // Plain scalar variable: fall through to emit Copy below.
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
  // Handle bytearray: buf: bytearray = bytearray(N)
  // bytearray has no '[' in the type so Parser produces a VarDecl, not AnnAssign.
  // Delegate to the shared helper embedded in the lambda below.
  if (stmt->var_type == "bytearray") {
    int count = 0;
    std::vector<int> init_vals;

    if (stmt->init) {
      if (const auto *call = dynamic_cast<const CallExpr *>(stmt->init.get())) {
        const auto *callee = dynamic_cast<const VariableExpr *>(call->callee.get());
        if (callee && callee->name == "bytearray" && !call->args.empty()) {
          const Expression *arg0 = call->args[0].get();
          if (const auto *il = dynamic_cast<const IntegerLiteral *>(arg0)) {
            count = il->value;
            init_vals.assign(count, 0);
          } else if (const auto *le = dynamic_cast<const ListExpr *>(arg0)) {
            count = (int)le->elements.size();
            for (const auto &e : le->elements) {
              if (const auto *il2 = dynamic_cast<const IntegerLiteral *>(e.get()))
                init_vals.push_back(il2->value);
              else
                init_vals.push_back(0);
            }
          }
        }
      }
    }

    if (count <= 0) {
      throw std::runtime_error("bytearray: could not determine buffer size from initializer.");
    }

    std::string qualified;
    if (!current_inline_prefix.empty())
      qualified = current_inline_prefix + stmt->name;
    else if (!current_function.empty())
      qualified = current_function + "." + stmt->name;
    else
      qualified = stmt->name;

    array_sizes[qualified]      = count;
    array_elem_types[qualified] = DataType::UINT8;
    variable_types[qualified]   = DataType::UINT8;
    // Force SRAM path so indexed writes/reads use ArrayStore/ArrayLoad.
    arrays_with_variable_index.insert(qualified);
    // Also register in the persistent module_sram_arrays set so that non-inline
    // functions can access module-level bytearrays with variable indices even after
    // arrays_with_variable_index is cleared between function compilations.
    if (current_function.empty() && current_inline_prefix.empty())
      module_sram_arrays.insert(qualified);

    for (int k = 0; k < count; ++k) {
      emit(tacky::ArrayStore{qualified, tacky::Constant{k},
                             tacky::Constant{init_vals[k]}, DataType::UINT8, count});
    }
    return;
  }

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
  // Handle bytearray annotation: bytearray is syntactic sugar for a SRAM uint8[N].
  // Supported RHS forms:
  //   bytearray(N)            -> N-element zero-initialized buffer
  //   bytearray(b"...")       -> init from bytes literal (ListExpr after lexer decoding)
  //   bytearray([v0, v1, ...])-> init from list literal
  if (stmt->annotation == "bytearray") {
    int count = 0;
    std::vector<int> init_vals;

    if (stmt->value) {
      if (const auto *call = dynamic_cast<const CallExpr *>(stmt->value.get())) {
        const auto *callee = dynamic_cast<const VariableExpr *>(call->callee.get());
        if (callee && callee->name == "bytearray" && !call->args.empty()) {
          const Expression *arg0 = call->args[0].get();
          // bytearray(N): integer size, zero-init
          if (const auto *il = dynamic_cast<const IntegerLiteral *>(arg0)) {
            count = il->value;
            init_vals.assign(count, 0);
          }
          // bytearray(b"...") or bytearray([...]): list of bytes
          else if (const auto *le = dynamic_cast<const ListExpr *>(arg0)) {
            count = (int)le->elements.size();
            for (const auto &e : le->elements) {
              if (const auto *il2 = dynamic_cast<const IntegerLiteral *>(e.get()))
                init_vals.push_back(il2->value);
              else
                init_vals.push_back(0);
            }
          }
        }
      }
    }

    if (count <= 0) {
      throw std::runtime_error("bytearray: could not determine buffer size from initializer.");
    }

    std::string qualified = current_function.empty()
                                ? stmt->target
                                : current_function + "." + stmt->target;
    array_sizes[qualified]      = count;
    array_elem_types[qualified] = DataType::UINT8;
    variable_types[qualified]   = DataType::UINT8;

    // bytearray is always variable-index accessible (mutable buffer), so use SRAM.
    // Force SRAM path by registering it in arrays_with_variable_index.
    arrays_with_variable_index.insert(qualified);

    for (int k = 0; k < count; ++k) {
      emit(tacky::ArrayStore{qualified, tacky::Constant{k},
                             tacky::Constant{init_vals[k]}, DataType::UINT8, count});
    }
    return;
  }

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
          // List comprehension: fully unroll and return early
          if (auto *lc = dynamic_cast<const ListCompExpr *>(stmt->value.get())) {
            visitListComp(lc, qualified, count, elem_dt);
            return;
          }
          if (auto *le = dynamic_cast<const ListExpr *>(stmt->value.get())) {
            for (int k = 0; k < std::min(count, (int)le->elements.size()); ++k) {
              if (auto *il = dynamic_cast<const IntegerLiteral *>(le->elements[k].get()))
                init_vals[k] = il->value;
            }
          }
          // list * N or b"..." * N  — compile-time repeat: [v] * N or [v0,v1,...] * N
          if (auto *be = dynamic_cast<const BinaryExpr *>(stmt->value.get())) {
            if (be->op == BinaryOp::Mul) {
              const auto *le_rep = dynamic_cast<const ListExpr *>(be->left.get());
              const auto *repeat_lit = dynamic_cast<const IntegerLiteral *>(be->right.get());
              if (le_rep && repeat_lit && repeat_lit->value > 0) {
                // Fill init_vals by repeating the list elements
                for (int k = 0; k < count; ++k) {
                  size_t src_idx = k % le_rep->elements.size();
                  if (src_idx < le_rep->elements.size()) {
                    if (auto *il = dynamic_cast<const IntegerLiteral *>(
                            le_rep->elements[src_idx].get()))
                      init_vals[k] = il->value;
                  }
                }
              }
            }
          }
        }

        if (arrays_with_variable_index.contains(qualified) || module_sram_arrays.contains(qualified)) {
          // Variable-index path: contiguous SRAM block — emit ArrayStore per element.
          for (int k = 0; k < count; ++k) {
            emit(tacky::ArrayStore{qualified, tacky::Constant{k},
                                   tacky::Constant{init_vals[k]}, elem_dt, count});
          }
        } else {
          // Constant-index only path: synthetic scalars — each element gets its own
          // register-allocable variable (zero overhead on AVR).
          for (int k = 0; k < count; ++k) {
            std::string elem_name = qualified + "__" + std::to_string(k);
            tacky::Variable elem_var{elem_name, elem_dt};
            variable_types[elem_name] = elem_dt;
            emit(tacky::Copy{tacky::Constant{init_vals[k]}, elem_var});
          }
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

void IRGenerator::visitListComp(const ListCompExpr *lc,
                                const std::string &qualified_name,
                                int count, DataType elem_dt) {
  // Build the full sequence of (outer_val, [inner_val]) pairs.
  // Supported iterables:
  //   range(N)           -> values 0, 1, ..., N-1
  //   range(start, stop) -> values start, start+1, ..., stop-1
  //   [v0, v1, ...]      -> values v0, v1, ... (must be constant integers)

  // Helper: evaluate a compile-time-constant expression to int.
  // Supports: integer literals, loop variables bound in constant_variables,
  // and binary expressions (arithmetic + comparisons) over constants.
  std::function<std::optional<int>(const Expression *)> eval_const;
  eval_const = [&](const Expression *e) -> std::optional<int> {
    if (const auto *il = dynamic_cast<const IntegerLiteral *>(e))
      return il->value;
    if (const auto *bl = dynamic_cast<const BooleanLiteral *>(e))
      return bl->value ? 1 : 0;
    if (const auto *v = dynamic_cast<const VariableExpr *>(e)) {
      std::string k = current_inline_prefix + v->name;
      if (constant_variables.contains(k)) return constant_variables.at(k);
    }
    if (const auto *be = dynamic_cast<const BinaryExpr *>(e)) {
      auto lv = eval_const(be->left.get());
      auto rv = eval_const(be->right.get());
      if (!lv || !rv) return std::nullopt;
      switch (be->op) {
        case BinaryOp::Add:      return *lv + *rv;
        case BinaryOp::Sub:      return *lv - *rv;
        case BinaryOp::Mul:      return *lv * *rv;
        case BinaryOp::Div:      return (*rv != 0) ? *lv / *rv : std::optional<int>{};
        case BinaryOp::FloorDiv: return (*rv != 0) ? *lv / *rv : std::optional<int>{};
        case BinaryOp::Mod:      return (*rv != 0) ? *lv % *rv : std::optional<int>{};
        case BinaryOp::Equal:    return (*lv == *rv) ? 1 : 0;
        case BinaryOp::NotEqual: return (*lv != *rv) ? 1 : 0;
        case BinaryOp::Less:     return (*lv < *rv)  ? 1 : 0;
        case BinaryOp::Greater:  return (*lv > *rv)  ? 1 : 0;
        case BinaryOp::LessEq:   return (*lv <= *rv) ? 1 : 0;
        case BinaryOp::GreaterEq:return (*lv >= *rv) ? 1 : 0;
        case BinaryOp::And:      return (*lv && *rv) ? 1 : 0;
        case BinaryOp::Or:       return (*lv || *rv) ? 1 : 0;
        case BinaryOp::BitAnd:   return *lv & *rv;
        case BinaryOp::BitOr:    return *lv | *rv;
        case BinaryOp::BitXor:   return *lv ^ *rv;
        case BinaryOp::LShift:   return *lv << *rv;
        case BinaryOp::RShift:   return *lv >> *rv;
        default: return std::nullopt;
      }
    }
    if (const auto *ue = dynamic_cast<const UnaryExpr *>(e)) {
      auto val = eval_const(ue->operand.get());
      if (!val) return std::nullopt;
      switch (ue->op) {
        case UnaryOp::Negate: return -*val;
        case UnaryOp::Not:    return !*val ? 1 : 0;
        case UnaryOp::BitNot: return ~*val;
        default: return std::nullopt;
      }
    }
    return std::nullopt;
  };

  // Helper: collect values from a range() call or ListExpr iterable.
  auto collect_iterable = [&](const Expression *iter_expr) -> std::vector<int> {
    std::vector<int> vals;
    if (const auto *call = dynamic_cast<const CallExpr *>(iter_expr)) {
      const auto *callee_var = dynamic_cast<const VariableExpr *>(call->callee.get());
      if (!callee_var || callee_var->name != "range") {
        throw std::runtime_error(
            "List comprehension iterable must be range() or a constant list.");
      }
      int start = 0, stop = 0;
      if (call->args.size() == 1) {
        auto sv = eval_const(call->args[0].get());
        if (!sv) throw std::runtime_error(
            "List comprehension: range() argument must be a compile-time constant.");
        stop = *sv;
      } else if (call->args.size() >= 2) {
        auto sv = eval_const(call->args[0].get());
        auto ev = eval_const(call->args[1].get());
        if (!sv || !ev) throw std::runtime_error(
            "List comprehension: range() arguments must be compile-time constants.");
        start = *sv;
        stop = *ev;
      } else {
        throw std::runtime_error("List comprehension: range() requires at least one argument.");
      }
      for (int i = start; i < stop; ++i)
        vals.push_back(i);
    } else if (const auto *le = dynamic_cast<const ListExpr *>(iter_expr)) {
      for (const auto &e : le->elements) {
        auto v = eval_const(e.get());
        if (!v) throw std::runtime_error(
            "List comprehension: list iterable elements must be compile-time constants.");
        vals.push_back(*v);
      }
    } else {
      throw std::runtime_error(
          "List comprehension iterable must be range() or a constant list literal.");
    }
    return vals;
  };

  std::vector<int> outer_vals = collect_iterable(lc->iterable.get());

  // Build the full flat list of element values by unrolling all loop combinations.
  // For nested: for each outer val, bind outer var, then for each inner val bind inner var.
  // For filter:  after binding var(s), evaluate filter; skip element if filter == 0.
  std::string outer_key = current_inline_prefix + lc->var_name;
  std::string inner_key = lc->var2_name.empty() ? "" : (current_inline_prefix + lc->var2_name);
  bool has_inner = !lc->var2_name.empty() && lc->iterable2 != nullptr;

  // Collect all (outer, inner) pairs and compute element values.
  struct ElemEntry { tacky::Val val; };
  std::vector<ElemEntry> entries;

  for (int oval : outer_vals) {
    constant_variables[outer_key] = oval;

    if (has_inner) {
      // Nested: collect inner values (inner iterable may reference outer var via eval_const).
      std::vector<int> inner_vals = collect_iterable(lc->iterable2.get());
      for (int ival : inner_vals) {
        constant_variables[inner_key] = ival;

        // Apply filter if present.
        if (lc->filter) {
          auto fv = eval_const(lc->filter.get());
          if (!fv) throw std::runtime_error(
              "List comprehension: filter condition must be a compile-time constant.");
          if (*fv == 0) continue;
        }

        tacky::Val elem_val = visitExpression(lc->element.get());
        entries.push_back({elem_val});
      }
      constant_variables.erase(inner_key);
    } else {
      // Single loop, optional filter.
      if (lc->filter) {
        auto fv = eval_const(lc->filter.get());
        if (!fv) throw std::runtime_error(
            "List comprehension: filter condition must be a compile-time constant.");
        if (*fv == 0) {
          continue;
        }
      }

      tacky::Val elem_val = visitExpression(lc->element.get());
      entries.push_back({elem_val});
    }
  }
  constant_variables.erase(outer_key);

  // Validate total count against declared array size.
  if ((int)entries.size() != count) {
    throw std::runtime_error(
        "List comprehension: generated " + std::to_string(entries.size()) +
        " elements but array size is " + std::to_string(count) + ".");
  }

  bool use_sram = arrays_with_variable_index.contains(qualified_name) || module_sram_arrays.contains(qualified_name);

  for (int k = 0; k < count; ++k) {
    if (use_sram) {
      emit(tacky::ArrayStore{qualified_name, tacky::Constant{k}, entries[k].val,
                             elem_dt, count});
    } else {
      std::string elem_name = qualified_name + "__" + std::to_string(k);
      tacky::Variable elem_var{elem_name, elem_dt};
      variable_types[elem_name] = elem_dt;
      emit(tacky::Copy{entries[k].val, elem_var});
    }
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
    // If the variable was const-folded (e.g. module-level `e = CONST`),
    // AugAssign mutates it.  Force a Variable target and evict from
    // constant_variables so future reads return the new runtime value.
    if (std::holds_alternative<tacky::Constant>(target)) {
      std::string qualified;
      if (!current_inline_prefix.empty())
        qualified = current_inline_prefix + varExpr->name;
      else if (!current_function.empty())
        qualified = current_function + "." + varExpr->name;
      else
        qualified = varExpr->name;
      DataType dt = variable_types.contains(qualified)
                        ? variable_types.at(qualified)
                        : DataType::UINT8;
      target = tacky::Variable{qualified, dt};
      constant_variables.erase(qualified);
    }
    emit(tacky::AugAssign{map_augop(stmt->op), target, operand});
  } else if (auto indexExpr = dynamic_cast<const IndexExpr *>(stmt->target.get())) {
    // T1.3: Desugar arr[i] op= x  →  arr[i] = arr[i] op x
    // Read current value via visitIndex, apply op, write back via the same
    // paths used in visitAssign.
    tacky::BinaryOp bop = map_augop(stmt->op);
    tacky::Val current = visitIndex(indexExpr);
    tacky::Temporary result = make_temp(DataType::UINT8);
    emit(tacky::Binary{bop, current, operand, result});

    // Write back: replicate the write path of visitAssign(IndexExpr).
    if (auto *ve = dynamic_cast<const VariableExpr *>(indexExpr->target.get())) {
      std::string qualified = current_function.empty()
                                  ? ve->name
                                  : current_function + "." + ve->name;
      // Fallback: also check module-level (bare) name when local lookup fails.
      if (!array_sizes.contains(qualified) && array_sizes.contains(ve->name))
        qualified = ve->name;
      if (array_sizes.contains(qualified)) {
        if (arrays_with_variable_index.contains(qualified) || module_sram_arrays.contains(qualified)) {
          tacky::Val idx_val = visitExpression(indexExpr->index.get());
          emit(tacky::ArrayStore{qualified, idx_val, result,
                                 array_elem_types.at(qualified),
                                 array_sizes.at(qualified)});
        } else {
          auto *c = dynamic_cast<const IntegerLiteral *>(indexExpr->index.get());
          if (!c) throw std::runtime_error("Array subscript must be a compile-time constant");
          std::string elem_name = qualified + "__" + std::to_string(c->value);
          emit(tacky::Copy{result, tacky::Variable{elem_name, array_elem_types.at(qualified)}});
        }
        return;
      }
    }
    // Bit-slice write path (e.g. port[n] ^= 1)
    tacky::Val tgt_val = visitExpression(indexExpr->target.get());
    tacky::Val idx_val = visitExpression(indexExpr->index.get());
    auto resolve_addr = [&](tacky::Val &v) {
      auto try_name = [&](const std::string &name) -> bool {
        if (constant_address_variables.contains(name)) {
          v = tacky::MemoryAddress{constant_address_variables.at(name)};
          return true;
        }
        return false;
      };
      if (const auto t = std::get_if<tacky::Temporary>(&v)) return try_name(t->name);
      if (const auto vv = std::get_if<tacky::Variable>(&v)) return try_name(vv->name);
      return false;
    };
    resolve_addr(tgt_val);
    int bit = 0;
    if (auto c = std::get_if<tacky::Constant>(&idx_val)) {
      bit = c->value;
    } else {
      auto try_const = [&](const std::string &name) -> bool {
        if (constant_variables.contains(name)) { bit = constant_variables.at(name); return true; }
        return false;
      };
      bool resolved = false;
      if (const auto t = std::get_if<tacky::Temporary>(&idx_val)) resolved = try_const(t->name);
      else if (const auto v = std::get_if<tacky::Variable>(&idx_val)) resolved = try_const(v->name);
      if (!resolved) throw std::runtime_error("Bit index must be constant for augmented assignment");
    }
    emit(tacky::BitWrite{tgt_val, bit, result});
  } else {
    throw std::runtime_error("Augmented assignment target must be a variable or subscript");
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

void IRGenerator::visitTupleUnpack(const TupleUnpackStmt *stmt) {
  // Helper: resolve the qualified name for a target variable, mirroring the
  // logic used by visitVarDecl / resolve_binding so keys always match.
  auto qualify_target = [&](const std::string &name) -> std::string {
    if (!current_inline_prefix.empty()) return current_inline_prefix + name;
    if (!current_function.empty()) return current_function + "." + name;
    return name;
  };

  // Case 1: RHS is a tuple literal  (a, b) = (expr0, expr1)
  if (const auto *tup = dynamic_cast<const TupleExpr *>(stmt->value.get())) {
    if (tup->elements.size() != stmt->targets.size()) {
      throw std::runtime_error(
          "Tuple size mismatch: " + std::to_string(tup->elements.size()) +
          " values, " + std::to_string(stmt->targets.size()) + " targets");
    }
    for (size_t _k = 0; _k < stmt->targets.size(); ++_k) {
      tacky::Val v = visitExpression(tup->elements[_k].get());
      std::string qualified = qualify_target(stmt->targets[_k]);
      DataType dt = variable_types.contains(qualified)
                        ? variable_types.at(qualified)
                        : DataType::UINT8;
      emit(tacky::Copy{v, tacky::Variable{qualified, dt}});
      if (const auto *c = std::get_if<tacky::Constant>(&v))
        constant_variables[qualified] = c->value;
    }
    return;
  }

  // Case 2: RHS is an inline function call returning a tuple.
  // Set pending count so visitCall allocates result_vars in InlineContext.
  last_tuple_results_.clear();
  pending_tuple_count_ = static_cast<int>(stmt->targets.size());
  visitExpression(stmt->value.get());
  pending_tuple_count_ = 0;

  if (last_tuple_results_.size() != stmt->targets.size()) {
    throw std::runtime_error(
        "Tuple unpack: expected " + std::to_string(stmt->targets.size()) +
        " return values but got " + std::to_string(last_tuple_results_.size()) +
        ". Ensure the called function is @inline and returns a tuple literal.");
  }

  for (size_t _k = 0; _k < stmt->targets.size(); ++_k) {
    std::string qualified = qualify_target(stmt->targets[_k]);
    DataType dt = variable_types.contains(qualified)
                      ? variable_types.at(qualified)
                      : DataType::UINT8;
    emit(tacky::Copy{tacky::Variable{last_tuple_results_[_k], DataType::UINT8},
                      tacky::Variable{qualified, dt}});
  }
}

tacky::Val IRGenerator::visitExpression(const Expression *expr) {
  if (auto *bin = dynamic_cast<const BinaryExpr *>(expr))
    return visitBinary(bin);
  if (auto *tern = dynamic_cast<const TernaryExpr *>(expr))
    return visitTernary(tern);
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
      string_literal_ids[str->value] = next_string_id;
      string_id_to_str[next_string_id] = str->value;
      next_string_id++;
    }
    return tacky::Constant{string_literal_ids[str->value]};
  }

  // f-string: compile-time constant interpolation only.
  if (const auto *fstr = dynamic_cast<const FStringExpr *>(expr)) {
    return visitFStringExpr(fstr);
  }

  // Walrus operator: name := expr — assigns to name and returns the value.
  if (const auto *walrus = dynamic_cast<const WalrusExpr *>(expr)) {
    tacky::Val rhs = visitExpression(walrus->value.get());
    std::string key = current_inline_prefix.empty()
        ? walrus->var_name
        : current_inline_prefix + walrus->var_name;
    // Look up the existing variable's type
    DataType dt = DataType::UINT8;
    if (variable_types.contains(key)) dt = variable_types.at(key);
    tacky::Variable var{key, dt};
    emit(tacky::Copy{rhs, var});
    return var;
  }

  // Lambda expression (F9)
  if (const auto *lam = dynamic_cast<const LambdaExpr *>(expr))
    return visitLambdaExpr(lam);

  throw std::runtime_error("IR Generation: Unknown Expression type");
}

// F9: Lambda expression — synthesize an anonymous @inline closure and register it.
// The returned Constant{0} is a dummy (lambdas are never read as numeric values).
// The caller (visitAssign or a call site) uses pending_lambda_key to get the key.
tacky::Val IRGenerator::visitLambdaExpr(const LambdaExpr *expr) {
  std::string key = "__lambda_" + std::to_string(lambda_counter++);
  // Register so that lambda_variable_names can look it up by key.
  lambda_functions_map[key] = expr;
  pending_lambda_key = key;
  return tacky::Constant{0};
}

tacky::Val IRGenerator::visitFStringExpr(const FStringExpr *expr) {
  // Concatenate all parts. Every {expr} part must resolve to a compile-time
  // string or integer constant; otherwise a CompileError is thrown.
  std::string result;
  for (const auto &part : expr->parts) {
    if (!part.is_expr) {
      result += part.text;
    } else {
      // Evaluate the inner expression to get a tacky::Val.
      tacky::Val val = visitExpression(part.expr.get());
      if (const auto *c = std::get_if<tacky::Constant>(&val)) {
        // Check if it's a registered string ID.
        if (string_id_to_str.contains(c->value)) {
          result += string_id_to_str.at(c->value);
        } else {
          // Integer constant — convert to decimal string.
          result += std::to_string(c->value);
        }
      } else {
        throw std::runtime_error(
            "f-string interpolation requires a compile-time constant expression");
      }
    }
  }
  // Register the concatenated string and return its ID as a Constant.
  if (string_literal_ids.find(result) == string_literal_ids.end()) {
    string_literal_ids[result] = next_string_id;
    string_id_to_str[next_string_id] = result;
    next_string_id++;
  }
  return tacky::Constant{string_literal_ids[result]};
}

tacky::Val IRGenerator::visitLiteral(const IntegerLiteral *expr) {
  return tacky::Constant{expr->value};
}

tacky::Val IRGenerator::visitVariable(const VariableExpr *expr) {
  return resolve_binding(expr->name);
}

tacky::Val IRGenerator::visitBinary(const BinaryExpr *expr) {
  // in / not in — membership test against a list literal (compile-time or runtime OR-chain).
  // is / is not — identity check, maps to == / != for integer types on MCU.
  if (expr->op == BinaryOp::In || expr->op == BinaryOp::NotIn) {
    bool negate = (expr->op == BinaryOp::NotIn);
    tacky::Val lhs = visitExpression(expr->left.get());

    // RHS must be a list literal
    const auto *rlist = dynamic_cast<const ListExpr *>(expr->right.get());
    if (!rlist) {
      throw std::runtime_error(
          "'in' / 'not in' requires a list literal on the right-hand side");
    }
    if (rlist->elements.empty()) {
      // x in [] == False; x not in [] == True
      return tacky::Constant{negate ? 1 : 0};
    }

    // Evaluate all element values
    std::vector<tacky::Val> elems;
    elems.reserve(rlist->elements.size());
    bool all_const = true;
    if (auto lc = std::get_if<tacky::Constant>(&lhs)) {
      // LHS is constant — check each element
      for (const auto &e : rlist->elements) {
        tacky::Val ev = visitExpression(e.get());
        if (auto ec = std::get_if<tacky::Constant>(&ev)) {
          if (lc->value == ec->value) {
            return tacky::Constant{negate ? 0 : 1};
          }
        } else {
          all_const = false;
        }
        elems.push_back(ev);
      }
      if (all_const) {
        // LHS constant, all elements constant, no match found
        return tacky::Constant{negate ? 1 : 0};
      }
    } else {
      for (const auto &e : rlist->elements) {
        elems.push_back(visitExpression(e.get()));
      }
    }

    // Runtime OR-chain: result = (lhs==e0) || (lhs==e1) || ...
    // For not-in: result = (lhs!=e0) && (lhs!=e1) && ...
    tacky::Temporary result = make_temp(DataType::UINT8);
    if (negate) {
      // Build AND-chain of (lhs != elem)
      tacky::Temporary cmp = make_temp(DataType::UINT8);
      emit(tacky::Binary{tacky::BinaryOp::NotEqual, lhs, elems[0], cmp});
      emit(tacky::Copy{cmp, result});
      for (size_t i = 1; i < elems.size(); ++i) {
        tacky::Temporary ci = make_temp(DataType::UINT8);
        emit(tacky::Binary{tacky::BinaryOp::NotEqual, lhs, elems[i], ci});
        // result = result AND ci (short-circuit not needed for compile-time known sizes)
        std::string end_lbl = make_label();
        emit(tacky::JumpIfZero{result, end_lbl});
        emit(tacky::Copy{ci, result});
        emit(tacky::Label{end_lbl});
      }
    } else {
      // Build OR-chain of (lhs == elem)
      tacky::Temporary cmp = make_temp(DataType::UINT8);
      emit(tacky::Binary{tacky::BinaryOp::Equal, lhs, elems[0], cmp});
      emit(tacky::Copy{cmp, result});
      for (size_t i = 1; i < elems.size(); ++i) {
        tacky::Temporary ci = make_temp(DataType::UINT8);
        emit(tacky::Binary{tacky::BinaryOp::Equal, lhs, elems[i], ci});
        std::string end_lbl = make_label();
        emit(tacky::JumpIfNotZero{result, end_lbl});
        emit(tacky::Copy{ci, result});
        emit(tacky::Label{end_lbl});
      }
    }
    return result;
  }

  // is / is not — identity maps to == / != on integer MCU types
  if (expr->op == BinaryOp::Is || expr->op == BinaryOp::IsNot) {
    tacky::Val lhs = visitExpression(expr->left.get());
    tacky::Val rhs = visitExpression(expr->right.get());
    tacky::BinaryOp bop = (expr->op == BinaryOp::Is)
                              ? tacky::BinaryOp::Equal
                              : tacky::BinaryOp::NotEqual;
    if (auto c1 = std::get_if<tacky::Constant>(&lhs))
      if (auto c2 = std::get_if<tacky::Constant>(&rhs))
        return tacky::Constant{
            (bop == tacky::BinaryOp::Equal) ? (c1->value == c2->value ? 1 : 0)
                                            : (c1->value != c2->value ? 1 : 0)};
    tacky::Temporary dst = make_temp(DataType::UINT8);
    emit(tacky::Binary{bop, lhs, rhs, dst});
    return dst;
  }

  // T1.2: Short-circuit and/or — evaluate RHS only when needed.
  if (expr->op == BinaryOp::And) {
    tacky::Val v1 = visitExpression(expr->left.get());
    if (auto c1 = std::get_if<tacky::Constant>(&v1)) {
      if (c1->value == 0) return tacky::Constant{0};
      tacky::Val v2 = visitExpression(expr->right.get());
      if (auto c2 = std::get_if<tacky::Constant>(&v2))
        return tacky::Constant{(c2->value != 0) ? 1 : 0};
      tacky::Temporary r = make_temp(DataType::UINT8);
      emit(tacky::Binary{tacky::BinaryOp::NotEqual, v2, tacky::Constant{0}, r});
      return r;
    }
    std::string false_label = make_label();
    std::string end_label   = make_label();
    tacky::Temporary result = make_temp(DataType::UINT8);
    emit(tacky::JumpIfZero{v1, false_label});
    tacky::Val v2 = visitExpression(expr->right.get());
    emit(tacky::Binary{tacky::BinaryOp::NotEqual, v2, tacky::Constant{0}, result});
    emit(tacky::Jump{end_label});
    emit(tacky::Label{false_label});
    emit(tacky::Copy{tacky::Constant{0}, result});
    emit(tacky::Label{end_label});
    return result;
  }
  if (expr->op == BinaryOp::Or) {
    tacky::Val v1 = visitExpression(expr->left.get());
    if (auto c1 = std::get_if<tacky::Constant>(&v1)) {
      if (c1->value != 0) return tacky::Constant{1};
      tacky::Val v2 = visitExpression(expr->right.get());
      if (auto c2 = std::get_if<tacky::Constant>(&v2))
        return tacky::Constant{(c2->value != 0) ? 1 : 0};
      tacky::Temporary r = make_temp(DataType::UINT8);
      emit(tacky::Binary{tacky::BinaryOp::NotEqual, v2, tacky::Constant{0}, r});
      return r;
    }
    std::string true_label = make_label();
    std::string end_label  = make_label();
    tacky::Temporary result = make_temp(DataType::UINT8);
    emit(tacky::JumpIfNotZero{v1, true_label});
    tacky::Val v2 = visitExpression(expr->right.get());
    emit(tacky::Binary{tacky::BinaryOp::NotEqual, v2, tacky::Constant{0}, result});
    emit(tacky::Jump{end_label});
    emit(tacky::Label{true_label});
    emit(tacky::Copy{tacky::Constant{1}, result});
    emit(tacky::Label{end_label});
    return result;
  }

  // ** (power) — compile-time constant fold only
  if (expr->op == BinaryOp::Pow) {
    tacky::Val bv = visitExpression(expr->left.get());
    tacky::Val ev = visitExpression(expr->right.get());
    auto cb = std::get_if<tacky::Constant>(&bv);
    auto ce = std::get_if<tacky::Constant>(&ev);
    if (!cb || !ce)
      throw std::runtime_error("** operator requires compile-time constant operands");
    int base = cb->value;
    int exp  = ce->value;
    if (exp < 0) throw std::runtime_error("** operator: negative exponent not supported");
    int result = 1;
    for (int k = 0; k < exp; ++k) result *= base;
    return tacky::Constant{result};
  }

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
    case BinaryOp::Pow:
      // Should have been handled above; fall through to error
      throw std::runtime_error("** requires compile-time constant operands");
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

// T1.1: Ternary / conditional expression: true_val if condition else false_val
tacky::Val IRGenerator::visitTernary(const TernaryExpr *expr) {
  tacky::Val cond = visitExpression(expr->condition.get());

  // Constant folding: if condition is statically known, pick one branch.
  if (auto c = std::get_if<tacky::Constant>(&cond)) {
    if (c->value != 0) return visitExpression(expr->true_val.get());
    return visitExpression(expr->false_val.get());
  }

  std::string false_label = make_label();
  std::string end_label   = make_label();

  emit(tacky::JumpIfZero{cond, false_label});
  tacky::Val true_val = visitExpression(expr->true_val.get());
  tacky::Temporary result = make_temp(DataType::UINT8);
  emit(tacky::Copy{true_val, result});
  emit(tacky::Jump{end_label});
  emit(tacky::Label{false_label});
  tacky::Val false_val = visitExpression(expr->false_val.get());
  emit(tacky::Copy{false_val, result});
  emit(tacky::Label{end_label});
  return result;
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
    // Fallback: also check module-level (bare) name when local lookup fails.
    if (!array_sizes.contains(qualified) && array_sizes.contains(ve->name))
      qualified = ve->name;
    if (array_sizes.contains(qualified)) {
      if (arrays_with_variable_index.contains(qualified) || module_sram_arrays.contains(qualified)) {
        // Variable-index path: emit ArrayLoad IR (works for both constant and variable)
        tacky::Val idx_val = visitExpression(expr->index.get());
        tacky::Temporary tmp = make_temp(array_elem_types.at(qualified));
        emit(tacky::ArrayLoad{qualified, idx_val, tmp,
                              array_elem_types.at(qualified),
                              array_sizes.at(qualified)});
        return tmp;
      } else {
        // Constant-index only path: return synthetic scalar variable directly
        auto *c = dynamic_cast<const IntegerLiteral *>(expr->index.get());
        if (!c) throw std::runtime_error("Array subscript must be a compile-time constant");
        std::string elem_name = qualified + "__" + std::to_string(c->value);
        return tacky::Variable{elem_name, array_elem_types.at(qualified)};
      }
    }
  }

  // Bit-slice path (existing behaviour for register access like PORTB[0])
  tacky::Val target = visitExpression(expr->target.get());
  tacky::Val indexVal = visitExpression(expr->index.get());

  // Resolve target from constant_address_variables (ptr-returning inline helper).
  auto resolve_addr = [&](tacky::Val &v) {
    auto try_name = [&](const std::string &name) -> bool {
      if (constant_address_variables.contains(name)) {
        v = tacky::MemoryAddress{constant_address_variables.at(name)};
        return true;
      }
      return false;
    };
    if (const auto t = std::get_if<tacky::Temporary>(&v)) return try_name(t->name);
    if (const auto var = std::get_if<tacky::Variable>(&v)) return try_name(var->name);
    return false;
  };
  resolve_addr(target);

  int bit = 0;
  if (auto c = std::get_if<tacky::Constant>(&indexVal)) {
    bit = c->value;
  } else {
    auto try_const = [&](const std::string &name) -> bool {
      if (constant_variables.contains(name)) { bit = constant_variables.at(name); return true; }
      return false;
    };
    bool resolved = false;
    if (const auto t = std::get_if<tacky::Temporary>(&indexVal)) resolved = try_const(t->name);
    else if (const auto v = std::get_if<tacky::Variable>(&indexVal)) resolved = try_const(v->name);
    if (!resolved) throw std::runtime_error("Bit index must be constant for reading");
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
    // This handles Pin.OUTPUT / _Pin.OUTPUT where OUTPUT is defined at module scope.
    if (imported_aliases.contains(varExpr->name)) {
      std::string mod_name = imported_aliases.at(varExpr->name);
      // Use original class name if this is an alias (e.g. _Pin -> Pin)
      const std::string &original_name =
          alias_to_original.count(varExpr->name)
              ? alias_to_original.at(varExpr->name)
              : varExpr->name;
      std::string mod_prefix = mod_name;
      std::replace(mod_prefix.begin(), mod_prefix.end(), '.', '_');
      std::string mod_mangled = mod_prefix + "_" + expr->member;
      if (globals.contains(mod_mangled)) {
        const auto &sym = globals.at(mod_mangled);
        if (sym.is_memory_address)
          return tacky::MemoryAddress{sym.value, sym.type};
        return tacky::Constant{sym.value};
      }
      // Also check class-level constant with full prefix (uses original name)
      std::string class_mangled =
          mod_prefix + "_" + original_name + "_" + expr->member;
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
    // Check if it's a ptr-typed compile-time member (e.g. self._port = PORTB)
    if (constant_address_variables.contains(flattened_name)) {
      return tacky::MemoryAddress{constant_address_variables.at(flattened_name)};
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
  // ── super().__init__(...) / super().method(...) desugaring ───────────────
  // Pattern: CallExpr( MemberAccessExpr( CallExpr(VariableExpr("super"),[]), method ), args )
  if (const auto *mem = dynamic_cast<const MemberAccessExpr *>(expr->callee.get())) {
    if (const auto *super_call =
            dynamic_cast<const CallExpr *>(mem->object.get())) {
      if (const auto *super_var =
              dynamic_cast<const VariableExpr *>(super_call->callee.get())) {
        if (super_var->name == "super") {
          // Determine the base class prefix from current_module_prefix.
          // current_module_prefix is "ChildClass_" while inside its methods.
          std::string child_class = current_module_prefix.empty()
              ? ""
              : current_module_prefix.substr(0, current_module_prefix.size() - 1);
          if (class_base_prefixes.contains(child_class)) {
            std::string base_prefix = class_base_prefixes.at(child_class);
            std::string base_method = base_prefix + mem->member;
            // Rebuild a synthetic CallExpr targeting the base method, then
            // fall through to the normal inline dispatch below.
            // We do this by simply setting callee and jumping to inline handling.
            std::string callee = base_method;
            // inline dispatch for the base method (copy of the inline path below)
            if (inline_functions.contains(callee)) {
              const FunctionDef *func = inline_functions.at(callee);
              std::string exit_label = make_label();
              int new_depth = inline_depth + 1;
              std::string new_prefix =
                  "inline" + std::to_string(new_depth) + "_" + func->name + "_";
              // Bind self alias to the current inline self (or pending_constructor_target)
              std::string self_alias = current_inline_prefix + "self";
              if (variable_aliases.contains(self_alias))
                variable_aliases[new_prefix + "self"] = variable_aliases.at(self_alias);
              else if (!pending_constructor_target.empty())
                variable_aliases[new_prefix + "self"] = pending_constructor_target;
              // Bind positional args (skip self param).
              int param_idx = 0;
              for (const auto &p : func->params) {
                if (p.name == "self") continue;
                if (param_idx < (int)expr->args.size()) {
                  tacky::Val arg_val = visitExpression(expr->args[param_idx].get());
                  std::string param_key = new_prefix + p.name;
                  if (auto *v = std::get_if<tacky::Variable>(&arg_val)) {
                    variable_aliases[param_key] = v->name;
                  } else {
                    tacky::Variable param_var{param_key, DataType::UINT8};
                    emit(tacky::Copy{arg_val, param_var});
                  }
                  param_idx++;
                }
              }
              std::string saved_prefix = current_inline_prefix;
              std::string saved_mod = current_module_prefix;
              int saved_depth = inline_depth;
              current_inline_prefix = new_prefix;
              current_module_prefix = base_prefix;
              inline_depth = new_depth;
              inline_stack.push_back({exit_label, std::nullopt, {}, ""});
              visitBlock(dynamic_cast<const Block *>(func->body.get()));
              emit(tacky::Label{exit_label});
              inline_stack.pop_back();
              current_inline_prefix = saved_prefix;
              current_module_prefix = saved_mod;
              inline_depth = saved_depth;
              return std::monostate{};
            }
          }
        }
      }
    }
  }

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
      } else if (varExpr->name == "int") {
        // int.from_bytes() pseudo-static intrinsic
        callee = "int_" + mem->member;
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

  // F9: Lambda call — if callee resolves to a variable bound to a lambda, expand inline.
  {
    // Build qualified name of the variable (matches the key in lambda_variable_names).
    std::string qcallee;
    if (const auto *var = dynamic_cast<const VariableExpr *>(expr->callee.get())) {
      if (!current_inline_prefix.empty())
        qcallee = current_inline_prefix + var->name;
      else if (!current_function.empty())
        qcallee = current_function + "." + var->name;
      else
        qcallee = var->name;
    }
    // Also try the raw resolved callee as a key.
    std::string lambda_key;
    if (!qcallee.empty() && lambda_variable_names.contains(qcallee))
      lambda_key = lambda_variable_names.at(qcallee);
    else if (lambda_variable_names.contains(callee))
      lambda_key = lambda_variable_names.at(callee);

    if (!lambda_key.empty() && lambda_functions_map.contains(lambda_key)) {
      const LambdaExpr *lam = lambda_functions_map.at(lambda_key);
      // Inline-expand: bind params, evaluate body, restore context.
      std::string pfx = "__lam" + std::to_string(lambda_counter++) + "_";
      for (int i = 0; i < (int)lam->params.size() && i < (int)expr->args.size(); ++i) {
        std::string param_key = pfx + lam->params[i].name;
        tacky::Val arg_val = visitExpression(expr->args[i].get());
        DataType dt = resolve_type(lam->params[i].type);
        if (auto *c = std::get_if<tacky::Constant>(&arg_val)) {
          constant_variables[param_key] = c->value;
        } else {
          tacky::Variable pvar{param_key, dt};
          emit(tacky::Copy{arg_val, pvar});
          variable_types[param_key] = dt;
        }
      }
      std::string saved_inline = current_inline_prefix;
      current_inline_prefix = pfx;
      tacky::Val result = visitExpression(lam->body.get());
      current_inline_prefix = saved_inline;
      // Clean up param bindings.
      for (const auto &p : lam->params) {
        std::string param_key = pfx + p.name;
        constant_variables.erase(param_key);
        variable_types.erase(param_key);
      }
      return result;
    }
  }

  // Constructor support: Class() -> Class___init__()
  if (inline_functions.contains(callee + "___init__")) {
    callee += "___init__";
  }

  // ── Overload resolution: callee has type-based overloads ─────────────────
  if (overloaded_functions.contains(callee)) {
    // Build suffix from actual arg types (skip self / keyword args).
    std::string suffix;
    bool first = true;
    for (const auto &arg : expr->args) {
      if (dynamic_cast<const KeywordArgExpr *>(arg.get())) continue;
      if (!first) suffix += "_";
      first = false;
      suffix += datatype_to_suffix_str(infer_expr_type(arg.get()));
    }
    if (suffix.empty()) suffix = "void";
    std::string mangled = callee + "___" + suffix;
    if (inline_functions.contains(mangled)) {
      callee = mangled;
    } else {
      // Try to find any overload that matches by arg count (fallback).
      int arg_count = 0;
      for (const auto &arg : expr->args)
        if (!dynamic_cast<const KeywordArgExpr *>(arg.get())) ++arg_count;
      for (const auto &[key, _] : inline_functions) {
        if (key.find(callee + "___") == 0) {
          // Count non-self params of this overload.
          const FunctionDef *cand = inline_functions.at(key);
          int cand_params = 0;
          for (const auto &p : cand->params)
            if (p.name != "self") ++cand_params;
          if (cand_params == arg_count) {
            callee = key;
            break;
          }
        }
      }
    }
  }

  // Normalise time intrinsics: sleep_ms / time_sleep_ms / pymcu_time_sleep_ms
  // → the actual delay_ms inline function (arch-specific tight loop).
  // The module may be loaded as "pymcu.time" or "time" depending on how it is
  // imported, so we search inline_functions by suffix rather than hard-coding a
  // prefix.  This lets users write `import time; time.sleep_ms(ms)` or
  // `from time import sleep_ms; sleep_ms(ms)` without any extra import.
  {
    bool is_sleep_ms = (callee == "sleep_ms"   || callee == "time_sleep_ms" ||
                        callee == "pymcu_time_sleep_ms");
    bool is_sleep_us = (callee == "sleep_us"   || callee == "time_sleep_us" ||
                        callee == "pymcu_time_sleep_us");
    if (is_sleep_ms || is_sleep_us) {
      const std::string target_suffix = is_sleep_ms ? "delay_ms" : "delay_us";
      // Prefer exact match first (fast path).
      std::string candidate = "pymcu_time_" + target_suffix;
      if (!inline_functions.contains(candidate)) {
        // Fall back to suffix search (module loaded as "time" vs "pymcu.time").
        candidate = "";
        for (const auto &[fn_name, _] : inline_functions) {
          if (fn_name.size() > target_suffix.size() &&
              fn_name.substr(fn_name.size() - target_suffix.size()) == target_suffix) {
            candidate = fn_name;
            break;
          }
        }
      }
      if (!candidate.empty()) callee = candidate;
    }
  }

  // len() — compile-time constant: returns the element count of a fixed-size array,
  // or the size of a list literal.
  if (callee == "len") {
    if (expr->args.size() != 1)
      throw std::runtime_error("len() expects exactly one argument");
    // List literal: len([1,2,3]) → 3
    if (const auto *le = dynamic_cast<const ListExpr *>(expr->args[0].get())) {
      return tacky::Constant{(int)le->elements.size()};
    }
    // Array variable: len(buf) → compile-time constant from array_sizes
    if (const auto *v = dynamic_cast<const VariableExpr *>(expr->args[0].get())) {
      // Try fully qualified with inline prefix first
      if (!current_inline_prefix.empty()) {
        std::string key = current_inline_prefix + v->name;
        if (array_sizes.contains(key)) {
          return tacky::Constant{array_sizes.at(key)};
        }
      }
      // Try with current function prefix (normal non-inline context)
      if (!current_function.empty()) {
        std::string key = current_function + "." + v->name;
        if (array_sizes.contains(key)) {
          return tacky::Constant{array_sizes.at(key)};
        }
      }
      // Try bare name (module-level array)
      if (array_sizes.contains(v->name)) {
        return tacky::Constant{array_sizes.at(v->name)};
      }
    }
    throw std::runtime_error("len() argument must be a fixed-size array or list literal");
  }

  // int.from_bytes(b, endian) — pack 2 bytes into uint16.
  // Compile-time fold when b is a constant list; runtime: (hi<<8)|lo or (lo<<8)|hi.
  // Syntax: int.from_bytes(b"\x01\x02", 'little')  or  int.from_bytes([lo, hi], 'little')
  // Object 'int' is not a module, so callee is resolved as "int_from_bytes" via
  // the bare-name flattening path (varExpr->name + "_" + member).
  if (callee == "int_from_bytes") {
    if (expr->args.size() != 2)
      throw std::runtime_error("int.from_bytes() expects exactly two arguments (bytes, endian)");
    // Determine endianness from second arg (string literal 'little' or 'big')
    bool little_endian = true;
    if (const auto *estr = dynamic_cast<const StringLiteral *>(expr->args[1].get())) {
      if (estr->value == "big") little_endian = false;
      else if (estr->value != "little")
        throw std::runtime_error("int.from_bytes() endian must be 'little' or 'big'");
    } else {
      throw std::runtime_error("int.from_bytes() endian argument must be a string literal");
    }
    // First arg: list literal (from bytes literal or explicit list)
    if (const auto *le = dynamic_cast<const ListExpr *>(expr->args[0].get())) {
      if (le->elements.size() < 2)
        throw std::runtime_error("int.from_bytes() requires at least 2 bytes");
      tacky::Val b0 = visitExpression(le->elements[0].get());
      tacky::Val b1 = visitExpression(le->elements[1].get());
      // Constant fold
      if (auto c0 = std::get_if<tacky::Constant>(&b0))
        if (auto c1 = std::get_if<tacky::Constant>(&b1)) {
          int val = little_endian
              ? ((c1->value & 0xFF) << 8) | (c0->value & 0xFF)
              : ((c0->value & 0xFF) << 8) | (c1->value & 0xFF);
          return tacky::Constant{val};
        }
      // Runtime: emit (hi << 8) | lo
      tacky::Val lo_val = little_endian ? b0 : b1;
      tacky::Val hi_val = little_endian ? b1 : b0;
      tacky::Temporary hi_shifted = make_temp(DataType::UINT16);
      tacky::Temporary result_t   = make_temp(DataType::UINT16);
      emit(tacky::Binary{tacky::BinaryOp::LShift, hi_val, tacky::Constant{8}, hi_shifted});
      emit(tacky::Binary{tacky::BinaryOp::BitOr, hi_shifted, lo_val, result_t});
      return result_t;
    }
    throw std::runtime_error(
        "int.from_bytes() first argument must be a bytes literal b\"...\" or list [lo, hi]");
  }

  // T1.4: abs(), min(), max() built-in intrinsics.
  if (callee == "abs") {
    if (expr->args.size() != 1)
      throw std::runtime_error("abs() expects exactly one argument");
    tacky::Val v = visitExpression(expr->args[0].get());
    if (auto c = std::get_if<tacky::Constant>(&v))
      return tacky::Constant{c->value < 0 ? -c->value : c->value};
    // Emit: if v < 0: result = -v else result = v
    std::string neg_label = make_label();
    std::string end_label = make_label();
    tacky::Temporary result = make_temp(DataType::UINT8);
    tacky::Temporary negv   = make_temp(DataType::UINT8);
    emit(tacky::Binary{tacky::BinaryOp::LessThan, v, tacky::Constant{0}, negv});
    emit(tacky::JumpIfNotZero{negv, neg_label});
    emit(tacky::Copy{v, result});
    emit(tacky::Jump{end_label});
    emit(tacky::Label{neg_label});
    tacky::Temporary neg_result = make_temp(DataType::UINT8);
    emit(tacky::Binary{tacky::BinaryOp::Sub, tacky::Constant{0}, v, neg_result});
    emit(tacky::Copy{neg_result, result});
    emit(tacky::Label{end_label});
    return result;
  }
  if (callee == "min") {
    if (expr->args.size() != 2)
      throw std::runtime_error("min() expects exactly two arguments");
    tacky::Val a = visitExpression(expr->args[0].get());
    tacky::Val b = visitExpression(expr->args[1].get());
    if (auto ca = std::get_if<tacky::Constant>(&a))
      if (auto cb = std::get_if<tacky::Constant>(&b))
        return tacky::Constant{ca->value < cb->value ? ca->value : cb->value};
    std::string else_label = make_label();
    std::string end_label  = make_label();
    tacky::Temporary result = make_temp(DataType::UINT8);
    tacky::Temporary cmp    = make_temp(DataType::UINT8);
    emit(tacky::Binary{tacky::BinaryOp::LessThan, a, b, cmp});
    emit(tacky::JumpIfZero{cmp, else_label});
    emit(tacky::Copy{a, result});
    emit(tacky::Jump{end_label});
    emit(tacky::Label{else_label});
    emit(tacky::Copy{b, result});
    emit(tacky::Label{end_label});
    return result;
  }
  if (callee == "max") {
    if (expr->args.size() != 2)
      throw std::runtime_error("max() expects exactly two arguments");
    tacky::Val a = visitExpression(expr->args[0].get());
    tacky::Val b = visitExpression(expr->args[1].get());
    if (auto ca = std::get_if<tacky::Constant>(&a))
      if (auto cb = std::get_if<tacky::Constant>(&b))
        return tacky::Constant{ca->value > cb->value ? ca->value : cb->value};
    std::string else_label = make_label();
    std::string end_label  = make_label();
    tacky::Temporary result = make_temp(DataType::UINT8);
    tacky::Temporary cmp    = make_temp(DataType::UINT8);
    emit(tacky::Binary{tacky::BinaryOp::GreaterThan, a, b, cmp});
    emit(tacky::JumpIfZero{cmp, else_label});
    emit(tacky::Copy{a, result});
    emit(tacky::Jump{end_label});
    emit(tacky::Label{else_label});
    emit(tacky::Copy{b, result});
    emit(tacky::Label{end_label});
    return result;
  }

  // T1.5: ord() and chr() compile-time intrinsics.
  // ord('A') → 65; chr(65) → 65 (identity — result used as byte value).
  if (callee == "ord") {
    if (expr->args.size() != 1)
      throw std::runtime_error("ord() expects exactly one argument");
    // Accept single-char string literal
    if (const auto *str = dynamic_cast<const StringLiteral *>(expr->args[0].get())) {
      if (str->value.size() != 1)
        throw std::runtime_error("ord() argument must be a single character");
      return tacky::Constant{(int)(unsigned char)str->value[0]};
    }
    // Accept integer (already converted to Constant via single-char string path)
    tacky::Val v = visitExpression(expr->args[0].get());
    return v;  // pass-through for variable args
  }
  if (callee == "chr") {
    if (expr->args.size() != 1)
      throw std::runtime_error("chr() expects exactly one argument");
    // Constant fold: chr(65) → 65 (identity — byte sent to uart.write())
    tacky::Val v = visitExpression(expr->args[0].get());
    return v;  // chr is identity for byte values
  }

  // sum(list_or_array) — compile-time fold or unrolled addition
  if (callee == "sum") {
    if (expr->args.size() != 1)
      throw std::runtime_error("sum() expects exactly one argument");
    // List literal: sum([1, 2, 3]) or sum([a, b, c])
    if (const auto *le = dynamic_cast<const ListExpr *>(expr->args[0].get())) {
      if (le->elements.empty()) return tacky::Constant{0};
      tacky::Val acc = visitExpression(le->elements[0].get());
      for (size_t i = 1; i < le->elements.size(); ++i) {
        tacky::Val v = visitExpression(le->elements[i].get());
        if (auto ca = std::get_if<tacky::Constant>(&acc))
          if (auto cv = std::get_if<tacky::Constant>(&v)) {
            acc = tacky::Constant{ca->value + cv->value};
            continue;
          }
        tacky::Temporary t = make_temp(DataType::UINT8);
        emit(tacky::Binary{tacky::BinaryOp::Add, acc, v, t});
        acc = t;
      }
      return acc;
    }
    // Array variable: sum(arr) — unroll additions using array_sizes
    if (const auto *v = dynamic_cast<const VariableExpr *>(expr->args[0].get())) {
      // Determine array size
      int arr_size = -1;
      std::string arr_base;
      if (!current_inline_prefix.empty()) {
        std::string key = current_inline_prefix + v->name;
        if (array_sizes.contains(key)) { arr_size = array_sizes.at(key); arr_base = key; }
      }
      if (arr_size < 0 && !current_function.empty()) {
        std::string key = current_function + "." + v->name;
        if (array_sizes.contains(key)) { arr_size = array_sizes.at(key); arr_base = key; }
      }
      if (arr_size < 0 && array_sizes.contains(v->name)) {
        arr_size = array_sizes.at(v->name); arr_base = v->name;
      }
      if (arr_size <= 0)
        throw std::runtime_error("sum() requires a list literal or fixed-size array");
      // Unroll: acc = arr[0] + arr[1] + ...
      tacky::Val acc = tacky::Variable{arr_base + "__0", DataType::UINT8};
      for (int i = 1; i < arr_size; ++i) {
        tacky::Val vi = tacky::Variable{arr_base + "__" + std::to_string(i), DataType::UINT8};
        tacky::Temporary t = make_temp(DataType::UINT8);
        emit(tacky::Binary{tacky::BinaryOp::Add, acc, vi, t});
        acc = t;
      }
      return acc;
    }
    throw std::runtime_error("sum() requires a list literal or fixed-size array");
  }

  // any(list) — OR-chain: returns 1 if any element is non-zero
  if (callee == "any") {
    if (expr->args.size() != 1)
      throw std::runtime_error("any() expects exactly one argument");
    const auto *le = dynamic_cast<const ListExpr *>(expr->args[0].get());
    if (!le) throw std::runtime_error("any() requires a list literal argument");
    if (le->elements.empty()) return tacky::Constant{0};
    // Evaluate elements; constant-fold if all constants
    bool all_const = true;
    for (const auto &e : le->elements) {
      tacky::Val v = visitExpression(e.get());
      if (auto c = std::get_if<tacky::Constant>(&v)) {
        if (c->value != 0) return tacky::Constant{1};
      } else {
        all_const = false;
      }
    }
    if (all_const) return tacky::Constant{0};
    // Runtime OR-chain
    tacky::Temporary result = make_temp(DataType::UINT8);
    emit(tacky::Copy{tacky::Constant{0}, result});
    for (const auto &e : le->elements) {
      tacky::Val v = visitExpression(e.get());
      tacky::Temporary cmp = make_temp(DataType::UINT8);
      emit(tacky::Binary{tacky::BinaryOp::NotEqual, v, tacky::Constant{0}, cmp});
      std::string end_lbl = make_label();
      emit(tacky::JumpIfNotZero{result, end_lbl});
      emit(tacky::Copy{cmp, result});
      emit(tacky::Label{end_lbl});
    }
    return result;
  }

  // all(list) — AND-chain: returns 1 if all elements are non-zero
  if (callee == "all") {
    if (expr->args.size() != 1)
      throw std::runtime_error("all() expects exactly one argument");
    const auto *le = dynamic_cast<const ListExpr *>(expr->args[0].get());
    if (!le) throw std::runtime_error("all() requires a list literal argument");
    if (le->elements.empty()) return tacky::Constant{1};
    // Evaluate elements; constant-fold if all constants
    bool all_const = true;
    for (const auto &e : le->elements) {
      tacky::Val v = visitExpression(e.get());
      if (auto c = std::get_if<tacky::Constant>(&v)) {
        if (c->value == 0) return tacky::Constant{0};
      } else {
        all_const = false;
      }
    }
    if (all_const) return tacky::Constant{1};
    // Runtime AND-chain
    tacky::Temporary result = make_temp(DataType::UINT8);
    emit(tacky::Copy{tacky::Constant{1}, result});
    for (const auto &e : le->elements) {
      tacky::Val v = visitExpression(e.get());
      tacky::Temporary cmp = make_temp(DataType::UINT8);
      emit(tacky::Binary{tacky::BinaryOp::NotEqual, v, tacky::Constant{0}, cmp});
      std::string end_lbl = make_label();
      emit(tacky::JumpIfZero{result, end_lbl});
      emit(tacky::Copy{cmp, result});
      emit(tacky::Label{end_lbl});
    }
    return result;
  }

  // hex(n) — compile-time only: integer to "0xNN" hex string
  if (callee == "hex") {
    if (expr->args.size() != 1)
      throw std::runtime_error("hex() expects exactly one argument");
    tacky::Val v = visitExpression(expr->args[0].get());
    auto c = std::get_if<tacky::Constant>(&v);
    if (!c) throw std::runtime_error("hex() argument must be a compile-time constant integer");
    // Format as "0x" + lowercase hex
    unsigned int uval = static_cast<unsigned int>(c->value);
    char buf[32];
    std::snprintf(buf, sizeof(buf), "0x%x", uval);
    std::string hexstr(buf);
    if (string_literal_ids.find(hexstr) == string_literal_ids.end()) {
      string_literal_ids[hexstr] = next_string_id;
      string_id_to_str[next_string_id] = hexstr;
      next_string_id++;
    }
    return tacky::Constant{string_literal_ids[hexstr]};
  }

  // bin(n) — compile-time only: integer to "0bNNN" binary string
  if (callee == "bin") {
    if (expr->args.size() != 1)
      throw std::runtime_error("bin() expects exactly one argument");
    tacky::Val v = visitExpression(expr->args[0].get());
    auto c = std::get_if<tacky::Constant>(&v);
    if (!c) throw std::runtime_error("bin() argument must be a compile-time constant integer");
    unsigned int uval = static_cast<unsigned int>(c->value);
    std::string binstr = "0b";
    if (uval == 0) {
      binstr += "0";
    } else {
      // Find the highest set bit
      std::string bits;
      unsigned int tmp = uval;
      while (tmp > 0) {
        bits = (char)('0' + (tmp & 1)) + bits;
        tmp >>= 1;
      }
      binstr += bits;
    }
    if (string_literal_ids.find(binstr) == string_literal_ids.end()) {
      string_literal_ids[binstr] = next_string_id;
      string_id_to_str[next_string_id] = binstr;
      next_string_id++;
    }
    return tacky::Constant{string_literal_ids[binstr]};
  }

  // str(n) — compile-time only: integer to decimal string, e.g. str(42) => "42"
  if (callee == "str") {
    if (expr->args.size() != 1)
      throw std::runtime_error("str() expects exactly one argument");
    tacky::Val v = visitExpression(expr->args[0].get());
    auto c = std::get_if<tacky::Constant>(&v);
    if (!c) throw std::runtime_error("str() argument must be a compile-time constant integer");
    std::string decstr = std::to_string(c->value);
    if (string_literal_ids.find(decstr) == string_literal_ids.end()) {
      string_literal_ids[decstr] = next_string_id;
      string_id_to_str[next_string_id] = decstr;
      next_string_id++;
    }
    return tacky::Constant{string_literal_ids[decstr]};
  }

  // pow(x, n) — compile-time only: integer power x**n
  if (callee == "pow") {
    if (expr->args.size() != 2)
      throw std::runtime_error("pow() expects exactly two arguments");
    tacky::Val bv = visitExpression(expr->args[0].get());
    tacky::Val ev = visitExpression(expr->args[1].get());
    auto cb = std::get_if<tacky::Constant>(&bv);
    auto ce = std::get_if<tacky::Constant>(&ev);
    if (!cb || !ce)
      throw std::runtime_error("pow() arguments must be compile-time constant integers");
    int base = cb->value;
    int exp  = ce->value;
    if (exp < 0) throw std::runtime_error("pow() negative exponent not supported");
    int result = 1;
    for (int k = 0; k < exp; ++k) result *= base;
    return tacky::Constant{result};
  }

  // divmod(a, b) — returns (quotient, remainder) by calling __div8 and __mod8.
  // Use as: q, r = divmod(a, b)
  if (callee == "divmod") {
    if (expr->args.size() != 2)
      throw std::runtime_error("divmod() expects exactly two arguments");
    tacky::Val a_val = visitExpression(expr->args[0].get());
    tacky::Val b_val = visitExpression(expr->args[1].get());
    // Constant fold
    if (auto ca = std::get_if<tacky::Constant>(&a_val))
      if (auto cb = std::get_if<tacky::Constant>(&b_val)) {
        if (cb->value == 0)
          throw std::runtime_error("divmod(): division by zero");
        int q = ca->value / cb->value;
        int r = ca->value % cb->value;
        if (pending_tuple_count_ == 2) {
          std::string base = current_function.empty() ? "main" : current_function;
          std::string qn = base + ".divmod_q" + std::to_string(temp_counter);
          std::string rn = base + ".divmod_r" + std::to_string(temp_counter + 1);
          temp_counter += 2;
          emit(tacky::Copy{tacky::Constant{q}, tacky::Variable{qn, DataType::UINT8}});
          emit(tacky::Copy{tacky::Constant{r}, tacky::Variable{rn, DataType::UINT8}});
          last_tuple_results_ = {qn, rn};
          return std::monostate{};
        }
        return tacky::Constant{q};
      }
    // Runtime: call __div8 for quotient, __mod8 for remainder
    if (pending_tuple_count_ == 2) {
      std::string base = current_function.empty() ? "main" : current_function;
      std::string qn = base + ".divmod_q" + std::to_string(temp_counter);
      std::string rn = base + ".divmod_r" + std::to_string(temp_counter + 1);
      temp_counter += 2;
      tacky::Variable qvar{qn, DataType::UINT8};
      tacky::Variable rvar{rn, DataType::UINT8};
      // Call __div8(a, b) → quotient in R24
      tacky::Call div_call;
      div_call.function_name = "__div8";
      div_call.args = {a_val, b_val};
      div_call.dst = qvar;
      emit(div_call);
      // Call __mod8(a, b) → remainder in R24
      tacky::Call mod_call;
      mod_call.function_name = "__mod8";
      mod_call.args = {a_val, b_val};
      mod_call.dst = rvar;
      emit(mod_call);
      last_tuple_results_ = {qn, rn};
      return std::monostate{};
    }
    // Single-value context: return only quotient
    tacky::Temporary q_tmp = make_temp(DataType::UINT8);
    tacky::Call div_call;
    div_call.function_name = "__div8";
    div_call.args = {a_val, b_val};
    div_call.dst = q_tmp;
    emit(div_call);
    return q_tmp;
  }

  // T2.4: Type cast intrinsics — uint8(val), uint16(val), uint32(val),
  // int8(val), int16(val), int32(val).  Emit a Cast IR instruction so the
  // backend can truncate/extend the value to the target width.
  static const std::map<std::string, DataType> cast_types = {
      {"uint8",  DataType::UINT8},  {"uint16", DataType::UINT16},
      {"uint32", DataType::UINT32}, {"int8",   DataType::INT8},
      {"int16",  DataType::INT16},  {"int32",  DataType::INT32},
  };
  if (cast_types.contains(callee)) {
    if (expr->args.size() != 1)
      throw std::runtime_error(callee + "() expects exactly one argument");
    tacky::Val v = visitExpression(expr->args[0].get());
    DataType dst_type = cast_types.at(callee);
    // Constant fold: truncate integer literal to target width.
    if (auto c = std::get_if<tacky::Constant>(&v)) {
      int val = c->value;
      switch (dst_type) {
        case DataType::UINT8:  val = (uint8_t)val; break;
        case DataType::UINT16: val = (uint16_t)val; break;
        case DataType::INT8:   val = (int8_t)val; break;
        case DataType::INT16:  val = (int16_t)val; break;
        default: break;
      }
      return tacky::Constant{val};
    }
    // Runtime cast: emit a Copy into a typed temporary.
    // AVR backend uses the type to decide how many bytes to move.
    tacky::Temporary dst = make_temp(dst_type);
    emit(tacky::Copy{v, dst});
    return dst;
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
    emit(tacky::UARTSendString{text, add_nl ? "\n" : ""});
    return std::monostate{};
  }

  // Handle print() — Python-style print routed to UART.
  // Supports standard Python semantics:
  //   print(a, b, ..., sep=" ", end="\n")
  // Each positional arg is emitted as:
  //   - string literal / const[str] → UARTSendString (flash pool, zero overhead)
  //   - uint8/int value             → uart_write_decimal_u8 (decimal digits)
  // sep is emitted between args; end is emitted after all args.
  if (callee == "print") {
    // --- 1. Extract keyword args: end (default "\n") and sep (default " ") ---
    std::string end_str = "\n";
    std::string sep_str = " ";
    for (const auto &arg : expr->args) {
      if (const auto *kw = dynamic_cast<const KeywordArgExpr *>(arg.get())) {
        if (kw->key == "end" || kw->key == "sep") {
          if (const auto *lit = dynamic_cast<const StringLiteral *>(kw->value.get())) {
            if (kw->key == "end") end_str = lit->value;
            else                  sep_str = lit->value;
          } else {
            throw std::runtime_error(
              "print() '" + kw->key + "' must be a compile-time string literal");
          }
        }
      }
    }

    // --- 2. Collect positional args ---
    std::vector<const Expression *> pos_args;
    for (const auto &arg : expr->args) {
      if (!dynamic_cast<const KeywordArgExpr *>(arg.get()))
        pos_args.push_back(arg.get());
    }

    // Helper: emit a string value (sep/end) via flash string pool.
    // Always uses UARTSendString — avoids calling uart_write directly, since
    // uart_write is @inline (no label emitted) and cannot be called as a symbol.
    auto emit_str = [&](const std::string &s) {
      if (s.empty()) return;
      emit(tacky::UARTSendString{s, ""});
    };

    // Resolve the arch-specific decimal writer function name.
    // uart_write_decimal_u8 is compiled from the UART HAL module with a module
    // prefix (e.g., "pymcu_hal__uart_avr_uart_write_decimal_u8"). We need the
    // exact IR name so that Dead Function Elimination keeps the function and
    // the AVR codegen can emit a valid label reference.
    // Search function_return_types for a function whose name ends with the suffix.
    std::string decimal_write_fn = resolve_callee("uart_write_decimal_u8");
    if (decimal_write_fn == "uart_write_decimal_u8") {
      // resolve_callee fell back to bare name — try suffix search in compiled fns
      static const std::string DEC_SUFFIX = "uart_write_decimal_u8";
      for (const auto &[fn_name, _rt] : function_return_types) {
        if (fn_name.size() > DEC_SUFFIX.size() &&
            fn_name.substr(fn_name.size() - DEC_SUFFIX.size()) == DEC_SUFFIX) {
          decimal_write_fn = fn_name;
          break;
        }
      }
    }

    // Helper: emit one positional argument
    auto emit_print_arg = [&](const Expression *arg) {
      // String literal
      if (const auto *lit = dynamic_cast<const StringLiteral *>(arg)) {
        emit(tacky::UARTSendString{lit->value, ""});
        return;
      }
      // const[str] variable
      if (const auto *var = dynamic_cast<const VariableExpr *>(arg)) {
        std::string key = current_inline_prefix + var->name;
        if (auto str_val = resolve_str_constant(key)) {
          emit(tacky::UARTSendString{*str_val, ""});
          return;
        }
      }
      // uint8/int value → decimal digits
      tacky::Val val = visitExpression(arg);
      tacky::Temporary tmp = make_temp(DataType::UINT8);
      emit(tacky::Copy{val, tmp});
      emit(tacky::Call{decimal_write_fn, {tmp}, tmp});
    };

    if (pos_args.empty()) {
      // print() with no args → just the end string
      emit_str(end_str);
      return std::monostate{};
    }

    // --- 3. Emit each positional arg with sep between them ---
    for (size_t i = 0; i < pos_args.size(); ++i) {
      if (i > 0) emit_str(sep_str);
      emit_print_arg(pos_args[i]);
    }

    // --- 4. Emit end string ---
    emit_str(end_str);
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

  // Handle compile_isr(handler, vector) intrinsic.
  // Marks the referenced handler function as an ISR at the given vector,
  // eliminating the need for a manual @interrupt(vector) decorator.
  // Called from pin_irq_setup() when Pin.irq(trigger, handler) is used.
  // handler is a function reference (bare identifier), vector is a constant.
  if (callee == "compile_isr" && intrinsic_names.contains("compile_isr")) {
    if (expr->args.size() != 2) {
      throw std::runtime_error("compile_isr() expects exactly 2 arguments: "
                               "compile_isr(handler, vector)");
    }

    // Resolve the interrupt vector (arg 1) — must be compile-time constant
    tacky::Val vec_val = visitExpression(expr->args[1].get());
    int vector = 0;
    if (auto c = std::get_if<tacky::Constant>(&vec_val)) {
      vector = c->value;
    } else {
      throw std::runtime_error(
          "compile_isr() second argument (vector) must be a compile-time "
          "constant");
    }

    // Resolve the handler function name (arg 0).
    // The handler is passed as a bare identifier (e.g. on_press) through one
    // or more levels of @inline expansion.  It lives in variable_aliases as a
    // chain ending at "<enclosing_func>.<func_name>", e.g. "main.on_press".
    std::string handler_func_name;
    bool handler_provided = false;

    if (auto *var = dynamic_cast<const VariableExpr *>(expr->args[0].get())) {
      std::string key = current_inline_prefix + var->name;

      // If the handler slot holds Constant{0} the caller used the default
      // (no handler provided) — silently skip ISR registration.
      if (constant_variables.count(key) && constant_variables.at(key) == 0) {
        return std::monostate{};
      }

      // Follow the variable_aliases chain to the root name.
      for (int depth = 0; depth < 20; ++depth) {
        auto it = variable_aliases.find(key);
        if (it == variable_aliases.end()) break;
        key = it->second;
      }

      // key is now something like "main.on_press" (enclosing-function prefix
      // + "." + bare-function-name).  Strip everything up to the first dot.
      auto dot_pos = key.find('.');
      handler_func_name =
          (dot_pos != std::string::npos) ? key.substr(dot_pos + 1) : key;
      handler_provided = !handler_func_name.empty();
    } else {
      // Arg was evaluated to a Constant — 0 means no handler.
      tacky::Val arg0 = visitExpression(expr->args[0].get());
      if (auto c = std::get_if<tacky::Constant>(&arg0)) {
        if (c->value == 0) return std::monostate{};
      }
      throw std::runtime_error(
          "compile_isr() first argument must be a function reference or 0");
    }

    if (!handler_provided) return std::monostate{};

    // Store the registration for apply in generate() after all functions are
    // compiled. generate() has access to ir_program.functions; visitCall does
    // not (ir_program is a local variable there).
    pending_isr_registrations[handler_func_name] = vector;
    return std::monostate{};
  }

  // @extern fast-path: emit Call directly to the C symbol, no inlining.
  if (extern_function_map.contains(callee)) {
    const std::string &c_sym = extern_function_map.at(callee);
    std::vector<tacky::Val> ext_args;
    for (const auto &arg : expr->args)
      ext_args.push_back(visitExpression(arg.get()));

    tacky::Call ext_call;
    ext_call.function_name = c_sym;
    ext_call.args = ext_args;

    // Determine return type from the function registration.
    bool returns_void = !function_return_types.contains(callee) ||
                        function_return_types.at(callee) == "void" ||
                        function_return_types.at(callee) == "None";
    if (returns_void) {
      ext_call.dst = std::monostate{};
      emit(ext_call);
      return std::monostate{};
    }
    tacky::Temporary ext_dst = make_temp(
        resolve_type(function_return_types.at(callee)));
    ext_call.dst = ext_dst;
    emit(ext_call);
    return ext_dst;
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
    std::vector<std::string> tuple_result_names;

    if (pending_tuple_count_ > 0) {
      // Tuple multi-return: allocate N named result slots with a single dot so
      // they are register-allocated (not stack-only).  Using 2+ dots would put
      // them on the stack and trigger AVR peephole Pattern A which eliminates
      // the STD/LDD pair, corrupting the value on the second read.
      std::string base = current_function.empty() ? "main" : current_function;
      for (int _k = 0; _k < pending_tuple_count_; ++_k) {
        tuple_result_names.push_back(
            base + ".iret_" + std::to_string(new_depth) + "_" +
            std::to_string(_k));
      }
    } else if (func->return_type != "void" && func->return_type != "None") {
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

    inline_stack.push_back({exit_label, result, tuple_result_names, callee});

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
        // Special case: if the target param is const[str] and the passed
        // variable resolves to a compile-time string constant, propagate it
        // directly into str_constant_variables so downstream match/case string
        // comparisons can constant-fold (e.g. passing cs="PB0" through a
        // const[str] SPI param into Pin's const[str] name param).
        if (func->params[param_idx].type == "const[str]") {
          if (auto str_val = resolve_str_constant(v->name)) {
            str_constant_variables[paramName] = *str_val;
            constant_variables.erase(paramName);
            variable_aliases.erase(paramName);
            continue;
          }
        }
        variable_aliases[paramName] = v->name;
        // Erase stale constant entries from previous inline expansions
        constant_variables.erase(paramName);
        str_constant_variables.erase(paramName);
        // Alias handles all uses of this parameter — emitting a Copy would
        // allocate a second storage location (register + stack slot) for the
        // same variable, causing FunctionCall argument loads to read from the
        // uninitialized stack slot instead of following the alias to the
        // correct register.
        continue;
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
          // 3. Compile-time Constant holding a string ID (e.g. from f-string)
          if (const auto *c = std::get_if<tacky::Constant>(&argValues[i])) {
            if (string_id_to_str.contains(c->value)) {
              str_constant_variables[paramName] = string_id_to_str.at(c->value);
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

      // Propagate compile-time memory addresses (e.g. ptr[uint8] register
      // pointers like PIND passed as pin_reg). Without this, the address is
      // copied into a stack slot and then read once at call time — producing
      // a stale cached register value instead of a live per-iteration read.
      if (const auto m = std::get_if<tacky::MemoryAddress>(&argValues[i])) {
        constant_address_variables[paramName] = m->address;
        constant_address_variables.erase(paramName + "_type");  // no type override
        continue;  // No Copy needed — address is folded at use sites
      }

      // Copy arg to param with proper type from function signature.
      // Erase any stale constant_variables / alias entries from a previous
      // call at the same inline depth where this same param slot held a
      // compile-time constant or was aliased to a caller variable.
      // Without this, body expressions that read the parameter would see
      // the old constant or stale alias instead of the runtime value.
      constant_variables.erase(paramName);
      str_constant_variables.erase(paramName);
      variable_aliases.erase(paramName);
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

    // Capture tuple result names before popping context
    if (!inline_stack.back().result_vars.empty()) {
      last_tuple_results_ = inline_stack.back().result_vars;
    }
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
  // T2.1: Enum/IntEnum classes are zero-cost — all fields were folded into
  // globals by the scan phase. No runtime code to emit.
  for (const auto &base : classNode->bases)
    if (base == "Enum" || base == "IntEnum") return;

  // Phase 2: Static Class Implementation
  if (classNode->body) {
    std::string old_prefix = current_module_prefix;
    current_module_prefix += classNode->name + "_";
    visitStatement(classNode->body.get());
    current_module_prefix = old_prefix;
  }
}