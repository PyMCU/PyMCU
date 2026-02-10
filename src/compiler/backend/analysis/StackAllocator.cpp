#include "StackAllocator.h"
#include <iostream>
#include <variant>

std::pair<std::map<std::string, int>, int> StackAllocator::allocate(const tacky::Program& program) {
    offsets.clear();
    offsets_base.clear();
    call_graph.clear();
    max_stack_usage = 0;

    build_graph(program);

    // Comenzamos el análisis desde 'main'
    if (call_graph.count("main")) {
        calculate_offsets("main", 0);
    }

    return {offsets, max_stack_usage};
}

void StackAllocator::build_graph(const tacky::Program& program) {
    for (const auto& func : program.functions) {
        FunctionNode& node = call_graph[func.name];
        node.name = func.name;

        // Register parameters as locals
        for (const auto& param : func.params) {
            node.locals.insert(param);
        }

        // Lambda auxiliar para registrar variables usadas en instrucciones
        auto register_var = [&](const tacky::Val& val) {
            if (auto v = std::get_if<tacky::Variable>(&val)) node.locals.insert(v->name);
            if (auto t = std::get_if<tacky::Temporary>(&val)) node.locals.insert(t->name);
        };

        for (const auto& instr : func.body) {
            std::visit([&](auto&& arg) {
                using T = std::decay_t<decltype(arg)>;

                // Instrucciones de movimiento de datos
                if constexpr (std::is_same_v<T, tacky::Copy>) {
                    register_var(arg.src);
                    register_var(arg.dst);
                }
                // Aritmética binaria
                else if constexpr (std::is_same_v<T, tacky::Binary>) {
                    register_var(arg.src1);
                    register_var(arg.src2);
                    register_var(arg.dst);
                }
                // Unaria
                else if constexpr (std::is_same_v<T, tacky::Unary>) {
                    register_var(arg.src);
                    register_var(arg.dst);
                }
                // Operaciones de Bits
                else if constexpr (std::is_same_v<T, tacky::BitSet>) { register_var(arg.target); }
                else if constexpr (std::is_same_v<T, tacky::BitClear>) { register_var(arg.target); }
                else if constexpr (std::is_same_v<T, tacky::BitCheck>) {
                    register_var(arg.source);
                    register_var(arg.dst);
                }
                else if constexpr (std::is_same_v<T, tacky::BitWrite>) {
                    register_var(arg.src);
                    register_var(arg.target);
                }
                // Llamadas a función
                else if constexpr (std::is_same_v<T, tacky::Call>) {
                    node.callees.push_back(arg.function_name);

                    // CORRECCIÓN AQUÍ: Eliminamos el check de std::monostate.
                    // Verificamos directamente si el destino es Variable o Temporal.
                    if (auto v = std::get_if<tacky::Variable>(&arg.dst)) node.locals.insert(v->name);
                    if (auto t = std::get_if<tacky::Temporary>(&arg.dst)) node.locals.insert(t->name);
                }
                // Retorno
                else if constexpr (std::is_same_v<T, tacky::Return>) { register_var(arg.value); }
                // Saltos condicionales
                else if constexpr (std::is_same_v<T, tacky::JumpIfZero>) { register_var(arg.condition); }
                else if constexpr (std::is_same_v<T, tacky::JumpIfNotZero>) { register_var(arg.condition); }

                // Etiquetas y saltos incondicionales no usan variables, se ignoran.
            }, instr);
        }
        node.local_size = node.locals.size();
    }
}

void StackAllocator::calculate_offsets(const std::string& func_name, int current_base) {
    FunctionNode& node = call_graph[func_name];

    // Evitar ciclos infinitos en recursión (prohibida en este modelo estático)
    if (node.visited) return;
    node.visited = true;

    // We want the function to be at the deepest possible base among all callers
    // However, for simplicity in this first implementation, we can just ensure 
    // that if it's already been assigned an offset, we only update if the new base is deeper.
    // Wait, the current offsets are per-variable.
    
    bool already_assigned = false;
    if (!node.locals.empty()) {
        const std::string& first_var = *node.locals.begin();
        if (offsets.contains(first_var)) {
            already_assigned = true;
            if (current_base <= offsets_base[func_name]) {
                // Already at a deeper or same level, no need to re-calculate for this path
                node.visited = false;
                return;
            }
        }
    } else if (offsets_base.contains(func_name)) {
        already_assigned = true;
        if (current_base <= offsets_base[func_name]) {
            node.visited = false;
            return;
        }
    }

    offsets_base[func_name] = current_base;

    // 1. Asignar offsets a las variables locales de ESTA función
    int local_offset = 0;
    for (const auto& var_name : node.locals) {
        offsets[var_name] = current_base + local_offset;
        local_offset++;
    }

    // 2. Calcular la base para las funciones hijas (Overlay)
    // El espacio de los hijos empieza DESPUÉS de las variables de esta función.
    int children_base = current_base + node.local_size;

    // Rastrear el uso máximo de stack para reservar la memoria adecuada
    if (children_base > max_stack_usage) {
        max_stack_usage = children_base;
    }

    // Procesar hijos
    for (const auto& callee : node.callees) {
        if (call_graph.count(callee)) {
            calculate_offsets(callee, children_base);
        }
    }

    node.visited = false;
}