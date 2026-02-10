#include "Optimizer.h"
#include <variant>
#include <map>
#include <set>
#include <algorithm>

tacky::Program Optimizer::optimize(const tacky::Program& program) {
    tacky::Program optimized = program;
    for (auto& func : optimized.functions) {
        optimize_function(func);
    }
    return optimized;
}

void Optimizer::optimize_function(tacky::Function& func) {
    for (int i = 0; i < 10; ++i) { // Fixed number of passes for simplicity
        fold_constants(func);
        eliminate_dead_code(func);
    }
}

static std::optional<int> get_constant(const tacky::Val& val) {
    if (auto c = std::get_if<tacky::Constant>(&val)) {
        return c->value;
    }
    return std::nullopt;
}

void Optimizer::fold_constants(tacky::Function& func) {
    std::map<std::string, int> temp_constants;

    for (auto& instr : func.body) {
        // Constant Propagation for Temporaries
        std::visit([&](auto&& arg) {
            using T = std::decay_t<decltype(arg)>;
            if constexpr (requires { arg.src; }) {
                if (auto t = std::get_if<tacky::Temporary>(&arg.src)) {
                    if (temp_constants.contains(t->name)) {
                        arg.src = tacky::Constant{temp_constants[t->name]};
                    }
                }
            }
            if constexpr (requires { arg.src1; }) {
                if (auto t = std::get_if<tacky::Temporary>(&arg.src1)) {
                    if (temp_constants.contains(t->name)) {
                        arg.src1 = tacky::Constant{temp_constants[t->name]};
                    }
                }
            }
            if constexpr (requires { arg.src2; }) {
                if (auto t = std::get_if<tacky::Temporary>(&arg.src2)) {
                    if (temp_constants.contains(t->name)) {
                        arg.src2 = tacky::Constant{temp_constants[t->name]};
                    }
                }
            }
            if constexpr (std::is_same_v<T, tacky::Return>) {
                if (auto t = std::get_if<tacky::Temporary>(&arg.value)) {
                    if (temp_constants.contains(t->name)) {
                        arg.value = tacky::Constant{temp_constants[t->name]};
                    }
                }
            }
            if constexpr (std::is_same_v<T, tacky::JumpIfZero>) {
                if (auto t = std::get_if<tacky::Temporary>(&arg.condition)) {
                    if (temp_constants.contains(t->name)) {
                        arg.condition = tacky::Constant{temp_constants[t->name]};
                    }
                }
            }
            if constexpr (std::is_same_v<T, tacky::JumpIfNotZero>) {
                if (auto t = std::get_if<tacky::Temporary>(&arg.condition)) {
                    if (temp_constants.contains(t->name)) {
                        arg.condition = tacky::Constant{temp_constants[t->name]};
                    }
                }
            }
        }, instr);

        if (auto* binary = std::get_if<tacky::Binary>(&instr)) {
            auto c1 = get_constant(binary->src1);
            auto c2 = get_constant(binary->src2);
            if (c1 && c2) {
                int result = 0;
                bool foldable = true;
                switch (binary->op) {
                    case tacky::BinaryOp::Add: result = *c1 + *c2; break;
                    case tacky::BinaryOp::Sub: result = *c1 - *c2; break;
                    case tacky::BinaryOp::Mul: result = *c1 * *c2; break;
                    case tacky::BinaryOp::Div: if (*c2 != 0) result = *c1 / *c2; else foldable = false; break;
                    case tacky::BinaryOp::Mod: if (*c2 != 0) result = *c1 % *c2; else foldable = false; break;
                    case tacky::BinaryOp::Equal: result = (*c1 == *c2); break;
                    case tacky::BinaryOp::NotEqual: result = (*c1 != *c2); break;
                    case tacky::BinaryOp::LessThan: result = (*c1 < *c2); break;
                    case tacky::BinaryOp::LessEqual: result = (*c1 <= *c2); break;
                    case tacky::BinaryOp::GreaterThan: result = (*c1 > *c2); break;
                    case tacky::BinaryOp::GreaterEqual: result = (*c1 >= *c2); break;
                    case tacky::BinaryOp::BitAnd: result = *c1 & *c2; break;
                    case tacky::BinaryOp::BitOr:  result = *c1 | *c2; break;
                    case tacky::BinaryOp::BitXor: result = *c1 ^ *c2; break;
                    case tacky::BinaryOp::LShift: result = *c1 << *c2; break;
                    case tacky::BinaryOp::RShift: result = *c1 >> *c2; break;
                    default: foldable = false; break;
                }
                if (foldable) {
                    instr = tacky::Copy{tacky::Constant{result}, binary->dst};
                    if (auto t = std::get_if<tacky::Temporary>(&binary->dst)) {
                        temp_constants[t->name] = result;
                    }
                }
            }
        } else if (auto* unary = std::get_if<tacky::Unary>(&instr)) {
            auto c = get_constant(unary->src);
            if (c) {
                int result = 0;
                bool foldable = true;
                switch (unary->op) {
                    case tacky::UnaryOp::Neg: result = -(*c); break;
                    case tacky::UnaryOp::Not: result = !(*c); break;
                    case tacky::UnaryOp::BitNot: result = ~(*c); break;
                    default: foldable = false; break;
                }
                if (foldable) {
                    instr = tacky::Copy{tacky::Constant{result}, unary->dst};
                    if (auto t = std::get_if<tacky::Temporary>(&unary->dst)) {
                        temp_constants[t->name] = result;
                    }
                }
            }
        } else if (auto* copy = std::get_if<tacky::Copy>(&instr)) {
            if (auto src_c = get_constant(copy->src)) {
                if (auto t = std::get_if<tacky::Temporary>(&copy->dst)) {
                    temp_constants[t->name] = *src_c;
                }
            }
        }
    }
}

void Optimizer::eliminate_dead_code(tacky::Function& func) {
    // Simple DCE: remove Copy/Binary/Unary/Call if dst is a Temporary and never used.
    // Note: Call might have side effects, so we should be careful. 
    // But for now let's focus on Temporaries which are strictly internal.
    
    std::set<std::string> used_temps;
    auto register_use = [&](const tacky::Val& val) {
        if (auto t = std::get_if<tacky::Temporary>(&val)) {
            used_temps.insert(t->name);
        }
    };

    for (const auto& instr : func.body) {
        std::visit([&](auto&& arg) {
            using T = std::decay_t<decltype(arg)>;
            if constexpr (std::is_same_v<T, tacky::Return>) register_use(arg.value);
            else if constexpr (std::is_same_v<T, tacky::Unary>) register_use(arg.src);
            else if constexpr (std::is_same_v<T, tacky::Binary>) {
                register_use(arg.src1);
                register_use(arg.src2);
            }
            else if constexpr (std::is_same_v<T, tacky::Copy>) register_use(arg.src);
            else if constexpr (std::is_same_v<T, tacky::JumpIfZero>) register_use(arg.condition);
            else if constexpr (std::is_same_v<T, tacky::JumpIfNotZero>) register_use(arg.condition);
            else if constexpr (std::is_same_v<T, tacky::Call>) {
                for (const auto& arg_val : arg.args) register_use(arg_val);
            }
            else if constexpr (std::is_same_v<T, tacky::BitCheck>) register_use(arg.source);
            else if constexpr (std::is_same_v<T, tacky::BitWrite>) {
                register_use(arg.target);
                register_use(arg.src);
            }
            else if constexpr (std::is_same_v<T, tacky::BitSet>) register_use(arg.target);
            else if constexpr (std::is_same_v<T, tacky::BitClear>) register_use(arg.target);
        }, instr);
    }

    auto is_dead = [&](const tacky::Instruction& instr) {
        return std::visit([&](auto&& arg) -> bool {
            using T = std::decay_t<decltype(arg)>;
            const tacky::Val* dst_ptr = nullptr;
            if constexpr (requires { arg.dst; }) {
                if constexpr (!std::is_same_v<T, tacky::Call>) {
                    dst_ptr = &arg.dst;
                }
            }
            
            if (dst_ptr) {
                if (auto t = std::get_if<tacky::Temporary>(dst_ptr)) {
                    return used_temps.find(t->name) == used_temps.end();
                }
            }
            return false;
        }, instr);
    };

    func.body.erase(std::remove_if(func.body.begin(), func.body.end(), is_dead), func.body.end());
}
